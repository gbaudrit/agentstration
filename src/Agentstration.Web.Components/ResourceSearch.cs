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

public sealed record CommandPaletteFallbackResult(
    string Label,
    string Url,
    string Icon = "✦",
    string? Detail = null);

public interface ICommandPaletteFallbackProvider
{
    Task<IReadOnlyList<CommandPaletteFallbackResult>> ResolveAsync(string query, CancellationToken cancellationToken);
}

internal sealed class EmptyResourceSearchProvider : IResourceSearchProvider
{
    public Task<IReadOnlyList<ResourceSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ResourceSearchResult>>([]);
}

internal sealed class EmptyCommandPaletteFallbackProvider : ICommandPaletteFallbackProvider
{
    public Task<IReadOnlyList<CommandPaletteFallbackResult>> ResolveAsync(string query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CommandPaletteFallbackResult>>([]);
}
