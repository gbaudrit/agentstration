using Agentstration.Api.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal sealed class ListModelsEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/", HandleAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);

    private static Task<IResult> HandleAsync(ModelDiscoveryService service, CancellationToken cancellationToken) =>
        ModelsApiHttp.ExecuteAsync(async () => Results.Ok(new ValueResponse<ModelResource>(
            (await service.ListAsync(cancellationToken)).Select(value => value.Value).ToArray())));
}

internal sealed class GetModelEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/{modelName}", HandleAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);

    private static Task<IResult> HandleAsync(
        string modelName,
        string? resourceNamespace,
        HttpResponse response,
        ModelDiscoveryService service,
        CancellationToken cancellationToken) =>
        ModelsApiHttp.ExecuteAsync(async () =>
        {
            var stored = await service.GetAsync(ModelsApiHttp.Namespace(resourceNamespace), modelName, cancellationToken)
                ?? throw new ResourceNotFoundException(new(ModelResourceKinds.Model, modelName, ModelsApiHttp.Namespace(resourceNamespace)));
            return ModelsApiHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });
}
