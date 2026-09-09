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
        sources.MapPost("/packs/preview", PreviewPackAsync);
        sources.MapPost("/packs/install", InstallPackAsync);
        sources.MapGet("", ListAsync);
        sources.MapGet("/{publisher}/{name}", GetAsync);
        sources.MapDelete("/{publisher}/{name}", DeleteAsync);
        sources.MapGet("/{publisher}/{name}/versions", ListVersionsAsync);
        sources.MapGet("/{publisher}/{name}/versions/{versionUid:guid}", GetVersionAsync);
        sources.MapGet("/{publisher}/{name}/versions/{versionUid:guid}/verification", GetVersionVerificationAsync);
        sources.MapGet("/{publisher}/{name}/versions/{versionUid:guid}/bindings", GetBindingsAsync);
        sources.MapPut("/{publisher}/{name}/versions/{versionUid:guid}/bindings", ConfigureBindingsAsync);
        sources.MapPost("/{publisher}/{name}/versions/{versionUid:guid}/channels/{channel}/refresh", RefreshChannelAsync);
        sources.MapGet("/{publisher}/{name}/versions/{versionUid:guid}/channels/{channel}/snapshots", ListChannelSnapshotsAsync);
        sources.MapGet("/{publisher}/{name}/versions/{versionUid:guid}/channels/{channel}/snapshots/{snapshotUid:guid}", GetChannelSnapshotAsync);
        sources.MapGet("/{publisher}/{name}/versions/{versionUid:guid}/channels/{channel}/snapshots/{snapshotUid:guid}/verification", GetChannelSnapshotVerificationAsync);
        sources.MapGet("/{publisher}/{name}/versions/{versionUid:guid}/channels/{channel}/status", GetChannelStatusAsync);
        sources.MapGet("/{publisher}/{name}/versions/{versionUid:guid}/channels/{channel}/snapshots/{snapshotUid:guid}/catalogs", BrowseCatalogsAsync);
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
            var result = await service.ImportYamlAsync(request.Manifest, request.ScopeRef, cancellationToken);
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
            var result = await service.ImportUrlAsync(source, request.ScopeRef, cancellationToken);
            SetImportLocation(response, result);
            return Results.Json(result, statusCode: result.Outcome == SourceImportOutcome.Created
                ? StatusCodes.Status201Created
                : StatusCodes.Status200OK);
        });

    private static Task<IResult> ListAsync(SourceManagementService service, CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(await service.ListAsync(cancellationToken)));

    private static Task<IResult> PreviewPackAsync(
        SourcePackInstallRequest request,
        SourcePackInstallationService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(await service.PreviewAsync(
            request.Selection, request.Bindings ?? [], cancellationToken)));

    private static Task<IResult> InstallPackAsync(
        SourcePackInstallRequest request,
        HttpResponse response,
        SourcePackInstallationService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var installed = await service.InstallAsync(
                request.Selection,
                request.ReplaceExisting,
                request.Bindings ?? [],
                new PackRemovalOptions(request.RemoveDashboardReferences),
                cancellationToken);
            response.Headers.ETag = installed.ETag;
            response.Headers.Location = $"/api/packs/{Uri.EscapeDataString(installed.Value.Definition.Publisher)}/{Uri.EscapeDataString(installed.Value.Definition.PackName)}";
            return Results.Created(response.Headers.Location, installed.Value);
        });

    private static Task<IResult> GetAsync(
        string publisher,
        string name,
        string? scopeRef,
        SourceManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(
            await (Scope(scopeRef) is { } scope
                ? service.GetExactAsync(scope, publisher, name, cancellationToken)
                : service.GetAsync(publisher, name, cancellationToken))
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Source, name, new Agentstration.Resources.ResourceNamespace(publisher)))));

    private static Task<IResult> DeleteAsync(
        string publisher,
        string name,
        string? scopeRef,
        HttpRequest request,
        SourceManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var ifMatch = ManagementHttp.IfMatch(request)
                ?? throw new ControlPlaneConcurrencyException("Deleting a Source requires If-Match.");
            await service.DeleteExactAsync(
                await ResolveScopeAsync(scopeRef, publisher, name, service, cancellationToken),
                publisher,
                name,
                ifMatch,
                cancellationToken);
            return Results.NoContent();
        });

    private static Task<IResult> ListVersionsAsync(
        string publisher,
        string name,
        string? scopeRef,
        SourceManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(Scope(scopeRef) is { } scope
            ? await service.ListVersionsExactAsync(scope, publisher, name, cancellationToken)
            : await service.ListVersionsAsync(publisher, name, cancellationToken)));

    private static Task<IResult> GetVersionAsync(
        string publisher,
        string name,
        Guid versionUid,
        string? scopeRef,
        SourceManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(
            await (Scope(scopeRef) is { } scope
                ? service.GetVersionExactAsync(scope, publisher, name, versionUid, cancellationToken)
                : service.GetVersionAsync(publisher, name, versionUid, cancellationToken))
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceVersion, versionUid.ToString("D")))));

    private static Task<IResult> UpdateDisplayNameAsync(
        string publisher,
        string name,
        string? scopeRef,
        UpdateSourceDisplayNameRequest request,
        HttpRequest httpRequest,
        HttpResponse response,
        SourceManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var ifMatch = ManagementHttp.IfMatch(httpRequest)
                ?? throw new ControlPlaneConcurrencyException("Updating a Source display name requires If-Match.");
            var updated = Scope(scopeRef) is { } scope
                ? await service.UpdateDisplayNameExactAsync(scope, publisher, name, request.DisplayName, ifMatch, cancellationToken)
                : await service.UpdateDisplayNameAsync(publisher, name, request.DisplayName, ifMatch, cancellationToken);
            return ManagementHttp.ResourceResult(updated, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> GetVersionVerificationAsync(
        string publisher,
        string name,
        Guid versionUid,
        string? scopeRef,
        SourceManagementService sources,
        SourceVerificationService verification,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var scope = await ResolveScopeAsync(scopeRef, publisher, name, sources, cancellationToken);
            var version = await sources.GetVersionExactAsync(scope, publisher, name, versionUid, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceVersion, versionUid.ToString("D")));
            return Results.Ok(await verification.VerifyDefinitionAsync(version, cancellationToken));
        });

    private static Task<IResult> GetBindingsAsync(
        string publisher,
        string name,
        Guid versionUid,
        string? scopeRef,
        HttpResponse response,
        SourceBindingManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var status = Scope(scopeRef) is { } scope
                ? await service.GetStatusExactAsync(scope, publisher, name, versionUid, cancellationToken)
                : await service.GetStatusAsync(publisher, name, versionUid, cancellationToken);
            response.Headers.ETag = status.ConfigurationETag;
            return Results.Ok(status);
        });

    private static Task<IResult> ConfigureBindingsAsync(
        string publisher,
        string name,
        Guid versionUid,
        string? scopeRef,
        ConfigureSourceBindingsRequest request,
        HttpRequest httpRequest,
        HttpResponse response,
        SourceBindingManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var ifMatch = ManagementHttp.IfMatch(httpRequest)
                ?? throw new ControlPlaneConcurrencyException("Updating Source bindings requires If-Match.");
            var result = Scope(scopeRef) is { } scope
                ? await service.ConfigureExactAsync(scope, publisher, name, versionUid, request.Bindings, ifMatch, cancellationToken)
                : await service.ConfigureAsync(publisher, name, versionUid, request.Bindings, ifMatch, cancellationToken);
            response.Headers.ETag = result.Configuration.ETag;
            return Results.Ok(result);
        });

    private static void SetImportLocation(HttpResponse response, SourceImportResult result)
    {
        var scopeRef = result.Source.Source.ScopeRef
            ?? throw new InvalidOperationException("An imported Source must have an ownership scope.");
        response.Headers.Location = $"/api/sources/{Uri.EscapeDataString(result.Source.Source.Definition.Publisher)}/{Uri.EscapeDataString(result.Source.Source.Metadata.Name)}/versions/{result.Version.Uid:D}?scopeRef={Uri.EscapeDataString(scopeRef.ToString())}";
    }

    private static Task<IResult> RefreshChannelAsync(
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        string? scopeRef,
        HttpResponse response,
        SourceChannelSnapshotService service,
        SourceManagementService sources,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var scope = await ResolveScopeAsync(scopeRef, publisher, name, sources, cancellationToken);
            var result = await service.RefreshExactAsync(scope, publisher, name, versionUid, channel, cancellationToken);
            response.Headers.Location = $"/api/sources/{Uri.EscapeDataString(publisher)}/{Uri.EscapeDataString(name)}/versions/{versionUid:D}/channels/{Uri.EscapeDataString(channel)}/snapshots/{result.Snapshot.Uid:D}?scopeRef={Uri.EscapeDataString(scope.ToString())}";
            return Results.Json(result, statusCode: result.Outcome == SourceChannelRefreshOutcome.Created
                ? StatusCodes.Status201Created
                : StatusCodes.Status200OK);
        });

    private static Task<IResult> ListChannelSnapshotsAsync(
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        string? scopeRef,
        SourceChannelSnapshotService service,
        SourceManagementService sources,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(await service.ListAsync(
            await ResolveScopeAsync(scopeRef, publisher, name, sources, cancellationToken),
            publisher, name, versionUid, channel, cancellationToken)));

    private static Task<IResult> GetChannelSnapshotAsync(
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        Guid snapshotUid,
        string? scopeRef,
        SourceChannelSnapshotService service,
        SourceManagementService sources,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(await service.GetAsync(
            await ResolveScopeAsync(scopeRef, publisher, name, sources, cancellationToken),
            publisher, name, versionUid, channel, snapshotUid, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceChannelSnapshot, snapshotUid.ToString("D")))));

    private static Task<IResult> GetChannelStatusAsync(
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        string? scopeRef,
        SourceChannelSnapshotService service,
        SourceManagementService sources,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(await service.GetStatusAsync(
            await ResolveScopeAsync(scopeRef, publisher, name, sources, cancellationToken),
            publisher, name, versionUid, channel, cancellationToken)));

    private static Task<IResult> GetChannelSnapshotVerificationAsync(
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        Guid snapshotUid,
        string? scopeRef,
        SourceManagementService sources,
        SourceChannelSnapshotService snapshots,
        SourceVerificationService verification,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var scope = await ResolveScopeAsync(scopeRef, publisher, name, sources, cancellationToken);
            var version = await sources.GetVersionExactAsync(scope, publisher, name, versionUid, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceVersion, versionUid.ToString("D")));
            var snapshot = await snapshots.GetAsync(scope, publisher, name, versionUid, channel, snapshotUid, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceChannelSnapshot, snapshotUid.ToString("D")));
            return Results.Ok(await verification.VerifySnapshotAsync(version, snapshot, cancellationToken));
        });

    private static async Task<Agentstration.Resources.ResourceScopeRef> ResolveScopeAsync(
        string? value,
        string publisher,
        string name,
        SourceManagementService sources,
        CancellationToken cancellationToken)
    {
        if (Scope(value) is { } explicitScope) return explicitScope;
        var source = (await sources.GetAsync(publisher, name, cancellationToken))?.Source
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Source, name, new Agentstration.Resources.ResourceNamespace(publisher)));
        return source.ScopeRef ?? throw new InvalidOperationException("A Source must have an ownership scope.");
    }

    private static Task<IResult> BrowseCatalogsAsync(
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        Guid snapshotUid,
        string? scopeRef,
        string? locale,
        SourceCatalogService service,
        SourceManagementService sources,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(await service.BrowseAsync(
            await ResolveScopeAsync(scopeRef, publisher, name, sources, cancellationToken),
            publisher, name, versionUid, channel, snapshotUid, locale, cancellationToken)));

    private static void EnforceRequestBound(HttpRequest request)
    {
        if (request.ContentLength is > MaximumRequestBytes)
            throw new SourceValidationException("source_import_request_size_limit", $"Source import requests cannot exceed {MaximumRequestBytes} bytes.");
    }

    private static Agentstration.Resources.ResourceScopeRef? Scope(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Agentstration.Resources.ResourceScopeRef.Parse(value);
}
