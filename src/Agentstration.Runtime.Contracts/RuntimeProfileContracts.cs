using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Contracts;

public sealed record CreateRuntimeProfileRequest(
    string Name,
    RuntimeProfileProperties Properties,
    string Namespace = "default",
    ResourceScopeRef? ScopeRef = null);
public sealed record PutRuntimeProfileRequest(RuntimeProfileProperties Properties);
public sealed record RuntimeProfileSummaryResponse(
    string Id,
    string Name,
    RuntimeProfileProperties Properties,
    int UsageCount,
    string Namespace = "default",
    ResourceScopeRef? ScopeRef = null);
public sealed record RuntimeProfileUsageResponse(
    string ResourceId,
    string Name,
    string Environment,
    string AgentResourceId);
public sealed record RuntimeProfileUsagesResponse(IReadOnlyList<RuntimeProfileUsageResponse> Value, int Count);
