using Agentstration.Management.Core;
using Agentstration.Management.Contracts;

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
    private static Task<IResult> HandleAsync(string providerName, ModelProviderManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => Results.Ok(ModelProviderMappings.Response(await service.GetRequiredAsync(providerName, cancellationToken), includeEndpoint: false)));
}

internal sealed class ListProviderModelsEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{providerName}/models", HandleAsync);
    private static Task<IResult> HandleAsync(string providerName, ModelProviderManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var models = await service.ListModelsAsync(providerName, cancellationToken);
            return Results.Ok(new ValueResponse<AvailableModelResponse>(models.Select(model =>
                new AvailableModelResponse(model.Name, model.DisplayName, model.Status, model.Capabilities, model.Metadata)).ToArray()));
        });
}

internal sealed class GetModelProviderStatusEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{providerName}/status", HandleAsync);
    private static Task<IResult> HandleAsync(string providerName, ModelProviderManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var provider = await service.GetStatusAsync(providerName, cancellationToken);
            return Results.Ok(new ModelProviderStatusResponse(provider.Configuration.Name, provider.Health.Status, provider.CheckedAt, provider.Health.Details));
        });
}

internal static class ModelProviderMappings
{
    public static ModelProviderResponse Response(ModelProviderView provider, bool includeEndpoint = false) => new(
        ModelProviderManagementService.ModelProviderId(provider.Configuration.Name),
        provider.Configuration.Name,
        "default",
        "local",
        new ModelProviderPropertiesResponse(
            provider.Configuration.DisplayName ?? provider.Configuration.Name,
            provider.Configuration.ProviderType,
            provider.Configuration.ManagementMode,
            provider.Health.Status,
            provider.Configuration.EndpointDisplayName,
            provider.Models.Count,
            includeEndpoint ? provider.Configuration.Endpoint : null,
            provider.CheckedAt));
}
