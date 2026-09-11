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
        registries.MapGet("/discovery", SearchAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapGet("/discovery/publishers", ListPublishersAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapGet("/discovery/sources/{publisher}/{sourceName}", GetDiscoveredSourceAsync)
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapPost("/discovery/imports", ImportDiscoveredSourceAsync)
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapGet("/", ListAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapGet("/{registryName}", GetAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapPost("/", CreateAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapPut("/{registryName}", UpdateAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapDelete("/{registryName}", DeleteAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapPost("/{registryName}/refresh", RefreshAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapGet("/{registryName}/refreshes", ListRefreshesAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapGet("/{registryName}/trust", GetOriginTrustAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        registries.MapGet("/trust/sources/{publisher}/{sourceName}/versions/{version}", GetSourceTrustAsync)
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
    }

    private static Task<IResult> SearchAsync(
        string? search,
        string? publisher,
        string? registry,
        bool? compatibleOnly,
        bool? freshOnly,
        bool? conflictsOnly,
        SourceRegistryTrustPolicy? trustPolicy,
        SourceRegistryPublisherStatus? publisherStatus,
        SourceVerificationStatus? verificationStatus,
        int? skip,
        int? take,
        SourceRegistryDiscoveryService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => Results.Ok(await service.SearchAsync(new()
        {
            Search = search,
            Publisher = publisher,
            Registry = registry,
            CompatibleOnly = compatibleOnly ?? true,
            FreshOnly = freshOnly ?? false,
            ConflictsOnly = conflictsOnly ?? false,
            TrustPolicy = trustPolicy,
            PublisherStatus = publisherStatus,
            VerificationStatus = verificationStatus,
            Skip = skip ?? 0,
            Take = take ?? 50
        }, cancellationToken)));

    private static Task<IResult> ListPublishersAsync(
        SourceRegistryDiscoveryService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => Results.Ok(
            new ValueResponse<SourceRegistryDiscoveryPublisher>(await service.ListPublishersAsync(cancellationToken))));

    private static Task<IResult> GetDiscoveredSourceAsync(
        string publisher,
        string sourceName,
        SourceRegistryDiscoveryService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var result = await service.GetAsync(publisher, sourceName, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

    private static Task<IResult> ImportDiscoveredSourceAsync(
        ImportSourceRegistryObservationRequest body,
        SourceRegistryDiscoveryService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => Results.Ok(
            await service.ImportAsync(body.Selection, body.ScopeRef, cancellationToken)));

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

    private static Task<IResult> GetOriginTrustAsync(
        string registryName,
        SourceRegistryTrustEvaluationService trust,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var result = await trust.EvaluateOriginAsync(registryName, cancellationToken)
                ?? throw new SourceRegistryNotFoundException(registryName);
            return Results.Ok(result);
        });

    private static Task<IResult> GetSourceTrustAsync(
        string publisher,
        string sourceName,
        string version,
        string? manifestDigest,
        SourceRegistryTrustEvaluationService trust,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => Results.Ok(
            await trust.EvaluateSourceAsync(publisher, sourceName, version, manifestDigest, cancellationToken)));
}
