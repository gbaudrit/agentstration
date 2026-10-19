using Agentstration.Resources;

namespace Agentstration.Sources.Contracts;

public interface ISourceConsoleQueryService
{
    Task<IReadOnlyList<SourceConsoleListItem>> ListAsync(Guid actorPrincipalId, CancellationToken cancellationToken);

    Task<SourceConsoleDetailView> GetAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid? versionUid,
        string? locale,
        Guid actorPrincipalId,
        CancellationToken cancellationToken);

    Task ConfigureBindingsAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        IReadOnlyList<SourceConsoleBindingSelection> selections,
        string etag,
        Guid actorPrincipalId,
        CancellationToken cancellationToken);
}
