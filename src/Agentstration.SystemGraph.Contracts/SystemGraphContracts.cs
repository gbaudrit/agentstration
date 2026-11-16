using Agentstration.Resources;

namespace Agentstration.SystemGraph.Contracts;

public sealed record SystemGraphQuery(
    ResourceNamespace? Namespace = null,
    string? Root = null,
    int Limit = 100,
    string? Cursor = null);

public sealed record SystemGraphIdentity(
    Guid ResourceUid,
    ResourceScopeRef OwnerScope,
    ResourceNamespace Namespace,
    string Kind,
    string Name);

public sealed record SystemGraphEvidence(
    long? Generation = null,
    string? ETag = null,
    string? Publication = null,
    string? Hash = null,
    string? OriginKind = null);

public sealed record SystemGraphNode(
    string Id,
    SystemGraphIdentity Identity,
    string State,
    SystemGraphEvidence? Evidence = null);

public sealed record SystemGraphRelation(
    string Source,
    string Target,
    string Kind,
    string? SourceField = null,
    string? Resolution = null);

public sealed record SystemGraphPage(
    IReadOnlyList<SystemGraphNode> Nodes,
    IReadOnlyList<SystemGraphRelation> Relations,
    string Snapshot,
    DateTimeOffset ObservedAt,
    string Coverage,
    bool GloballyComplete,
    bool HasMore,
    string? NextCursor)
{
    public IReadOnlyList<SystemGraphScope> Scopes { get; init; } = [];
}

public sealed record SystemGraphScope(ResourceScopeRef Scope, ResourceScopeRef? Parent);

public interface ISystemGraphQueryService
{
    Task<SystemGraphPage> QueryAsync(SystemGraphQuery query, CancellationToken cancellationToken);
}

// Contributor contracts are in-process metadata ports, never HTTP index/export surfaces.
public sealed record SystemGraphContribution(
    IReadOnlyList<SystemGraphNode> Nodes,
    IReadOnlyList<SystemGraphRelation> Relations);

public interface ISystemGraphContributor
{
    string Family { get; }
    IAsyncEnumerable<SystemGraphContribution> ReadAsync(
        ResourceScopeRef scope, CancellationToken cancellationToken);
}

public sealed record SystemGraphReadContext(string AuthorizationKey, IReadOnlyList<ResourceScopeRef> Scopes, IReadOnlyList<ResourceScopeRef>? ReadableScopes = null, IReadOnlyList<SystemGraphScope>? Hierarchy = null);
public sealed record SystemGraphAccess(bool Discover, bool InspectBindings);

public interface ISystemGraphAuthorization
{
    Task<SystemGraphReadContext> GetContextAsync(CancellationToken cancellationToken);
    Task<SystemGraphAccess> EvaluateAsync(SystemGraphIdentity identity, SystemGraphReadContext context, CancellationToken cancellationToken);
}

public static class SystemGraphIds
{
    public static string Definition(Guid uid) => $"resource:{uid:D}";
    public static string Generation(Guid uid, long generation) => $"generation:{uid:D}:{generation}";
    public static string Publication(Guid uid, string version) => $"publication:{uid:D}:{Uri.EscapeDataString(version)}";
}

public interface ISystemGraphReadScope
{
    IDisposable Enter();
}
