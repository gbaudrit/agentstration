using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Web.Console;

public interface IToolDefinitionsClient
{
    Task<IReadOnlyList<ToolDefinitionResource>> GetAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolDefinitionResource>> GetAsync(string name, ResourceNamespace @namespace, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolDefinitionResource>> CreateAsync(CreateToolDefinitionRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolDefinitionResource>> UpdateAsync(string name, ResourceNamespace @namespace, PutToolDefinitionRequest request, string etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolDefinitionResource>> SetEnabledAsync(string name, ResourceNamespace @namespace, bool enabled, string etag, CancellationToken cancellationToken);
    Task DeleteAsync(string name, ResourceNamespace @namespace, string etag, CancellationToken cancellationToken);
}

public sealed class ToolDefinitionsApiClient(HttpClient httpClient) : IToolDefinitionsClient
{
    public async Task<IReadOnlyList<ToolDefinitionResource>> GetAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<ToolDefinitionResource>>(httpClient, "api/tooldefinitions", cancellationToken)).Value;

    public async Task<ResourceSnapshot<ToolDefinitionResource>> GetAsync(string name, ResourceNamespace @namespace, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(Path(name, @namespace), cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<ToolDefinitionResource>> CreateAsync(CreateToolDefinitionRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/tooldefinitions", request, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<ToolDefinitionResource>> UpdateAsync(string name, ResourceNamespace @namespace, PutToolDefinitionRequest request, string etag, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Put, Path(name, @namespace), request, etag, cancellationToken);

    public async Task<ResourceSnapshot<ToolDefinitionResource>> SetEnabledAsync(string name, ResourceNamespace @namespace, bool enabled, string etag, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Put, ChildPath(name, @namespace, "enabled"), new SetToolDefinitionEnabledRequest(enabled), etag, cancellationToken);

    public async Task DeleteAsync(string name, ResourceNamespace @namespace, string etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path(name, @namespace));
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<ResourceSnapshot<ToolDefinitionResource>> SendAsync<T>(HttpMethod method, string path, T body, string etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    private static async Task<ResourceSnapshot<ToolDefinitionResource>> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<ToolDefinitionResource>(cancellationToken)
            ?? throw new InvalidOperationException("ToolDefinition API returned an empty response.");
        return new(value, response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Missing ETag."));
    }

    private static string Path(string name, ResourceNamespace @namespace) =>
        $"api/tooldefinitions/{Uri.EscapeDataString(name)}?namespace={Uri.EscapeDataString(@namespace.Value)}";
    private static string ChildPath(string name, ResourceNamespace @namespace, string child) =>
        $"api/tooldefinitions/{Uri.EscapeDataString(name)}/{child}?namespace={Uri.EscapeDataString(@namespace.Value)}";
}
