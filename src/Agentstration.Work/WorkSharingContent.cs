using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Work;

/// <summary>The portable documents contain declarations and observations, never local execution state.</summary>
public static class WorkSharingContent
{
    private static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 24 };

    public static Result<WorkSharingChange> Normalize(WorkSharingChange change)
    {
        if (change is null || !WorkSharingFormat.ValidKey(change.Key) || change.ExpectedVersion < 0
            || change.Content.ValueKind != JsonValueKind.Object
            || JsonSerializer.SerializeToUtf8Bytes(change.Content).Length > WorkSharingFormat.MaximumRecordBytes)
            return Failure();
        try
        {
            object? normalized = change.Key.Domain switch
            {
                WorkSharingDomain.Plan => Plan(change.Content.Deserialize<WorkSharingPlan>(Json)),
                WorkSharingDomain.ProjectMetadata => Metadata(change.Content.Deserialize<WorkSharingProjectMetadata>(Json)),
                WorkSharingDomain.Contribution => Contribution(change.Content.Deserialize<WorkSharingContribution>(Json)),
                WorkSharingDomain.Usage => Usage(change.Content.Deserialize<WorkSharingUsage>(Json)),
                WorkSharingDomain.BootstrapProfile => Bootstrap(change.Content.Deserialize<WorkSharingBootstrapProfile>(Json)),
                _ => null
            };
            if (normalized is null) return Failure();
            if (normalized is WorkSharingPlan plan && change.Key.Name != plan.PlanId.ToString()
                || normalized is WorkSharingContribution contribution && change.Key.Name != contribution.Id.ToString("D")
                || normalized is WorkSharingUsage usage && change.Key.Name != usage.Id.ToString("D")
                || normalized is WorkSharingBootstrapProfile profile && change.Key.Name != profile.Name) return Failure();
            return Result<WorkSharingChange>.Success(change with { Content = JsonSerializer.SerializeToElement(normalized, normalized.GetType(), Json) });
        }
        catch (JsonException) { return Failure(); }
    }

    public static Result<WorkSharingPlan> NormalizeDraft(WorkSharingPlan plan) => Plan(plan, false) is { } normalized
        ? Result<WorkSharingPlan>.Success(normalized) : Result<WorkSharingPlan>.Failure("work_data_plan_invalid");

    private static WorkSharingPlan? Plan(WorkSharingPlan? plan, bool requireAllocation = true)
    {
        if (plan is null || !ValidIssue(plan.Issue) || plan.PlanId.Value == Guid.Empty || plan.Definition is null || plan.Trees is not { Count: > 0 and <= 100 }
            || plan.Allocation is null || plan.Allocation.Count > 100) return null;
        var definition = plan.Definition.Validate();
        if (!definition.IsSuccess || plan.Trees.Any(tree => tree is null || !tree.Validate().IsSuccess)) return null;
        var trees = plan.Trees.Select(tree => tree.Validate().Value!).OrderBy(tree => tree.RootTaskId.Value).ToArray();
        var tasks = trees.SelectMany(tree => tree.Tasks).ToArray();
        if (tasks.Length > 100 || tasks.Select(task => task.Id).Distinct().Count() != tasks.Length
            || definition.Value!.TaskIds.Any(id => !tasks.Any(task => task.Id == id))) return null;
        var atomic = tasks.Where(task => task.Kind == WorkTaskKind.Atomic && definition.Value.TaskIds.Contains(task.Id)).Select(task => task.Id).OrderBy(id => id.Value);
        if (plan.Allocation.Any(allocation => allocation is null || allocation.ParticipantId.Value == Guid.Empty)
            || (requireAllocation ? !atomic.SequenceEqual(plan.Allocation.Select(allocation => allocation.TaskId).OrderBy(id => id.Value)) : plan.Allocation.Count != 0)) return null;
        // The same combined dependency/hierarchy graph used by WorkPlan validation must be acyclic.
        var required = tasks.Where(task => task.ParentTaskId is { } parent && definition.Value.TaskIds.Contains(parent) && !task.IsOptional).ToArray();
        if (required.Any(task => !definition.Value.TaskIds.Contains(task.Id))) return null;
        var hierarchyEdges = required.Select(task => new WorkTaskDependency(task.ParentTaskId!.Value, task.Id));
        if (WorkPlanDefinition.HasCycle(definition.Value.TaskIds.ToHashSet(), [.. definition.Value.Dependencies, .. hierarchyEdges])) return null;
        return plan with { Definition = definition.Value, Trees = trees, Allocation = plan.Allocation.OrderBy(value => value.TaskId.Value).ToArray() };
    }
    public static bool ValidIssue(WorkSharingIssueReference? issue) => issue is null || WorkProjectDefinition.ValidName(issue.Provider)
        && issue.Repository is { Length: > 0 and <= 512 } && !issue.Repository.Any(char.IsControl) && !issue.Repository.Contains('@', StringComparison.Ordinal)
        && issue.TaskId is { Length: > 0 and <= 128 } && issue.TaskId[0] is >= '1' and <= '9' && issue.TaskId.All(char.IsAsciiDigit)
        && Prefix(issue.PlanLabelPrefix) && Prefix(issue.ParticipantLabelPrefix)
        && !issue.PlanLabelPrefix.StartsWith(issue.ParticipantLabelPrefix, StringComparison.OrdinalIgnoreCase)
        && !issue.ParticipantLabelPrefix.StartsWith(issue.PlanLabelPrefix, StringComparison.OrdinalIgnoreCase);
    private static bool Prefix(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
    private static WorkSharingProjectMetadata? Metadata(WorkSharingProjectMetadata? value) => value is not null
        && !string.IsNullOrWhiteSpace(value.DisplayName) && value.DisplayName.Length <= 200 && value.Description?.Length is not > 4_000
        && value.Participants is { Count: > 0 and <= 64 } && value.Participants.All(id => id.Value != Guid.Empty)
        && value.Participants.Distinct().Count() == value.Participants.Count
        ? value with { Participants = value.Participants.OrderBy(id => id.Value).ToArray() } : null;
    private static WorkSharingContribution? Contribution(WorkSharingContribution? value) => value is not null && value.Id != Guid.Empty
        && ValidOptionalIds(value.PlanId, value.TaskId) && WorkProjectDefinition.ValidName(value.Kind)
        && value.Evidence is { Length: > 0 and <= 16_000 } && value.ArtifactReferences is { Count: <= 32 }
        && value.ArtifactReferences.All(reference => reference is { Length: > 0 and <= 2_000 } && !reference.Any(char.IsControl)) ? value : null;
    private static WorkSharingUsage? Usage(WorkSharingUsage? value) => value is not null && value.Id != Guid.Empty
        && ValidOptionalIds(value.PlanId, value.TaskId) && value.Counters is { Count: <= 32 }
        && (value.Accounting is null ? value.Counters.Count > 0 : value.Counters.Count == 0 && WorkUsageFormat.Valid(value.Accounting)
            && value.Accounting.Id.Value == value.Id && value.Accounting.PlanId == value.PlanId && value.Accounting.TaskId == value.TaskId
            && value.Accounting.ProjectId is not null && value.Accounting.ParticipantId is not null)
        && value.Counters.All(counter => WorkProjectDefinition.ValidName(counter.Key) && counter.Value >= 0)
        ? value with { Counters = value.Counters.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value) } : null;
    private static WorkSharingBootstrapProfile? Bootstrap(WorkSharingBootstrapProfile? value) => value is not null
        && WorkProjectDefinition.ValidName(value.Name) && value.Locale is { Length: >= 2 and <= 35 }
        && value.Resources is { Count: > 0 and <= 100 } && value.Resources.All(resource => resource is not null
            && resource.ApiVersion is { Length: > 0 and <= 128 } && resource.Kind is { Length: > 0 and <= 128 } && resource.Kind.All(char.IsAsciiLetterOrDigit)
            && WorkProjectDefinition.ValidName(resource.Name) && WorkProjectDefinition.ValidName(resource.Namespace)
            && resource.Definition.ValueKind == JsonValueKind.Object && PortableDeclaration(resource.Definition))
        && value.Resources.Select(resource => (resource.Kind, resource.Namespace, resource.Name)).Distinct().Count() == value.Resources.Count ? value : null;
    private static bool PortableDeclaration(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().All(PortableDeclaration),
        JsonValueKind.Object => value.EnumerateObject().All(property => !Forbidden(property.Name) && PortableDeclaration(property.Value)),
        _ => true
    };
    private static bool Forbidden(string name) => name.Equals("workspaceId", StringComparison.OrdinalIgnoreCase)
        || name.Equals("tenantId", StringComparison.OrdinalIgnoreCase) || name.Equals("principalId", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ownerPrincipalId", StringComparison.OrdinalIgnoreCase) || name.Equals("scopeRef", StringComparison.OrdinalIgnoreCase)
        || name.Equals("uid", StringComparison.OrdinalIgnoreCase) || name.Equals("credential", StringComparison.OrdinalIgnoreCase)
        || name.Equals("password", StringComparison.OrdinalIgnoreCase) || name.Equals("apiKey", StringComparison.OrdinalIgnoreCase)
        || name.Equals("accessToken", StringComparison.OrdinalIgnoreCase) || name.Equals("secretValue", StringComparison.OrdinalIgnoreCase);
    private static bool ValidOptionalIds(WorkPlanId? plan, WorkTaskId? task) => plan?.Value != Guid.Empty && task?.Value != Guid.Empty;
    private static Result<WorkSharingChange> Failure() => Result<WorkSharingChange>.Failure("sharing_content_invalid");
}
