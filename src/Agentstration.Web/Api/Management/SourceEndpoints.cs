using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class SourceEndpoints : IManagementEndpoint
{
    private const int MaximumRequestBytes = 1024 * 1024 + 16 * 1024;

    public static void Map(RouteGroupBuilder group)
    {
        var sources = group.MapGroup("/sources").RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        sources.MapPost("/imports/yaml", ImportYamlAsync);
        sources.MapPost("/imports/url", ImportUrlAsync);
        sources.MapGet("", ListAsync);
        sources.MapGet("/{publisher}/{name}", GetAsync);
        sources.MapGet("/{publisher}/{name}/versions", ListVersionsAsync);
        sources.MapGet("/{publisher}/{name}/versions/{versionUid:guid}", GetVersionAsync);
        sources.MapPut("/{publisher}/{name}/display-name", UpdateDisplayNameAsync);
    }

    private static Task<IResult> ImportYamlAsync(
        ImportSourceYamlRequest request,
        HttpRequest httpRequest,
        HttpResponse response,
        SourceManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(httpRequest);
            EnforceRequestBound(httpRequest);
            var result = await service.ImportYamlAsync(request.Manifest, cancellationToken);
            SetImportLocation(response, result);
            return Results.Json(result, statusCode: result.Outcome == SourceImportOutcome.Created
                ? StatusCodes.Status201Created
                : StatusCodes.Status200OK);
        });

    private static Task<IResult> ImportUrlAsync(
        ImportSourceUrlRequest request,
        HttpRequest httpRequest,
        HttpResponse response,
        SourceManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(httpRequest);
            EnforceRequestBound(httpRequest);
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var source))
                throw new SourceValidationException("source_origin_invalid", "Source origin must be an absolute HTTP(S) URL.");
            var result = await service.ImportUrlAsync(source, cancellationToken);
            SetImportLocation(response, result);
            return Results.Json(result, statusCode: result.Outcome == SourceImportOutcome.Created
                ? StatusCodes.Status201Created
                : StatusCodes.Status200OK);
        });

    private static Task<IResult> ListAsync(SourceManagementService service, CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(await service.ListAsync(cancellationToken)));

    private static Task<IResult> GetAsync(
        string publisher,
        string name,
        SourceManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(
            await service.GetAsync(publisher, name, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Source, name, new Agentstration.Resources.ResourceNamespace(publisher)))));

    private static Task<IResult> ListVersionsAsync(
        string publisher,
        string name,
        SourceManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(await service.ListVersionsAsync(publisher, name, cancellationToken)));

    private static Task<IResult> GetVersionAsync(
        string publisher,
        string name,
        Guid versionUid,
        SourceManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(
            await service.GetVersionAsync(publisher, name, versionUid, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceVersion, versionUid.ToString("D")))));

    private static Task<IResult> UpdateDisplayNameAsync(
        string publisher,
        string name,
        UpdateSourceDisplayNameRequest request,
        HttpRequest httpRequest,
        HttpResponse response,
        SourceManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var ifMatch = ManagementHttp.IfMatch(httpRequest)
                ?? throw new ControlPlaneConcurrencyException("Updating a Source display name requires If-Match.");
            var updated = await service.UpdateDisplayNameAsync(publisher, name, request.DisplayName, ifMatch, cancellationToken);
            return ManagementHttp.ResourceResult(updated, response, StatusCodes.Status200OK);
        });

    private static void SetImportLocation(HttpResponse response, SourceImportResult result)
    {
        response.Headers.Location = $"/api/sources/{Uri.EscapeDataString(result.Source.Source.Definition.Publisher)}/{Uri.EscapeDataString(result.Source.Source.Metadata.Name)}/versions/{result.Version.Uid:D}";
    }

    private static void EnforceRequestBound(HttpRequest request)
    {
        if (request.ContentLength is > MaximumRequestBytes)
            throw new SourceValidationException("source_import_request_size_limit", $"Source import requests cannot exceed {MaximumRequestBytes} bytes.");
    }
}
