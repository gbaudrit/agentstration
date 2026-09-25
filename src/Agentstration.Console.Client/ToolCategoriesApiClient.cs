using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Api.Contracts;
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
