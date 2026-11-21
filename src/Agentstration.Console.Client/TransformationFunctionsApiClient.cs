using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Api.Contracts;
using Agentstration.Functions.Abstractions;
using Agentstration.Functions.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public sealed record FunctionChoice(string DisplayName, ResolvedTransformationFunction Function)
{
    public string? Description { get; init; }
    public bool IsBuiltIn { get; init; }
}
public sealed record FunctionCatalogPage<T>(IReadOnlyList<T> Items, bool HasMore);
public interface ITransformationFunctionsClient
{
    Task<FunctionCatalogPage<TransformationFunctionResource>> ListPageAsync(ResourceScopeRef? scope, int skip, int take, CancellationToken ct) =>
        throw new NotSupportedException("This client does not support function collection paging.");
    Task<FunctionCatalogPage<FunctionKindResource>> ListKindsPageAsync(ResourceScopeRef? scope, int skip, int take, CancellationToken ct) =>
        throw new NotSupportedException("This client does not support function-kind collection paging.");
    Task<IReadOnlyList<TransformationFunctionResource>> ListAsync(ResourceScopeRef? scope, CancellationToken ct);
    Task<IReadOnlyList<FunctionKindResource>> ListKindsAsync(ResourceScopeRef? scope, CancellationToken ct);
    Task<IReadOnlyList<NativeFunctionDescriptor>> ImplementationsAsync(CancellationToken ct);
    Task<IReadOnlyList<FunctionChoice>> ChoicesAsync(ResourceScopeRef scope, CancellationToken ct);
    Task<ResourceSnapshot<TransformationFunctionResource>> GetAsync(FunctionReference reference, CancellationToken ct);
    Task<ResourceSnapshot<FunctionKindResource>> GetKindAsync(FunctionReference reference, CancellationToken ct);
    Task SaveAsync(TransformationFunctionResource resource, string? etag, CancellationToken ct);
    Task SaveKindAsync(FunctionKindResource resource, string? etag, CancellationToken ct);
    Task<TransformationFunctionResource> ExposeAsync(ExposeTransformationSchemaRequest request, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class TransformationFunctionsApiClient(HttpClient http) : ITransformationFunctionsClient
{
    public async Task<TransformationFunctionResource> ExposeAsync(ExposeTransformationSchemaRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/transformation-functions/expose-schema", request, ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TransformationFunctionResource>(ct))!;
    }
    public Task<FunctionCatalogPage<TransformationFunctionResource>> ListPageAsync(ResourceScopeRef? scope, int skip, int take, CancellationToken ct) =>
        ReadPageAsync<TransformationFunctionResource>("api/transformation-functions", scope, skip, take, ct);
    public Task<FunctionCatalogPage<FunctionKindResource>> ListKindsPageAsync(ResourceScopeRef? scope, int skip, int take, CancellationToken ct) =>
        ReadPageAsync<FunctionKindResource>("api/function-kinds", scope, skip, take, ct);
    private async Task<FunctionCatalogPage<T>> ReadPageAsync<T>(string path, ResourceScopeRef? scope, int skip, int take, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(skip, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, 99);
        var owner = scope is { } exact ? $"&scopeRef={Uri.EscapeDataString(exact.Value)}" : string.Empty;
        var page = await ApiResponse.ReadAsync<ValueResponse<T>>(http, $"{path}?skip={skip}&take={take + 1}{owner}", ct);
        return new(page.Value.Take(take).ToArray(), page.Value.Count > take && skip + take <= 1_000_000);
    }

    public async Task<IReadOnlyList<TransformationFunctionResource>> ListAsync(ResourceScopeRef? scope, CancellationToken ct) =>
        (await ApiResponse.ReadAsync<ValueResponse<TransformationFunctionResource>>(http, "api/transformation-functions" + Query(scope), ct)).Value;
    public async Task<IReadOnlyList<FunctionKindResource>> ListKindsAsync(ResourceScopeRef? scope, CancellationToken ct) =>
        (await ApiResponse.ReadAsync<ValueResponse<FunctionKindResource>>(http, "api/function-kinds" + Query(scope), ct)).Value;
    public async Task<IReadOnlyList<NativeFunctionDescriptor>> ImplementationsAsync(CancellationToken ct) =>
        (await ApiResponse.ReadAsync<ValueResponse<NativeFunctionDescriptor>>(http, "api/transformation-functions/implementations", ct)).Value;
    public async Task<IReadOnlyList<FunctionChoice>> ChoicesAsync(ResourceScopeRef scope, CancellationToken ct)
    {
        var result = new List<FunctionChoice>();
        foreach (var function in (await ListAsync(scope, ct)).Where(f => f.Definition.Enabled))
            foreach (var version in function.Definition.Versions.OrderByDescending(v => v.Version, StringComparer.Ordinal))
            {
                using var response = await http.PostAsJsonAsync("api/transformation-functions/resolve", new
                { function = new FunctionReference(function.Name, version.Version, function.ScopeRef, function.Namespace, function.Uid), consumerScopeRef = scope.Value }, ct);
                if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.UnprocessableEntity) continue;
                await ApiResponse.EnsureSuccessAsync(response, ct);
                var resolved = await response.Content.ReadFromJsonAsync<ResolvedTransformationFunction>(ct)
                    ?? throw new InvalidOperationException("The function contract is unavailable.");
                result.Add(new(function.Definition.DisplayName + " · " + version.Version, resolved)
                { Description = function.Definition.Description, IsBuiltIn = function.Governance.Origin.Kind == ResourceOriginKind.BuiltIn });
            }
        return result;
    }
    public async Task<ResourceSnapshot<TransformationFunctionResource>> GetAsync(FunctionReference reference, CancellationToken ct)
    {
        using var response = await http.GetAsync("api/transformation-functions/" + Uri.EscapeDataString(reference.Name) + Query(reference.ScopeRef)
            + "&namespace=" + Uri.EscapeDataString((reference.Namespace ?? ResourceNamespace.Default).Value), ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
        return new((await response.Content.ReadFromJsonAsync<TransformationFunctionResource>(ct))!, response.Headers.ETag?.ToString() ?? string.Empty);
    }
    public async Task<ResourceSnapshot<FunctionKindResource>> GetKindAsync(FunctionReference reference, CancellationToken ct)
    {
        using var response = await http.GetAsync("api/function-kinds/" + Uri.EscapeDataString(reference.Name) + Query(reference.ScopeRef)
            + "&namespace=" + Uri.EscapeDataString((reference.Namespace ?? ResourceNamespace.Default).Value), ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
        return new((await response.Content.ReadFromJsonAsync<FunctionKindResource>(ct))!, response.Headers.ETag?.ToString() ?? string.Empty);
    }
    public Task SaveAsync(TransformationFunctionResource resource, string? etag, CancellationToken ct) => SaveResourceAsync("api/transformation-functions", resource, etag, ct);
    public Task SaveKindAsync(FunctionKindResource resource, string? etag, CancellationToken ct) => SaveResourceAsync("api/function-kinds", resource, etag, ct);
    private async Task SaveResourceAsync<T>(string path, T resource, string? etag, CancellationToken ct) where T : Resource
    {
        using var request = new HttpRequestMessage(etag is null ? HttpMethod.Post : HttpMethod.Put,
            etag is null ? path : path + "/" + Uri.EscapeDataString(resource.Name) + Query(resource.ScopeRef))
        { Content = JsonContent.Create(resource) };
        if (etag is not null) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await http.SendAsync(request, ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
    }
    private static string Query(ResourceScopeRef? scope) => scope is null ? "?take=100" : "?scopeRef=" + Uri.EscapeDataString(scope.Value.Value) + "&take=100";
}
