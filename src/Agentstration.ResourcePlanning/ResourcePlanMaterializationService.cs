using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.ResourceManagement;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.ResourcePlanning;

public sealed record ResourcePlanningMaterializationOptions
{
    public string MaterializerVersion { get; init; } = "1.0.0";
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
    public ResourceReference DefaultModelProfile { get; init; } = new("default");
    public ResourceReference DefaultRuntimeProfile { get; init; } = new("maf-builtin");
}

public interface IResourcePlanningStateReader
{
    Task<CurrentResourceEvidence?> GetAsync(PlannedResourceDocument resource, CancellationToken cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var snapshot = await plans.GetAsync(scope, planId, cancellationToken) ?? throw new ResourcePlanNotFoundException(planId);
        if (snapshot.Value.Status is not ResourcePlanStatus.Ready and not ResourcePlanStatus.Materialized and not ResourcePlanStatus.Validated)
            throw new ResourcePlanLifecycleException("resource_plan_not_ready", "A Resource Plan must be ready before materialization.");
        var validation = validator.Validate(snapshot.Value.Content);
        if (!validation.IsValid) throw new ResourcePlanValidationException(validation.Issues);
        var functional = FunctionalResourcePlanSerializer.Deserialize(snapshot.Value.Content);
        var diagnostics = new List<ResourcePlanMaterializationDiagnostic>();
        var candidates = CreateCandidates(snapshot.Value, functional, diagnostics);
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
            proposals = ordered.Select(value => new { value.LogicalId, value.Operation, value.ProposedDigest, value.DependsOn }),
            diagnostics
        });
        return new(planId, snapshot.Value.Revision, snapshot.Value.Content.SchemaVersion, options.MaterializerVersion, scope, ordered, diagnostics, digest);
    }

    private List<Candidate> CreateCandidates(ResourcePlan plan, FunctionalResourcePlanV1 functional, List<ResourcePlanMaterializationDiagnostic> diagnostics)
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
            var definition = new AgentProperties
            {
                DisplayName = role.DisplayName,
                Description = role.Purpose,
                Instructions = BuildInstructions(role),
                ModelProfile = options.DefaultModelProfile,
                RuntimeProfile = options.DefaultRuntimeProfile,
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
        PlanningCollaborationStyle.Single => new DirectFlowDefinition(participants[0]),
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

    private static string BuildInstructions(PlanningRoleIntent role) => string.Join(Environment.NewLine,
        new[] { role.Purpose, "", "Responsibilities:", .. role.Responsibilities.Select(value => $"- {value}"), "", "Required capabilities:", .. role.Capabilities.Select(value => $"- {value}") });

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
    IFlowRepository flows,
    IWorkplaceRepository workplace) : IResourcePlanningStateReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
            var definition = new { displayName = current.Value.DisplayName ?? current.Value.Name, description = current.Value.Description ?? string.Empty, version = current.Value.Version, enabled = current.Value.Enabled, spec = current.Value.Definition, publish = current.Value.ActiveVersion is not null, activate = current.Value.ActiveVersion is not null };
            return Evidence(null, 0, current.ETag, resource with { Definition = JsonSerializer.SerializeToElement(definition, JsonOptions) });
        }
        if (resource.Kind == EntryResourceKinds.Entry)
        {
            var workspaceId = new WorkspaceId(resource.ScopeRef.TargetId!.Value);
            var current = await workplace.GetEntryDraftAsync(workspaceId, new EntryId(resource.Metadata.Name, resource.Metadata.Namespace), cancellationToken);
            if (current is null) return null;
            var definition = new { displayName = current.DisplayName, description = current.Description ?? string.Empty, presentation = current.Presentation, binding = current.Binding, behavior = current.Behavior, publish = current.PublishedBinding is not null };
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
