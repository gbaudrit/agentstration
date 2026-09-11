using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface ISourceConsoleApiClient
{
    Task<IReadOnlyList<SourceConsoleListItem>> ListAsync(CancellationToken cancellationToken);
    Task<SourceConsoleDetailView> GetAsync(ResourceScopeRef scopeRef, string publisher, string name, Guid? versionUid, string? locale, CancellationToken cancellationToken);
    Task<SourceImportResult> ImportYamlAsync(string manifest, ResourceScopeRef scopeRef, CancellationToken cancellationToken);
    Task<SourceImportResult> ImportUrlAsync(Uri url, ResourceScopeRef scopeRef, CancellationToken cancellationToken);
    Task UpdateDisplayNameAsync(ResourceScopeRef scopeRef, string publisher, string name, string displayName, string etag, CancellationToken cancellationToken);
    Task UpdateRefreshConfigurationAsync(ResourceScopeRef scopeRef, string publisher, string name, SourceRefreshConfiguration refresh, string etag, CancellationToken cancellationToken);
    Task DeleteSourceAsync(ResourceScopeRef scopeRef, string publisher, string name, string etag, CancellationToken cancellationToken);
    Task ConfigureBindingsAsync(ResourceScopeRef scopeRef, string publisher, string name, Guid versionUid, IReadOnlyList<SourceConsoleBindingSelection> selections, string etag, CancellationToken cancellationToken);
    Task RefreshChannelAsync(ResourceScopeRef scopeRef, string publisher, string name, Guid versionUid, string channel, CancellationToken cancellationToken);
    Task<SourceImportResult> RefreshSourceAsync(ResourceScopeRef scopeRef, string publisher, string name, CancellationToken cancellationToken);
    Task<SourcePackInstallationPreview> PreviewPackAsync(SourcePackSelection selection, IReadOnlyList<PackBindingSelection> bindings, bool replaceExisting, CancellationToken cancellationToken);
    Task<ResourceSnapshot<InstalledPackResource>> InstallPackAsync(SourcePackSelection selection, string expectedPreviewDigest, bool replaceExisting, IReadOnlyList<PackBindingSelection> bindings, CancellationToken cancellationToken);
}

public sealed class SourceConsoleApiClient(HttpClient httpClient) : ISourceConsoleApiClient
{
    public Task<IReadOnlyList<SourceConsoleListItem>> ListAsync(CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<IReadOnlyList<SourceConsoleListItem>>(httpClient, "api/sources/console", cancellationToken);

    public Task<SourceConsoleDetailView> GetAsync(ResourceScopeRef scopeRef, string publisher, string name, Guid? versionUid, string? locale, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<SourceConsoleDetailView>(httpClient, ConsolePath(scopeRef, publisher, name, versionUid, locale), cancellationToken);

    public Task<SourceImportResult> ImportYamlAsync(string manifest, ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
        PostAsync<SourceImportResult>("api/sources/imports/yaml", new ImportSourceYamlRequest(manifest, scopeRef), cancellationToken);

    public Task<SourceImportResult> ImportUrlAsync(Uri url, ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
        PostAsync<SourceImportResult>("api/sources/imports/url", new ImportSourceUrlRequest(url.AbsoluteUri, scopeRef), cancellationToken);

    public Task UpdateDisplayNameAsync(ResourceScopeRef scopeRef, string publisher, string name, string displayName, string etag, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, ResourcePath(scopeRef, publisher, name, "display-name"), new UpdateSourceDisplayNameRequest(displayName), etag, cancellationToken);

    public Task UpdateRefreshConfigurationAsync(ResourceScopeRef scopeRef, string publisher, string name, SourceRefreshConfiguration refresh, string etag, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, ResourcePath(scopeRef, publisher, name, "refresh-policy"), new UpdateSourceRefreshConfigurationRequest(refresh), etag, cancellationToken);

    public Task DeleteSourceAsync(ResourceScopeRef scopeRef, string publisher, string name, string etag, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, ResourcePath(scopeRef, publisher, name), null, etag, cancellationToken);

    public Task ConfigureBindingsAsync(ResourceScopeRef scopeRef, string publisher, string name, Guid versionUid, IReadOnlyList<SourceConsoleBindingSelection> selections, string etag, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, $"api/sources/console/{Escape(publisher)}/{Escape(name)}/versions/{versionUid:D}/bindings?scopeRef={Escape(scopeRef.Value)}", selections, etag, cancellationToken);

    public Task RefreshChannelAsync(ResourceScopeRef scopeRef, string publisher, string name, Guid versionUid, string channel, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"api/sources/{Escape(publisher)}/{Escape(name)}/versions/{versionUid:D}/channels/{Escape(channel)}/refresh?scopeRef={Escape(scopeRef.Value)}", null, null, cancellationToken);

    public Task<SourceImportResult> RefreshSourceAsync(ResourceScopeRef scopeRef, string publisher, string name, CancellationToken cancellationToken) =>
        PostAsync<SourceImportResult>(ResourcePath(scopeRef, publisher, name, "refresh"), null, cancellationToken);

    public Task<SourcePackInstallationPreview> PreviewPackAsync(SourcePackSelection selection, IReadOnlyList<PackBindingSelection> bindings, bool replaceExisting, CancellationToken cancellationToken) =>
        PostAsync<SourcePackInstallationPreview>(PackPath(selection, "preview"), new SourcePackPreviewRequest(replaceExisting, bindings), cancellationToken);

    public async Task<ResourceSnapshot<InstalledPackResource>> InstallPackAsync(SourcePackSelection selection, string expectedPreviewDigest, bool replaceExisting, IReadOnlyList<PackBindingSelection> bindings, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(PackPath(selection, "install"), new SourcePackInstallRequest(expectedPreviewDigest, replaceExisting, bindings), cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<InstalledPackResource>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty installed Pack.", Guid.NewGuid().ToString("N"));
        var etag = response.Headers.ETag?.ToString()
            ?? throw new AgentstrationApiException("Agentstration API did not return the installed Pack ETag.", Guid.NewGuid().ToString("N"));
        return new(value, etag);
    }

    private async Task<T> PostAsync<T>(string path, object? body, CancellationToken cancellationToken)
    {
        using var response = body is null
            ? await httpClient.PostAsync(path, null, cancellationToken)
            : await httpClient.PostAsJsonAsync(path, body, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty response.", Guid.NewGuid().ToString("N"));
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = body is null ? null : JsonContent.Create(body)
        };
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    private static string ConsolePath(ResourceScopeRef scopeRef, string publisher, string name, Guid? versionUid, string? locale)
    {
        var path = $"api/sources/console/{Escape(publisher)}/{Escape(name)}?scopeRef={Escape(scopeRef.Value)}";
        if (versionUid is not null) path += $"&versionUid={versionUid:D}";
        if (!string.IsNullOrWhiteSpace(locale)) path += $"&locale={Escape(locale)}";
        return path;
    }

    private static string ResourcePath(ResourceScopeRef scopeRef, string publisher, string name, string? child = null) =>
        $"api/sources/{Escape(publisher)}/{Escape(name)}{(child is null ? string.Empty : $"/{child}")}?scopeRef={Escape(scopeRef.Value)}";

    private static string PackPath(SourcePackSelection selection, string action) =>
        $"api/sources/{Escape(selection.SourcePublisher)}/{Escape(selection.SourceName)}/versions/{selection.SourceVersionUid:D}/channels/{Escape(selection.Channel)}/snapshots/{selection.SnapshotUid:D}/pack-catalogs/{Escape(selection.CatalogName)}/entries/{Escape(selection.EntryName)}/{action}?scopeRef={Escape(selection.ScopeRef.Value)}";

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
