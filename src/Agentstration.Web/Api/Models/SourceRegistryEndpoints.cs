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
        registries.MapPut("/{registryName}", UpdateAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
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
        UpdateOfficialSourceRegistryRequest body,
        HttpRequest request,
        HttpResponse response,
        SourceRegistryManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            EnsureOfficial(registryName);
            if (!Uri.TryCreate(body.IndexUrl, UriKind.Absolute, out var indexUrl))
                throw new SourceRegistryOperationException("source_registry_index_url_invalid", "The registry index URL is invalid.");
            var stored = await service.UpdateOfficialAsync(indexUrl, body.Enabled, ModelManagementHttp.IfMatch(request), cancellationToken);
            var view = await service.GetAsync(stored.Value.Name, cancellationToken)
                ?? throw new SourceRegistryNotFoundException(stored.Value.Name);
            response.Headers.ETag = stored.ETag;
            return Results.Ok(view);
        });

    private static Task<IResult> RefreshAsync(
        string registryName,
        HttpResponse response,
        SourceRegistryManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            EnsureOfficial(registryName);
            var view = await service.RefreshOfficialAsync(cancellationToken);
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
            EnsureOfficial(registryName);
            var records = await service.ListRefreshesAsync(registryName, take ?? 50, cancellationToken);
            return Results.Ok(new SourceRegistryRefreshHistoryResponse(records, records.Count));
        });

    private static void EnsureOfficial(string registryName)
    {
        if (!string.Equals(registryName, SourceRegistryWellKnown.OfficialName, StringComparison.Ordinal))
            throw new SourceRegistryNotFoundException(registryName);
    }
}
