using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Api.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;

namespace Agentstration.Web.Console;

public interface ISchemasClient
{
    Task<IReadOnlyList<SchemaResource>> GetSchemasAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SchemaFormatDescriptor>> GetFormatsAsync(CancellationToken cancellationToken = default);
    Task<SchemaFormatValidationResult> ValidateDefinitionAsync(ValidateSchemaDefinitionRequest request,
        CancellationToken cancellationToken = default);
    Task<SchemaFormatValidationResult> ValidateInstanceAsync(ValidateSchemaInstanceRequest request,
        CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<SchemaResource>> GetSchemaAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace,
        string name, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<SchemaResource>> CreateAsync(CreateSchemaRequest request,
        CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<SchemaResource>> UpdateAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace,
        string name, PutSchemaRequest request, string etag, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SchemaVersionResource>> GetVersionsAsync(ResourceScopeRef scopeRef,
        ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default);
    Task<SchemaVersionResource> GetVersionAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace,
        string name, string version, CancellationToken cancellationToken = default);
    Task<SchemaVersionResource> PublishAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace,
        string name, PublishSchemaVersionRequest request, CancellationToken cancellationToken = default);
}

public sealed class SchemasApiClient(HttpClient httpClient) : ISchemasClient
{
    public async Task<IReadOnlyList<SchemaResource>> GetSchemasAsync(CancellationToken cancellationToken = default) =>
        (await ApiResponse.ReadAsync<ValueResponse<SchemaResource>>(httpClient, "api/schemas", cancellationToken)).Value;

    public async Task<IReadOnlyList<SchemaFormatDescriptor>> GetFormatsAsync(
        CancellationToken cancellationToken = default) =>
        (await ApiResponse.ReadAsync<ValueResponse<SchemaFormatDescriptor>>(
            httpClient, "api/schemas/formats", cancellationToken)).Value;

    public async Task<SchemaFormatValidationResult> ValidateDefinitionAsync(ValidateSchemaDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/schemas/formats/validate-definition", request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SchemaFormatValidationResult>(cancellationToken)
            ?? throw new InvalidOperationException("The Schema definition validation response was empty.");
    }

    public async Task<SchemaFormatValidationResult> ValidateInstanceAsync(ValidateSchemaInstanceRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/schemas/formats/validate-instance", request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SchemaFormatValidationResult>(cancellationToken)
            ?? throw new InvalidOperationException("The Schema instance validation response was empty.");
    }

    public async Task<ResourceSnapshot<SchemaResource>> GetSchemaAsync(ResourceScopeRef scopeRef,
        ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(Path(scopeRef, @namespace, name), cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return Snapshot(await response.Content.ReadFromJsonAsync<SchemaResource>(cancellationToken), response);
    }

    public async Task<ResourceSnapshot<SchemaResource>> CreateAsync(CreateSchemaRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("api/schemas", request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return Snapshot(await response.Content.ReadFromJsonAsync<SchemaResource>(cancellationToken), response);
    }

    public async Task<ResourceSnapshot<SchemaResource>> UpdateAsync(ResourceScopeRef scopeRef,
        ResourceNamespace @namespace, string name, PutSchemaRequest request, string etag,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, Path(scopeRef, @namespace, name))
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return Snapshot(await response.Content.ReadFromJsonAsync<SchemaResource>(cancellationToken), response);
    }

    public async Task<IReadOnlyList<SchemaVersionResource>> GetVersionsAsync(ResourceScopeRef scopeRef,
        ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
        (await ApiResponse.ReadAsync<ValueResponse<SchemaVersionResource>>(httpClient,
            VersionsPath(scopeRef, @namespace, name), cancellationToken)).Value;

    public Task<SchemaVersionResource> GetVersionAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace,
        string name, string version, CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<SchemaVersionResource>(httpClient,
            VersionPath(scopeRef, @namespace, name, version), cancellationToken);

    public async Task<SchemaVersionResource> PublishAsync(ResourceScopeRef scopeRef,
        ResourceNamespace @namespace, string name, PublishSchemaVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            VersionsPath(scopeRef, @namespace, name), request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SchemaVersionResource>(cancellationToken)
            ?? throw new InvalidOperationException("The Schema version response was empty.");
    }

    private static string Path(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name) =>
        $"api/schemas/{Uri.EscapeDataString(name)}?scopeRef={Uri.EscapeDataString(scopeRef.Value)}"
        + $"&resourceNamespace={Uri.EscapeDataString(@namespace.Value)}";

    private static string VersionsPath(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name) =>
        $"api/schemas/{Uri.EscapeDataString(name)}/versions?scopeRef={Uri.EscapeDataString(scopeRef.Value)}"
        + $"&resourceNamespace={Uri.EscapeDataString(@namespace.Value)}";

    private static string VersionPath(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name,
        string version) =>
        $"api/schemas/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(version)}"
        + $"?scopeRef={Uri.EscapeDataString(scopeRef.Value)}"
        + $"&resourceNamespace={Uri.EscapeDataString(@namespace.Value)}";

    private static ResourceSnapshot<SchemaResource> Snapshot(SchemaResource? value, HttpResponseMessage response) =>
        new(value ?? throw new InvalidOperationException("The Schema response was empty."),
            response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Missing ETag."));
}
