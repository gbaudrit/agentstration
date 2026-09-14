using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.ResourcePlanning.Contracts;

public readonly record struct ResourcePlanId(Guid Value)
{
    public static ResourcePlanId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public enum ResourcePlanStatus
{
    Draft,
    Ready,
    Materialized,
    Validated,
    Applied,
    Failed,
    Archived,
    Cancelled
}

public enum ResourcePlanActivityType
{
    Created,
    Refined,
    StatusChanged,
    Materialized,
    Validated,
    Applied,
    Failed,
    Archived,
    Cancelled
}

public sealed record ResourcePlanScope(Guid TenantId, WorkspaceId WorkspaceId);

public sealed record ResourcePlanOrigin(
    Guid PrincipalId,
    string? WorkItemId = null,
    string? FlowRunId = null,
    string? CallerId = null,
    string? CausationId = null,
    string? CorrelationId = null);

public sealed record ResourcePlanContent(string SchemaVersion, JsonElement Document);

public sealed record ResourcePlan(
    ResourcePlanId Id,
    ResourcePlanScope Scope,
    string Title,
    string Goal,
    string? Description,
    ResourcePlanContent Content,
    ResourcePlanStatus Status,
    long Revision,
    ResourcePlanOrigin Origin,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ResourcePlanActivity(
    Guid Id,
    ResourcePlanId PlanId,
    ResourcePlanScope Scope,
    long PlanRevision,
    ResourcePlanActivityType Type,
    Guid ActorPrincipalId,
    string? Detail,
    DateTimeOffset CreatedAt);

public sealed record ResourcePlanSnapshot(ResourcePlan Value, string ETag);
public sealed record ResourcePlanPage(IReadOnlyList<ResourcePlanSnapshot> Items, bool HasMore);

public sealed record CreateResourcePlanRequest(
    string Title,
    string Goal,
    string? Description,
    ResourcePlanContent Content,
    string? WorkItemId = null,
    string? FlowRunId = null,
    string? CallerId = null,
    string? CausationId = null,
    string? CorrelationId = null);

public sealed record RefineResourcePlanRequest(
    string Title,
    string Goal,
    string? Description,
    ResourcePlanContent Content);

public sealed record ChangeResourcePlanStatusRequest(ResourcePlanStatus Status, string? Detail = null);
