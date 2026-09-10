using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

public static class SourceRegistryEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var registries = endpoints.MapGroup("/api/sourceregistries");
        registries.MapGet("/", ListAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapGet("/{registryName}", GetAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapPost("/", CreateAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapPut("/{registryName}", UpdateAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapDelete("/{registryName}", DeleteAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapPost("/{registryName}/refresh", RefreshAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapGet("/{registryName}/refreshes", ListRefreshesAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
    }

    private static Task<IResult> ListAsync(SourceRegistryManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
            Results.Ok(new ValueResponse<SourceRegistryRegistrationView>(await service.ListAsync(cancellationToken))));

    private static Task<IResult> GetAsync(
        string registryName,
        HttpResponse response,
        SourceRegistryManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var view = await service.GetAsync(registryName, cancellationToken)
                ?? throw new SourceRegistryNotFoundException(registryName);
            response.Headers.ETag = view.Registration.ETag;
            return Results.Ok(view);
        });

    private static Task<IResult> UpdateAsync(
        string registryName,
        PutSourceRegistryRequest body,
        HttpRequest request,
        HttpResponse response,
        SourceRegistryManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.UpdateAsync(registryName, body.Properties, ModelManagementHttp.IfMatch(request), cancellationToken);
            var view = await service.GetAsync(stored.Value.Name, cancellationToken)
                ?? throw new SourceRegistryNotFoundException(stored.Value.Name);
            response.Headers.ETag = stored.ETag;
            return Results.Ok(view);
        });

    private static Task<IResult> CreateAsync(
        CreateSourceRegistryRequest body,
        HttpResponse response,
        SourceRegistryManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.CreateAsync(body.Name, body.Properties, cancellationToken);
            var view = await service.GetAsync(stored.Value.Name, cancellationToken)
                ?? throw new SourceRegistryNotFoundException(stored.Value.Name);
            response.Headers.ETag = stored.ETag;
            return Results.Json(view, statusCode: StatusCodes.Status201Created);
        });

    private static Task<IResult> DeleteAsync(
        string registryName,
        HttpRequest request,
        SourceRegistryManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            await service.DeleteAsync(registryName, ModelManagementHttp.IfMatch(request), cancellationToken);
            return Results.NoContent();
        });

    private static Task<IResult> RefreshAsync(
        string registryName,
        HttpResponse response,
        SourceRegistryManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var view = await service.RefreshAsync(registryName, cancellationToken);
            response.Headers.ETag = view.Registration.ETag;
            return Results.Ok(view);
        });

    private static Task<IResult> ListRefreshesAsync(
        string registryName,
        int? take,
        SourceRegistryManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var records = await service.ListRefreshesAsync(registryName, take ?? 50, cancellationToken);
            return Results.Ok(new SourceRegistryRefreshHistoryResponse(records, records.Count));
        });
}
