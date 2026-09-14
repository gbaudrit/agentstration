using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;

namespace Agentstration.Sources;

public sealed class SourceScopeResolver(SourceManagementService sources) : ISourceScopeResolver
{
    public async Task<ResourceScopeRef> ResolveAsync(
        string? scopeRef,
        string publisher,
        string name,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(scopeRef)) return ResourceScopeRef.Parse(scopeRef);
        var source = (await sources.GetAsync(publisher, name, cancellationToken))?.Source
            ?? throw new ResourceNotFoundException(new(SourceResourceKinds.Source, name, new ResourceNamespace(publisher)));
        return source.ScopeRef ?? throw new InvalidOperationException("A Source must have an ownership scope.");
    }
}
