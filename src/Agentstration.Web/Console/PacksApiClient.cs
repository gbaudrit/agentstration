using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Management.Abstractions;

namespace Agentstration.Web.Console;

public interface IPacksClient
{
    Task<IReadOnlyList<InstalledPackResource>> GetPacksAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<InstalledPackResource>> GetPackAsync(string publisher, string name, CancellationToken cancellationToken);
    Task<PackInstallationPreview> PreviewAsync(byte[] archive, string fileName, CancellationToken cancellationToken);
    Task<ResourceSnapshot<InstalledPackResource>> InstallAsync(byte[] archive, string fileName, IReadOnlyList<PackBindingSelection> bindings, CancellationToken cancellationToken);
    Task UninstallAsync(string publisher, string name, string etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<InstalledPackResource>> AttachSourceAsync(string publisher, string name, byte[] archive, string fileName, string etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<PackProjectResource>> ForkAsync(string publisher, string name, ForkPackCommand command, CancellationToken cancellationToken);
    Task<IReadOnlyList<PackProjectResource>> GetProjectsAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<PackProjectResource>> GetProjectAsync(Guid projectId, CancellationToken cancellationToken);
    Task<ResourceSnapshot<PackProjectResource>> UpdateProjectAsync(Guid projectId, UpdatePackProjectCommand command, string etag, CancellationToken cancellationToken);
    Task<PackProjectBuildResource> BuildAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PackProjectBuildResource>> GetBuildsAsync(Guid projectId, CancellationToken cancellationToken);
    Task<PackInstallationPreview> PreviewBuildAsync(Guid projectId, Guid buildId, CancellationToken cancellationToken);
    Task<ResourceSnapshot<InstalledPackResource>> InstallBuildAsync(Guid projectId, Guid buildId, bool replaceExisting, IReadOnlyList<PackBindingSelection> bindings, CancellationToken cancellationToken);
}

public sealed class PacksApiClient(HttpClient httpClient) : IPacksClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    public async Task<ResourceSnapshot<InstalledPackResource>> InstallAsync(byte[] archive, string fileName, IReadOnlyList<PackBindingSelection> bindings, CancellationToken cancellationToken)
    {
        using var response = await SendInstallationAsync(archive, fileName, bindings, cancellationToken);
        return await ReadInstalledAsync(response, cancellationToken);
    }

    public async Task UninstallAsync(string publisher, string name, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, Path(publisher, name));
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<InstalledPackResource>> AttachSourceAsync(string publisher, string name, byte[] archive, string fileName, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Path(publisher, name)}/source");
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        message.Headers.Add("X-Pack-File-Name", fileName);
        message.Content = new ByteArrayContent(archive);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadInstalledAsync(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<PackProjectResource>> ForkAsync(string publisher, string name, ForkPackCommand command, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"{Path(publisher, name)}/fork", command, cancellationToken);
        return await ReadResourceAsync<PackProjectResource>(response, "Pack Project", cancellationToken);
    }

    public async Task<IReadOnlyList<PackProjectResource>> GetProjectsAsync(CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<PackProjectResource[]>(httpClient, "api/pack-projects", cancellationToken);

    public async Task<ResourceSnapshot<PackProjectResource>> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(ProjectPath(projectId), cancellationToken);
        return await ReadResourceAsync<PackProjectResource>(response, "Pack Project", cancellationToken);
    }

    public async Task<ResourceSnapshot<PackProjectResource>> UpdateProjectAsync(Guid projectId, UpdatePackProjectCommand command, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, ProjectPath(projectId));
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        message.Content = JsonContent.Create(command);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadResourceAsync<PackProjectResource>(response, "Pack Project", cancellationToken);
    }

    public async Task<PackProjectBuildResource> BuildAsync(Guid projectId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"{ProjectPath(projectId)}/builds", null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PackProjectBuildResource>(cancellationToken)
            ?? throw Empty("Pack build");
    }

    public async Task<IReadOnlyList<PackProjectBuildResource>> GetBuildsAsync(Guid projectId, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<PackProjectBuildResource[]>(httpClient, $"{ProjectPath(projectId)}/builds", cancellationToken);

    public async Task<PackInstallationPreview> PreviewBuildAsync(Guid projectId, Guid buildId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(BuildActionPath(projectId, buildId, "preview", false), null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PackInstallationPreview>(cancellationToken)
            ?? throw Empty("Pack build preview");
    }

    public async Task<ResourceSnapshot<InstalledPackResource>> InstallBuildAsync(Guid projectId, Guid buildId, bool replaceExisting, IReadOnlyList<PackBindingSelection> bindings, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            BuildActionPath(projectId, buildId, "install", replaceExisting),
            new PackBuildInstallRequest(replaceExisting, bindings),
            cancellationToken);
        return await ReadInstalledAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendArchiveAsync(string path, byte[] archive, string fileName, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path);
        message.Headers.Add("X-Pack-File-Name", fileName);
        message.Content = new ByteArrayContent(archive);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return await httpClient.SendAsync(message, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendInstallationAsync(
        byte[] archive,
        string fileName,
        IReadOnlyList<PackBindingSelection> bindings,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var archiveContent = new ByteArrayContent(archive);
        archiveContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(archiveContent, "archive", fileName);
        content.Add(new StringContent(JsonSerializer.Serialize(bindings, JsonOptions), Encoding.UTF8, "application/json"), "bindings");
        return await httpClient.PostAsync("api/packs", content, cancellationToken);
    }

    private static async Task<ResourceSnapshot<InstalledPackResource>> ReadInstalledAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => await ReadResourceAsync<InstalledPackResource>(response, "installed Pack", cancellationToken);

    private static async Task<ResourceSnapshot<T>> ReadResourceAsync<T>(HttpResponseMessage response, string resourceName, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw Empty(resourceName);
        var etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(etag))
            throw new AgentstrationApiException($"Agentstration API did not return the {resourceName} ETag.", Guid.NewGuid().ToString("N"));
        return new(value, etag);
    }

    private static string Path(string publisher, string name) =>
        $"api/packs/{Uri.EscapeDataString(publisher)}/{Uri.EscapeDataString(name)}";

    private static string ProjectPath(Guid projectId) => $"api/pack-projects/{projectId:D}";
    private static string BuildActionPath(Guid projectId, Guid buildId, string action, bool replaceExisting) =>
        $"{ProjectPath(projectId)}/builds/{buildId:D}/{action}?replaceExisting={replaceExisting.ToString().ToLowerInvariant()}";
    private static AgentstrationApiException Empty(string resourceName) =>
        new($"Agentstration API returned an empty {resourceName}.", Guid.NewGuid().ToString("N"));
}
