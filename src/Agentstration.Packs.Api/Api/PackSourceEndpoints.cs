using Agentstration.Packs;
using Agentstration.Packs.Contracts;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal static class PackSourceEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var sources = endpoints.MapGroup("/api/sources").RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        sources.MapPost("/{publisher}/{name}/versions/{versionUid:guid}/channels/{channel}/snapshots/{snapshotUid:guid}/pack-catalogs/{catalog}/entries/{entry}/preview", PreviewAsync);
        sources.MapPost("/{publisher}/{name}/versions/{versionUid:guid}/channels/{channel}/snapshots/{snapshotUid:guid}/pack-catalogs/{catalog}/entries/{entry}/install", InstallAsync);
    }

    private static Task<IResult> PreviewAsync(
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        Guid snapshotUid,
        string catalog,
        string entry,
        string? scopeRef,
        SourcePackPreviewRequest request,
        ISourceScopeResolver scopes,
        PackSourceInstallationService service,
        CancellationToken cancellationToken) =>
        PacksApiHttp.ExecuteAsync(async () => Results.Ok(await service.PreviewAsync(
            new(
                await scopes.ResolveAsync(scopeRef, publisher, name, cancellationToken),
                publisher,
                name,
                versionUid,
                channel,
                snapshotUid,
                catalog,
                entry),
            request.Bindings ?? [],
            request.ReplaceExisting,
            new PackRemovalOptions(),
            cancellationToken)));

    private static Task<IResult> InstallAsync(
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        Guid snapshotUid,
        string catalog,
        string entry,
        string? scopeRef,
        SourcePackInstallRequest request,
        HttpResponse response,
        ISourceScopeResolver scopes,
        PackSourceInstallationService service,
        CancellationToken cancellationToken) =>
        PacksApiHttp.ExecuteAsync(async () =>
        {
            var installed = await service.InstallAsync(
                new(
                    await scopes.ResolveAsync(scopeRef, publisher, name, cancellationToken),
                    publisher,
                    name,
                    versionUid,
                    channel,
                    snapshotUid,
                    catalog,
                    entry),
                request.ExpectedPreviewDigest,
                request.ReplaceExisting,
                request.Bindings ?? [],
                new PackRemovalOptions(),
                cancellationToken);
            response.Headers.ETag = installed.ETag;
            response.Headers.Location = $"/api/packs/{Uri.EscapeDataString(installed.Value.Definition.Publisher)}/{Uri.EscapeDataString(installed.Value.Definition.PackName)}";
            return Results.Created(response.Headers.Location, installed.Value);
        });
}
