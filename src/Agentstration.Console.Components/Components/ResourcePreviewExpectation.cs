using Agentstration.Resources;
using Agentstration.Web.Components.State;

namespace Agentstration.Web.Components;

/// <summary>Exact observed identity required when opening a preview from a graph.</summary>
public sealed record ResourcePreviewExpectation(ResourceScopeRef Context, Guid Uid, long? Generation = null, string? ETag = null)
{
    internal bool CanRead(ConsoleContextState context) => context.Error is null
        && context.Current?.SelectedScopeRef == Context.Value;

    internal bool Matches(Guid? uid, long? generation, string etag) => uid == Uid
        && (Generation is null || Generation == generation)
        && (ETag is null || string.Equals(ETag, etag, StringComparison.Ordinal));
}
