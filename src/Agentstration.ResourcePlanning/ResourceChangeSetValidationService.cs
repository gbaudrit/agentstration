using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.ResourcePlanning;

public interface IPlannedResourceValidator
{
    bool Supports(string kind);
    Task<IReadOnlyList<ResourceChangeSetValidationIssue>> ValidateAsync(ResourceChange change, ResourcePlanScope scope, CancellationToken cancellationToken);
}

public sealed class ResourceChangeSetValidationService(
    ResourceChangeSetService changeSets,
    IResourceChangeSetRepository repository,
    IResourcePlanningStateReader stateReader,
    IEnumerable<IPlannedResourceValidator> resourceValidators,
    TimeProvider timeProvider)
{
    public async Task<ResourceChangeSetValidation> ValidateAsync(ResourcePlanScope scope, ResourceChangeSetId id, Guid actorPrincipalId, CancellationToken cancellationToken)
    {
        if (actorPrincipalId == Guid.Empty) throw new ArgumentException("An actor Principal is required.", nameof(actorPrincipalId));
        var snapshot = await changeSets.GetAsync(scope, id, cancellationToken);
        var issues = new List<ResourceChangeSetValidationIssue>();
        ValidateStructure(snapshot.Value, issues);
        foreach (var change in snapshot.Value.Changes)
        {
            var validator = resourceValidators.SingleOrDefault(value => value.Supports(change.Proposed.Kind));
            if (validator is null)
                issues.Add(Error("resource_change_kind_unsupported", $"changes[{change.Order}]", $"Resource kind '{change.Proposed.Kind}' has no planning validator."));
            else
                issues.AddRange(await validator.ValidateAsync(change, scope, cancellationToken));
            await ValidateCurrentStateAsync(change, issues, cancellationToken);
        }
        var readiness = issues.Any(value => value.Severity == ResourceChangeSetValidationSeverity.Error) ? ResourceChangeSetReadiness.Blocked : ResourceChangeSetReadiness.Ready;
        var validation = new ResourceChangeSetValidation(Guid.NewGuid(), id, snapshot.Value.Digest, snapshot.Value.PlanId, snapshot.Value.PlanRevision, scope, readiness, issues, actorPrincipalId, timeProvider.GetUtcNow());
        await repository.AddValidationAsync(validation, cancellationToken);
        if (readiness == ResourceChangeSetReadiness.Ready && snapshot.Value.Status != ResourceChangeSetStatus.Validated)
            _ = await repository.UpdateAsync(snapshot.Value with { Status = ResourceChangeSetStatus.Validated }, snapshot.ETag, cancellationToken);
        return validation;
    }

    public Task<IReadOnlyList<ResourceChangeSetValidation>> ListAsync(ResourcePlanScope scope, ResourceChangeSetId id, CancellationToken cancellationToken) =>
        repository.ListValidationsAsync(scope, id, cancellationToken);

    private static void ValidateStructure(ResourceChangeSet changeSet, List<ResourceChangeSetValidationIssue> issues)
    {
        var expectedScope = ResourceScopeRef.Workspace(changeSet.Scope.WorkspaceId.Value);
        var logicalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < changeSet.Changes.Count; index++)
        {
            var change = changeSet.Changes[index];
            if (change.Order != index) issues.Add(Error("resource_change_order_invalid", $"changes[{index}].order", "Change order must be contiguous and dependency-safe."));
            if (!logicalIds.Add(change.LogicalId)) issues.Add(Error("resource_change_duplicate", $"changes[{index}].logicalId", $"Logical ID '{change.LogicalId}' is duplicated."));
            if (change.Proposed.ScopeRef != expectedScope) issues.Add(Error("resource_change_scope_invalid", $"changes[{index}].proposed.scopeRef", "A proposed Resource must remain in the Resource Plan's owning Workspace."));
        }
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changeSet.Changes)
            positions.TryAdd(change.LogicalId, change.Order);
        foreach (var change in changeSet.Changes)
            foreach (var dependency in change.DependsOn)
            {
                if (!positions.TryGetValue(dependency, out var dependencyOrder)) issues.Add(Error("resource_change_dependency_missing", $"changes[{change.Order}].dependsOn", $"Dependency '{dependency}' is not in the change set."));
                else if (dependencyOrder >= change.Order) issues.Add(Error("resource_change_dependency_order_invalid", $"changes[{change.Order}].dependsOn", $"Dependency '{dependency}' must precede '{change.LogicalId}'."));
            }
    }

    private async Task ValidateCurrentStateAsync(ResourceChange change, List<ResourceChangeSetValidationIssue> issues, CancellationToken cancellationToken)
    {
        var current = await stateReader.GetAsync(change.Proposed, cancellationToken);
        if (change.Operation == ResourceChangeOperation.Create && current is not null)
        {
            issues.Add(Error("resource_change_create_conflict", $"changes[{change.Order}]", "The Resource now exists; rematerialize the Resource Plan."));
            return;
        }
        if (change.Operation is ResourceChangeOperation.Update or ResourceChangeOperation.Delete or ResourceChangeOperation.NoOp)
        {
            if (current is null || change.Current is null)
            {
                issues.Add(Error("resource_change_current_missing", $"changes[{change.Order}]", "The pinned current Resource no longer exists; rematerialize the Resource Plan."));
                return;
            }
            if (!string.Equals(current.ETag, change.Current.ETag, StringComparison.Ordinal) || !string.Equals(current.Digest, change.Current.Digest, StringComparison.Ordinal))
                issues.Add(Error("resource_change_stale", $"changes[{change.Order}].current", "The current Resource changed after materialization; rematerialize the Resource Plan."));
        }
    }

    private static ResourceChangeSetValidationIssue Error(string code, string path, string message) => new(code, path, message);
}

public sealed class CanonicalPlannedResourceValidator(AgentManagementService agents) : IPlannedResourceValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public bool Supports(string kind) => kind is AgentResourceKinds.Agent or FlowResourceKinds.Flow or EntryResourceKinds.Entry;

    public async Task<IReadOnlyList<ResourceChangeSetValidationIssue>> ValidateAsync(ResourceChange change, ResourcePlanScope scope, CancellationToken cancellationToken)
    {
        try
        {
            if (change.Proposed.Kind == AgentResourceKinds.Agent)
            {
                var definition = change.Proposed.Definition.Deserialize<AgentProperties>(JsonOptions) ?? throw new JsonException("Agent definition is empty.");
                try
                {
                    await agents.ValidateForCreateAsync(new AgentResource { ApiVersion = change.Proposed.ApiVersion, Kind = change.Proposed.Kind, ScopeRef = change.Proposed.ScopeRef, Metadata = change.Proposed.Metadata, Definition = definition }, cancellationToken);
                }
                catch (ModelProfileValidationException exception)
                {
                    var profile = definition.ModelProfile.Resolve(change.Proposed.Metadata.Namespace, ModelResourceKinds.ModelProfile);
                    return [new("resource_change_model_profile_invalid", $"changes[{change.Order}].proposed.definition.modelProfile",
                        $"Agent '{change.Proposed.Metadata.Name}' references ModelProfile '{profile.Namespace}/{profile.Name}': {exception.Message} Create or repair that ModelProfile, then revalidate the ChangeSet.")];
                }
            }
            else if (change.Proposed.Kind == FlowResourceKinds.Flow)
            {
                var definition = change.Proposed.Definition;
                var spec = definition.GetProperty("spec").Deserialize<FlowDefinition>(JsonOptions) ?? throw new JsonException("Flow specification is empty.");
                FlowValidator.Validate(new(scope.WorkspaceId, new(change.Proposed.Metadata.Name, change.Proposed.Metadata.Namespace), change.Proposed.Metadata.Name,
                    definition.GetProperty("description").GetString(), definition.GetProperty("version").GetString() ?? string.Empty,
                    definition.GetProperty("enabled").GetBoolean(), null, spec, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                    definition.GetProperty("displayName").GetString()));
            }
            else
            {
                var definition = change.Proposed.Definition;
                var draft = new EntryDraft
                {
                    WorkspaceId = scope.WorkspaceId,
                    Id = new(change.Proposed.Metadata.Name, change.Proposed.Metadata.Namespace),
                    Name = change.Proposed.Metadata.Name,
                    DisplayName = definition.GetProperty("displayName").GetString() ?? change.Proposed.Metadata.Name,
                    Description = definition.GetProperty("description").GetString(),
                    Presentation = definition.GetProperty("presentation").Deserialize<EntryPresentation>(JsonOptions) ?? throw new JsonException("Entry presentation is empty."),
                    Binding = definition.GetProperty("binding").Deserialize<EntryBinding>(JsonOptions) ?? throw new JsonException("Entry binding is empty."),
                    Behavior = definition.GetProperty("behavior").Deserialize<EntryBehavior>(JsonOptions) ?? new(),
                    UpdatedAt = DateTimeOffset.UnixEpoch
                };
                WorkplaceValidation.Validate(draft);
            }
            return [];
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or FlowValidationException or WorkValidationException
            or AgentDefinitionValidationException or ResourceNotFoundException or ResourceReferenceOutsideScopeException
            or ResourceReferenceAmbiguousException or ResourceScopePolicyException)
        {
            return [new("resource_change_canonical_invalid", $"changes[{change.Order}].proposed", exception.Message)];
        }
    }
}
