namespace Agentstration.Web.Components;

public sealed record ResourceSearchResult(
    string Label,
    string ResourceType,
    string Identifier,
    string Url,
    string Status,
    string Icon,
    string? SearchText = null);

public interface IResourceSearchProvider
{
    Task<IReadOnlyList<ResourceSearchResult>> SearchAsync(string query, CancellationToken cancellationToken);
}

internal sealed class EmptyResourceSearchProvider : IResourceSearchProvider
{
    public Task<IReadOnlyList<ResourceSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ResourceSearchResult>>([]);
}
