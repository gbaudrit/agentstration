using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;

namespace Agentstration.Web.Api.Models;

internal sealed class ListModelProvidersEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/", HandleAsync);
    private static Task<IResult> HandleAsync(ModelProviderManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var providers = await service.ListAsync(cancellationToken);
            return Results.Ok(new ValueResponse<ModelProviderResponse>(providers.Select(provider => ModelProviderMappings.Response(provider)).ToArray()));
        });
}

internal sealed class GetModelProviderEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{providerName}", HandleAsync);
    private static Task<IResult> HandleAsync(string providerName, string? resourceNamespace, HttpResponse response, ModelProviderManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.GetAsync(ModelManagementHttp.Namespace(resourceNamespace), providerName, cancellationToken)
                ?? throw new ModelProviderResourceNotFoundException(providerName);
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });
}

internal sealed class CreateModelProviderEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/", HandleAsync);
    private static Task<IResult> HandleAsync(CreateModelProviderRequest body, HttpResponse response, ModelProviderManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.CreateAsync(new ModelProviderResource
            {
                Metadata = new ResourceMetadata { Name = body.Name, Namespace = ModelManagementHttp.Namespace(body.Namespace) },
                Kind = ResourceKinds.ModelProvider,
                ApiVersion = ManagementApiVersions.CoreV1,
                Definition = body.Properties
            }, cancellationToken);
            response.Headers.Location = $"/api/modelproviders/{Uri.EscapeDataString(stored.Value.Name)}?resourceNamespace={Uri.EscapeDataString(stored.Value.Namespace.Value)}";
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
        });
}

internal sealed class PutModelProviderEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPut("/{providerName}", HandleAsync);
    private static Task<IResult> HandleAsync(string providerName, string? resourceNamespace, PutModelProviderRequest body, HttpRequest request, HttpResponse response, ModelProviderManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => ModelManagementHttp.ResourceResult(
            await service.PutAsync(ModelManagementHttp.Namespace(resourceNamespace), providerName, body.Properties, ModelManagementHttp.IfMatch(request), cancellationToken),
            response,
            StatusCodes.Status200OK));
}

internal sealed class DeleteModelProviderEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapDelete("/{providerName}", HandleAsync);
    private static Task<IResult> HandleAsync(string providerName, string? resourceNamespace, HttpRequest request, ModelProviderManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            await service.DeleteAsync(ModelManagementHttp.Namespace(resourceNamespace), providerName, ModelManagementHttp.IfMatch(request), cancellationToken);
            return Results.NoContent();
        });
}

internal sealed class GetModelProviderUsagesEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{providerName}/usages", HandleAsync);
    private static Task<IResult> HandleAsync(string providerName, string? resourceNamespace, ModelProviderManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var @namespace = ModelManagementHttp.Namespace(resourceNamespace);
            _ = await service.GetAsync(@namespace, providerName, cancellationToken) ?? throw new ModelProviderResourceNotFoundException(providerName);
            var usages = await service.GetUsagesAsync(@namespace, ModelProviderManagementService.ModelProviderId(providerName), cancellationToken);
            var values = usages.Select(value => new ModelProviderUsageResponse(value.Kind, value.Name, value.Name, value.DisplayName)).ToArray();
            return Results.Ok(new ModelProviderUsagesResponse(values, values.Length));
        });
}

internal sealed class TestModelProviderEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/{providerName}/test", HandleAsync);
    private static Task<IResult> HandleAsync(string providerName, string? resourceNamespace, ModelProviderManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var provider = await service.GetStatusAsync(ModelManagementHttp.Namespace(resourceNamespace), providerName, cancellationToken);
            return Results.Ok(new ModelProviderStatusResponse(provider.Configuration.Name, provider.Health.Status, provider.CheckedAt, provider.Health.Details));
        });
}

internal sealed class ListProviderModelsEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{providerName}/models", HandleAsync);
    private static Task<IResult> HandleAsync(string providerName, string? resourceNamespace, ModelProviderManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var models = await service.ListModelsAsync(ModelManagementHttp.Namespace(resourceNamespace), providerName, cancellationToken);
            return Results.Ok(new ValueResponse<AvailableModelResponse>(models.Select(model =>
                new AvailableModelResponse(model.Name, model.DisplayName, model.Status, model.Capabilities, model.Metadata)).ToArray()));
        });
}

internal sealed class GetModelProviderStatusEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{providerName}/status", HandleAsync);
    private static Task<IResult> HandleAsync(string providerName, string? resourceNamespace, ModelProviderManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var provider = await service.GetStatusAsync(ModelManagementHttp.Namespace(resourceNamespace), providerName, cancellationToken);
            return Results.Ok(new ModelProviderStatusResponse(provider.Configuration.Name, provider.Health.Status, provider.CheckedAt, provider.Health.Details));
        });
}

internal static class ModelProviderMappings
{
    public static ModelProviderResponse Response(ModelProviderView provider, bool includeEndpoint = false) => new(
        provider.Configuration.Uid.ToString("D"),
        provider.Configuration.Name,
        new ModelProviderPropertiesResponse(
            provider.Configuration.DisplayName ?? provider.Configuration.Name,
            provider.Configuration.ProviderType,
            provider.Configuration.ManagementMode.ToString().ToLowerInvariant(),
            provider.Health.Status,
            provider.Configuration.EndpointDisplayName,
            provider.Models.Count,
            includeEndpoint ? provider.Configuration.Endpoint : null,
            provider.CheckedAt),
        provider.Configuration.Namespace.Value);
}
