using Agentstration.Resources;

namespace Agentstration.Sources.Contracts;

public interface ISourceScopeResolver
{
    Task<ResourceScopeRef> ResolveAsync(
        string? scopeRef,
        string publisher,
        string name,
        CancellationToken cancellationToken);
}
