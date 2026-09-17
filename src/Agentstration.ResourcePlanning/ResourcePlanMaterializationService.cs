using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.ResourcePlanning;

public sealed record ResourcePlanningMaterializationOptions
{
    public string MaterializerVersion { get; init; } = "1.1.0";
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}

public interface IResourcePlanningStateReader
{
    Task<CurrentResourceEvidence?> GetAsync(PlannedResourceDocument resource, CancellationToken cancellationToken);
    Task<CurrentResourceEvidence?> ResolveBindingAsync(ResourcePlanScope scope, string kind, ResourceReference reference, CancellationToken cancellationToken);
}

public sealed class ResourcePlanMaterializationService(
    ResourcePlanService plans,
    IResourcePlanContentValidator validator,
    IResourcePlanningStateReader stateReader,
    ResourcePlanningMaterializationOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ResourcePlanMaterialization> MaterializeAsync(
        ResourcePlanScope scope,
        ResourcePlanId planId,
        CancellationToken cancellationToken) => await MaterializeAsync(scope, planId, new([]), cancellationToken);

    public async Task<ResourcePlanMaterialization> MaterializeAsync(
        ResourcePlanScope scope,
        ResourcePlanId planId,
        ResourcePlanMaterializationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Bindings is null) throw new ArgumentException("Bindings are required.", nameof(request));
        var snapshot = await plans.GetAsync(scope, planId, cancellationToken) ?? throw new ResourcePlanNotFoundException(planId);
        if (snapshot.Value.Status is not ResourcePlanStatus.Ready and not ResourcePlanStatus.Materialized and not ResourcePlanStatus.Validated)
            throw new ResourcePlanLifecycleException("resource_plan_not_ready", "A Resource Plan must be ready before materialization.");
        var validation = validator.Validate(snapshot.Value.Content);
        if (!validation.IsValid) throw new ResourcePlanValidationException(validation.Issues);
        var functional = FunctionalResourcePlanSerializer.Deserialize(snapshot.Value.Content);
        var diagnostics = new List<ResourcePlanMaterializationDiagnostic>();
        if (request.Bindings.Any(value => value is null || string.IsNullOrWhiteSpace(value.LogicalId)))
            throw new ArgumentException("Every binding requires a role logical ID.", nameof(request));
        var bindings = request.Bindings.ToDictionary(value => value.LogicalId, StringComparer.OrdinalIgnoreCase);
        var resolvedBindings = new Dictionary<string, ResourcePlanAgentBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in request.Bindings)
            if (!functional.Roles.Any(role => string.Equals(role.LogicalId, binding.LogicalId, StringComparison.OrdinalIgnoreCase)))
                diagnostics.Add(new("planning_binding_unknown_role", $"bindings[{binding.LogicalId}]", "The binding does not match a planned role."));
        var bindingEvidence = new List<ResourcePlanResolvedBinding>();
        foreach (var role in functional.Roles)
        {
            if (!bindings.TryGetValue(role.LogicalId, out var binding))
            {
                diagnostics.Add(new("planning_binding_required", $"roles[{role.LogicalId}]", "Select a Model Profile and a Runtime Profile for this role."));
                continue;
            }
            var model = await CheckBindingAsync(scope, role.LogicalId, "modelProfile", ModelResourceKinds.ModelProfile, binding.ModelProfile, diagnostics, bindingEvidence, cancellationToken);
            var runtime = await CheckBindingAsync(scope, role.LogicalId, "runtimeProfile", RuntimeProfileResourceKinds.RuntimeProfile, binding.RuntimeProfile, diagnostics, bindingEvidence, cancellationToken);
            if (model is not null && runtime is not null) resolvedBindings.Add(role.LogicalId, new(role.LogicalId, model, runtime));
        }
        var candidates = CreateCandidates(snapshot.Value, functional, resolvedBindings, diagnostics);
        var proposals = new List<MaterializedResourceProposal>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var current = await stateReader.GetAsync(candidate.Resource, cancellationToken);
            var proposedDigest = Digest(candidate.Resource);
            var operation = current is null
                ? ResourcePlanProposedOperation.Create
                : string.Equals(current.Digest, proposedDigest, StringComparison.Ordinal)
                    ? ResourcePlanProposedOperation.NoOp
                    : ResourcePlanProposedOperation.Update;
            proposals.Add(new(candidate.LogicalId, candidate.Resource, operation, current, candidate.DependsOn, proposedDigest));
        }
        var ordered = TopologicalOrder(proposals, diagnostics);
        var digest = Digest(new
        {
            planId = planId.Value,
            planRevision = snapshot.Value.Revision,
            contractVersion = snapshot.Value.Content.SchemaVersion,
            materializerVersion = options.MaterializerVersion,
            bindings = bindingEvidence.OrderBy(value => value.LogicalId, StringComparer.Ordinal).ThenBy(value => value.Field, StringComparer.Ordinal),
            proposals = ordered.Select(value => new { value.LogicalId, value.Operation, value.ProposedDigest, value.DependsOn }),
            diagnostics
        });
        return new(planId, snapshot.Value.Revision, snapshot.Value.Content.SchemaVersion, options.MaterializerVersion, scope, ordered, diagnostics, digest, bindingEvidence);
    }

    private async Task<ResourceReference?> CheckBindingAsync(ResourcePlanScope scope, string logicalId, string field, string kind, ResourceReference? reference,
        List<ResourcePlanMaterializationDiagnostic> diagnostics, List<ResourcePlanResolvedBinding> evidence, CancellationToken cancellationToken)
    {
        var path = $"bindings[{logicalId}].{field}";
        if (reference is null || string.IsNullOrWhiteSpace(reference.Name))
        {
            diagnostics.Add(new("planning_binding_required", path, $"Select a {field} for this role."));
            return null;
        }
        try
        {
            var requested = reference with { Namespace = reference.Namespace ?? options.Namespace };
            var resolved = await stateReader.ResolveBindingAsync(scope, kind, requested, cancellationToken);
            if (resolved is null)
            {
                diagnostics.Add(new("planning_binding_not_found", path, $"The selected {field} '{reference.Name}' is not visible in this Workspace."));
                return null;
            }
            if (resolved.Document.TryGetProperty("status", out var statusValue)
                && statusValue.Deserialize<ResourceStatus>(JsonOptions)?.ProvisioningState != ProvisioningState.Succeeded)
            {
                diagnostics.Add(new("planning_binding_unavailable", path, $"The selected {field} '{reference.Name}' is not provisioned successfully."));
                return null;
            }
            var exactScope = resolved.Document.TryGetProperty("scopeRef", out var scopeValue)
                ? scopeValue.Deserialize<ResourceScopeRef>(JsonOptions) : requested.ScopeRef;
            var exactReference = requested with { ScopeRef = exactScope };
            evidence.Add(new(logicalId, field, exactReference, resolved.Uid, resolved.Revision, resolved.ETag, resolved.Digest));
            return exactReference;
        }
        catch (ResourceReferenceOutsideScopeException)
        {
            diagnostics.Add(new("planning_binding_outside_scope", path, "The selected profile is outside this Workspace's visible scopes."));
            return null;
        }
        catch (ResourceReferenceAmbiguousException)
        {
            diagnostics.Add(new("planning_binding_ambiguous", path, "The selected profile is ambiguous; choose its exact scope."));
            return null;
        }
        catch (ModelProfileValidationException exception)
        {
            diagnostics.Add(new("planning_binding_incompatible", path, exception.Message));
            return null;
        }
    }

    private List<Candidate> CreateCandidates(ResourcePlan plan, FunctionalResourcePlanV1 functional, IReadOnlyDictionary<string, ResourcePlanAgentBinding> bindings, List<ResourcePlanMaterializationDiagnostic> diagnostics)
    {
        var result = new List<Candidate>();
        var scopeRef = ResourceScopeRef.Workspace(plan.Scope.WorkspaceId.Value);
        var names = functional.Roles.Concat<object>(functional.Workflows).Concat(functional.Integrations).Concat(functional.Experiences)
            .Select(value => value switch
            {
                PlanningRoleIntent item => item.LogicalId,
                PlanningWorkflowIntent item => item.LogicalId,
                PlanningIntegrationIntent item => item.LogicalId,
                PlanningExperienceIntent item => item.LogicalId,
                _ => throw new UnreachableException()
            }).ToDictionary(value => value, StableName, StringComparer.OrdinalIgnoreCase);

        foreach (var role in functional.Roles)
        {
            if (!bindings.TryGetValue(role.LogicalId, out var binding) || binding.ModelProfile is null || binding.RuntimeProfile is null) continue;
            var definition = new AgentProperties
            {
                DisplayName = role.DisplayName,
                Description = role.Purpose,
                Instructions = BuildInstructions(role),
                ModelProfile = binding.ModelProfile,
                RuntimeProfile = binding.RuntimeProfile,
                Behaviors = role.Capabilities.ToArray()
            };
            result.Add(new(role.LogicalId, Document(AgentResourceKinds.Agent, names[role.LogicalId], scopeRef, definition), Dependencies(functional, role.LogicalId)));
        }

        foreach (var workflow in functional.Workflows)
        {
            var participants = workflow.Participants.Select(value => new FlowTargetReference(FlowTargetKind.Agent, names[value], Namespace: options.Namespace)).ToArray();
            if (participants.Length == 0)
            {
                diagnostics.Add(new("planning_workflow_empty", $"workflows[{workflow.LogicalId}]", "A workflow requires at least one participant."));
                continue;
            }
            var spec = CreateFlowDefinition(workflow, participants);
            var definition = new MaterializedFlowDefinition(workflow.DisplayName, workflow.Objective, "1.0.0", true, spec, true, true);
            result.Add(new(workflow.LogicalId, Document(FlowResourceKinds.Flow, names[workflow.LogicalId], scopeRef, definition), workflow.Participants.Concat(Dependencies(functional, workflow.LogicalId)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }

        foreach (var integration in functional.Integrations)
            diagnostics.Add(new("planning_integration_binding_required", $"integrations[{integration.LogicalId}]", $"Integration '{integration.DisplayName}' requires an operator-selected Tool/provider binding before it can be materialized.", ResourcePlanMaterializationSeverity.Warning));

        foreach (var experience in functional.Experiences)
        {
            var definition = new MaterializedEntryDefinition(
                experience.DisplayName,
                experience.Purpose,
                new EntryPresentation { Kind = experience.Interaction == PlanningInteractionStyle.Conversation ? EntryPresentationKind.Conversation : EntryPresentationKind.Prompt },
                new EntryBinding(EntryBindingKind.Flow, names[experience.Workflow], options.Namespace),
                new EntryBehavior(AllowConversation: experience.Interaction == PlanningInteractionStyle.Conversation),
                true);
            result.Add(new(experience.LogicalId, Document(EntryResourceKinds.Entry, names[experience.LogicalId], scopeRef, definition), [experience.Workflow, .. Dependencies(functional, experience.LogicalId)]));
        }
        return result;
    }

    private PlannedResourceDocument Document<T>(string kind, string name, ResourceScopeRef scope, T definition) => new(
        ResourceApiVersions.CoreV1,
        kind,
        scope,
        new ResourceMetadata { Name = name, Namespace = options.Namespace, Annotations = new Dictionary<string, string> { ["agentstration.io/managed-by"] = "resource-planning" } },
        JsonSerializer.SerializeToElement(definition, JsonOptions));

    private static FlowDefinition CreateFlowDefinition(PlanningWorkflowIntent workflow, IReadOnlyList<FlowTargetReference> participants) => workflow.Collaboration switch
    {
        PlanningCollaborationStyle.Individual => new DirectFlowDefinition(participants[0]),
        PlanningCollaborationStyle.Ordered => new OrchestrationFlowDefinition(participants, new SequentialOrchestrationPattern()),
        PlanningCollaborationStyle.Parallel => new OrchestrationFlowDefinition(participants, new ConcurrentOrchestrationPattern()),
        PlanningCollaborationStyle.Delegated => new OrchestrationFlowDefinition(participants, new HandoffOrchestrationPattern(participants[0].Id, participants.Zip(participants.Skip(1), (from, to) => new FlowHandoff(from.Id, to.Id)).ToArray(), Autonomous: true)),
        PlanningCollaborationStyle.Collaborative => new OrchestrationFlowDefinition(participants, new GroupChatOrchestrationPattern()),
        PlanningCollaborationStyle.Adaptive => new OrchestrationFlowDefinition(participants, new MagenticOrchestrationPattern(participants[0])),
        _ => throw new ArgumentOutOfRangeException(nameof(workflow))
    };

    private static IReadOnlyList<string> Dependencies(FunctionalResourcePlanV1 plan, string logicalId) => plan.Dependencies
        .Where(value => string.Equals(value.From, logicalId, StringComparison.OrdinalIgnoreCase))
        .Select(value => value.To).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string BuildInstructions(PlanningRoleIntent role)
    {
        string[] lines =
        [
            role.Purpose,
            "",
            "Responsibilities:",
            .. role.Responsibilities.Select(value => $"- {value}"),
            "",
            "Required capabilities:",
            .. role.Capabilities.Select(value => $"- {value}")
        ];
        return string.Join(Environment.NewLine, lines);
    }

    private static string StableName(string logicalId)
    {
        var value = new string(logicalId.Trim().ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        if (value.Length == 0) value = $"planned-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(logicalId)))[..12]}";
        return value.Length <= 63 ? value : value[..63].TrimEnd('-');
    }

    private static IReadOnlyList<MaterializedResourceProposal> TopologicalOrder(IReadOnlyList<MaterializedResourceProposal> proposals, List<ResourcePlanMaterializationDiagnostic> diagnostics)
    {
        var byId = proposals.ToDictionary(value => value.LogicalId, StringComparer.OrdinalIgnoreCase);
        var result = new List<MaterializedResourceProposal>(proposals.Count);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(MaterializedResourceProposal proposal)
        {
            if (visited.Contains(proposal.LogicalId)) return;
            if (!visiting.Add(proposal.LogicalId))
            {
                diagnostics.Add(new("planning_dependency_cycle", $"dependencies[{proposal.LogicalId}]", "The materialized dependency graph contains a cycle."));
                return;
            }
            foreach (var dependency in proposal.DependsOn.Order(StringComparer.Ordinal)) if (byId.TryGetValue(dependency, out var target)) Visit(target);
            visiting.Remove(proposal.LogicalId);
            visited.Add(proposal.LogicalId);
            result.Add(proposal);
        }
        foreach (var proposal in proposals.OrderBy(value => value.LogicalId, StringComparer.Ordinal)) Visit(proposal);
        return result;
    }

    internal static string Digest<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))}";
    }

    private sealed record Candidate(string LogicalId, PlannedResourceDocument Resource, IReadOnlyList<string> DependsOn);
    private sealed record MaterializedFlowDefinition(string DisplayName, string Description, string Version, bool Enabled, FlowDefinition Spec, bool Publish, bool Activate);
    private sealed record MaterializedEntryDefinition(string DisplayName, string Description, EntryPresentation Presentation, EntryBinding Binding, EntryBehavior Behavior, bool Publish);
}

public sealed class ManagementResourcePlanningStateReader(
    IResourceStore resources,
    IResourceReferenceResolver references,
    IModelProfileReferenceValidator modelProfiles,
    IFlowRepository flows,
    IWorkplaceRepository workplace) : IResourcePlanningStateReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CurrentResourceEvidence?> ResolveBindingAsync(ResourcePlanScope scope, string kind, ResourceReference reference, CancellationToken cancellationToken)
    {
        var consumer = ResourceScopeRef.Workspace(scope.WorkspaceId.Value);
        if (kind == ModelResourceKinds.ModelProfile)
        {
            var stored = await references.ResolveAsync<ModelProfileResource>(reference, ResourceNamespace.Default, kind, consumer, cancellationToken);
            if (stored is not null) await modelProfiles.ValidateAsync(reference, ResourceNamespace.Default, consumer, cancellationToken);
            return stored is null ? null : new(stored.Value.Uid, stored.Value.Generation, stored.ETag, JsonSerializer.SerializeToElement(stored.Value, JsonOptions), ResourcePlanMaterializationService.Digest(stored.Value));
        }
        if (kind == RuntimeProfileResourceKinds.RuntimeProfile)
        {
            var stored = await references.ResolveAsync<RuntimeProfileResource>(reference, ResourceNamespace.Default, kind, consumer, cancellationToken);
            return stored is null ? null : new(stored.Value.Uid, stored.Value.Generation, stored.ETag, JsonSerializer.SerializeToElement(stored.Value, JsonOptions), ResourcePlanMaterializationService.Digest(stored.Value));
        }
        throw new ArgumentException("Unsupported binding kind.", nameof(kind));
    }

    public async Task<CurrentResourceEvidence?> GetAsync(PlannedResourceDocument resource, CancellationToken cancellationToken)
    {
        if (resource.Kind == AgentResourceKinds.Agent)
        {
            var current = await resources.GetExactAsync<AgentResource>(ScopedResourceAddress.Create(resource.ScopeRef, resource.Metadata.Namespace, resource.Kind, resource.Metadata.Name), cancellationToken);
            return current is null ? null : Evidence(current.Value.Uid, current.Value.Generation, current.ETag, resource with { Definition = JsonSerializer.SerializeToElement(current.Value.Definition, JsonOptions) });
        }
        if (resource.Kind == FlowResourceKinds.Flow)
        {
            var current = await flows.GetAsync(new WorkspaceId(resource.ScopeRef.TargetId!.Value), new FlowId(resource.Metadata.Name, resource.Metadata.Namespace), cancellationToken);
            if (current is null) return null;
            var published = await flows.GetVersionAsync(new WorkspaceId(resource.ScopeRef.TargetId!.Value), new FlowId(resource.Metadata.Name, resource.Metadata.Namespace), current.Value.Version, cancellationToken);
            var definition = new { displayName = current.Value.DisplayName ?? current.Value.Name, description = current.Value.Description ?? string.Empty, version = current.Value.Version, enabled = current.Value.Enabled, spec = current.Value.Definition, publish = published is not null, activate = current.Value.ActiveVersion == current.Value.Version };
            return Evidence(null, 0, current.ETag, resource with { Definition = JsonSerializer.SerializeToElement(definition, JsonOptions) });
        }
        if (resource.Kind == EntryResourceKinds.Entry)
        {
            var workspaceId = new WorkspaceId(resource.ScopeRef.TargetId!.Value);
            var current = await workplace.GetEntryDraftAsync(workspaceId, new EntryId(resource.Metadata.Name, resource.Metadata.Namespace), cancellationToken);
            if (current is null) return null;
            var definition = new { displayName = current.DisplayName, description = current.Description ?? string.Empty, presentation = current.Presentation, binding = current.Binding, behavior = current.Behavior, publish = current.PublishedBinding == current.Binding };
            return Evidence(null, current.Revision, current.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), resource with { Definition = JsonSerializer.SerializeToElement(definition, JsonOptions) });
        }
        return null;
    }

    private static CurrentResourceEvidence Evidence(Guid? uid, long revision, string? etag, PlannedResourceDocument document)
    {
        var json = JsonSerializer.SerializeToElement(document, JsonOptions);
        return new(uid, revision, etag, json, ResourcePlanMaterializationService.Digest(document));
    }
}
