using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Api.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Web.Console;

public interface IToolSetsClient
{
    Task<IReadOnlyList<ToolSetResource>> GetToolSetsAsync(CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<ToolSetResource>> GetToolSetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolSetVersionResource>> GetVersionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default);
    Task<ToolSetVersionResource> GetVersionAsync(ResourceNamespace @namespace, string name, string version, CancellationToken cancellationToken = default);
    Task DeleteAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken = default);
}

public sealed class ToolSetsApiClient(HttpClient httpClient) : IToolSetsClient
{
    public async Task<IReadOnlyList<ToolSetResource>> GetToolSetsAsync(CancellationToken cancellationToken = default) =>
        (await ApiResponse.ReadAsync<ValueResponse<ToolSetResource>>(httpClient, "api/toolsets", cancellationToken)).Value;

    public async Task<ResourceSnapshot<ToolSetResource>> GetToolSetAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(Path(@namespace, name), cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return new(
            (await response.Content.ReadFromJsonAsync<ToolSetResource>(cancellationToken))!,
            response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Missing ETag."));
    }

    public async Task<IReadOnlyList<ToolSetVersionResource>> GetVersionsAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken = default) =>
        (await ApiResponse.ReadAsync<ValueResponse<ToolSetVersionResource>>(httpClient,
            ChildPath(@namespace, name, "versions"), cancellationToken)).Value;

    public Task<ToolSetVersionResource> GetVersionAsync(
        ResourceNamespace @namespace,
        string name,
        string version,
        CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<ToolSetVersionResource>(httpClient,
            ChildPath(@namespace, name, $"versions/{Uri.EscapeDataString(version)}"), cancellationToken);

    public async Task DeleteAsync(
        ResourceNamespace @namespace,
        string name,
        string etag,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path(@namespace, name));
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    private static string Path(ResourceNamespace @namespace, string name)
    {
        var path = $"api/toolsets/{Uri.EscapeDataString(name)}";
        return @namespace.IsDefault ? path : $"{path}?resourceNamespace={Uri.EscapeDataString(@namespace.Value)}";
    }

    private static string ChildPath(ResourceNamespace @namespace, string name, string child)
    {
        var path = $"api/toolsets/{Uri.EscapeDataString(name)}/{child}";
        return @namespace.IsDefault ? path : $"{path}?resourceNamespace={Uri.EscapeDataString(@namespace.Value)}";
    }
}
