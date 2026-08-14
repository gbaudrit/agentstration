using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;

namespace Agentstration.Web.Console;

public interface IPacksClient
{
    Task<IReadOnlyList<InstalledPackResource>> GetPacksAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<InstalledPackResource>> GetPackAsync(string publisher, string name, CancellationToken cancellationToken);
    Task<PackInstallationPreview> PreviewAsync(byte[] archive, string fileName, CancellationToken cancellationToken);
    Task<ResourceSnapshot<InstalledPackResource>> InstallAsync(byte[] archive, string fileName, CancellationToken cancellationToken);
    Task UninstallAsync(string publisher, string name, string etag, CancellationToken cancellationToken);
}

public sealed class PacksApiClient(HttpClient httpClient) : IPacksClient
{
    public async Task<IReadOnlyList<InstalledPackResource>> GetPacksAsync(CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<InstalledPackResource[]>(httpClient, "api/packs", cancellationToken);

    public async Task<ResourceSnapshot<InstalledPackResource>> GetPackAsync(string publisher, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(Path(publisher, name), cancellationToken);
        return await ReadInstalledAsync(response, cancellationToken);
    }

    public async Task<PackInstallationPreview> PreviewAsync(byte[] archive, string fileName, CancellationToken cancellationToken)
    {
        using var response = await SendArchiveAsync("api/packs/preview", archive, fileName, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PackInstallationPreview>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty Pack preview.", Guid.NewGuid().ToString("N"));
    }

    public async Task<ResourceSnapshot<InstalledPackResource>> InstallAsync(byte[] archive, string fileName, CancellationToken cancellationToken)
    {
        using var response = await SendArchiveAsync("api/packs", archive, fileName, cancellationToken);
        return await ReadInstalledAsync(response, cancellationToken);
    }

    public async Task UninstallAsync(string publisher, string name, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, Path(publisher, name));
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendArchiveAsync(string path, byte[] archive, string fileName, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path);
        message.Headers.Add("X-Pack-File-Name", fileName);
        message.Content = new ByteArrayContent(archive);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return await httpClient.SendAsync(message, cancellationToken);
    }

    private static async Task<ResourceSnapshot<InstalledPackResource>> ReadInstalledAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<InstalledPackResource>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty installed Pack.", Guid.NewGuid().ToString("N"));
        var etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(etag))
            throw new AgentstrationApiException("Agentstration API did not return the installed Pack ETag.", Guid.NewGuid().ToString("N"));
        return new(value, etag);
    }

    private static string Path(string publisher, string name) =>
        $"api/packs/{Uri.EscapeDataString(publisher)}/{Uri.EscapeDataString(name)}";
}
