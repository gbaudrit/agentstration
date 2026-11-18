using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Api.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;

namespace Agentstration.Web.Console;

public interface IToolCategoriesClient
{
    Task<IReadOnlyList<ToolCategoryResource>> GetCategoriesAsync(CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<ToolCategoryResource>> GetCategoryAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolCategoryMember>> GetMembersAsync(string name, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<ToolCategoryResource>> CreateCategoryAsync(CreateToolCategoryRequest request, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<ToolCategoryResource>> UpdateCategoryAsync(string name, PutToolCategoryRequest request, string etag, CancellationToken cancellationToken = default);
    Task DeleteCategoryAsync(string name, string etag, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<ToolCategoryResource>> GetCategoryAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
        @namespace.IsDefault ? GetCategoryAsync(name, cancellationToken) : throw new NotSupportedException();
    Task<IReadOnlyList<ToolCategoryMember>> GetMembersAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
        @namespace.IsDefault ? GetMembersAsync(name, cancellationToken) : throw new NotSupportedException();
    Task<ResourceSnapshot<ToolCategoryResource>> UpdateCategoryAsync(ResourceNamespace @namespace, string name, PutToolCategoryRequest request, string etag, CancellationToken cancellationToken = default) =>
        @namespace.IsDefault ? UpdateCategoryAsync(name, request, etag, cancellationToken) : throw new NotSupportedException();
    Task DeleteCategoryAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken = default) =>
        @namespace.IsDefault ? DeleteCategoryAsync(name, etag, cancellationToken) : throw new NotSupportedException();

}

public sealed class ToolCategoriesApiClient(HttpClient httpClient) : IToolCategoriesClient
{
    public async Task<IReadOnlyList<ToolCategoryResource>> GetCategoriesAsync(CancellationToken cancellationToken = default) =>
        (await ApiResponse.ReadAsync<ValueResponse<ToolCategoryResource>>(httpClient, "api/toolcategories", cancellationToken)).Value;

    public Task<ResourceSnapshot<ToolCategoryResource>> GetCategoryAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Path(name), null, null, cancellationToken);

    public async Task<IReadOnlyList<ToolCategoryMember>> GetMembersAsync(string name, CancellationToken cancellationToken = default) =>
        (await ApiResponse.ReadAsync<ValueResponse<ToolCategoryMember>>(httpClient, $"{Path(name)}/members", cancellationToken)).Value;

    public Task<ResourceSnapshot<ToolCategoryResource>> CreateCategoryAsync(CreateToolCategoryRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/toolcategories", JsonContent.Create(request), null, cancellationToken);

    public Task<ResourceSnapshot<ToolCategoryResource>> UpdateCategoryAsync(string name, PutToolCategoryRequest request, string etag, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, Path(name), JsonContent.Create(request), etag, cancellationToken);

    public async Task DeleteCategoryAsync(string name, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path(name));
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }


    public Task<ResourceSnapshot<ToolCategoryResource>> GetCategoryAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, NamespacedPath(Path(name), @namespace), null, null, cancellationToken);
    public async Task<IReadOnlyList<ToolCategoryMember>> GetMembersAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
        (await ApiResponse.ReadAsync<ValueResponse<ToolCategoryMember>>(httpClient, NamespacedPath($"{Path(name)}/members", @namespace), cancellationToken)).Value;
    public Task<ResourceSnapshot<ToolCategoryResource>> UpdateCategoryAsync(ResourceNamespace @namespace, string name, PutToolCategoryRequest request, string etag, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, NamespacedPath(Path(name), @namespace), JsonContent.Create(request), etag, cancellationToken);
    public async Task DeleteCategoryAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, NamespacedPath(Path(name), @namespace));
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }
    private static string NamespacedPath(string path, ResourceNamespace @namespace) => @namespace.IsDefault
        ? path : $"{path}?resourceNamespace={Uri.EscapeDataString(@namespace.Value)}";

    private async Task<ResourceSnapshot<ToolCategoryResource>> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        string? etag,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return new(
            (await response.Content.ReadFromJsonAsync<ToolCategoryResource>(cancellationToken))!,
            response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Missing ETag."));
    }

    private static string Path(string name) => $"api/toolcategories/{Uri.EscapeDataString(name)}";
}
