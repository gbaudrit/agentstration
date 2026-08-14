using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Web.Api.Management;

internal sealed class PackEndpoints : IManagementEndpoint
{
    private const int MaximumArchiveBytes = 8 * 1024 * 1024;

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/packs/preview", PreviewAsync);
        group.MapPost("/packs", InstallAsync);
        group.MapGet("/packs", ListAsync);
        group.MapGet("/packs/{publisher}/{name}", GetAsync);
        group.MapDelete("/packs/{publisher}/{name}", UninstallAsync);
    }

    private static Task<IResult> PreviewAsync(
        HttpRequest request,
        IPackArchiveReader archiveReader,
        PackManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var archive = await ReadArchiveAsync(request, archiveReader, cancellationToken);
            return Results.Ok(await service.PreviewAsync(archive, cancellationToken));
        });

    private static Task<IResult> InstallAsync(
        HttpRequest request,
        HttpResponse response,
        IPackArchiveReader archiveReader,
        PackManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var archive = await ReadArchiveAsync(request, archiveReader, cancellationToken);
            var installed = await service.InstallAsync(archive, cancellationToken);
            response.Headers.ETag = installed.ETag;
            response.Headers.Location = $"/api/packs/{Uri.EscapeDataString(installed.Value.Definition.Publisher)}/{Uri.EscapeDataString(installed.Value.Definition.PackName)}";
            return Results.Created(response.Headers.Location, installed.Value);
        });

    private static async Task<PackArchive> ReadArchiveAsync(HttpRequest request, IPackArchiveReader archiveReader, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaximumArchiveBytes)
            throw new PackValidationException("pack_archive_size_limit", $"Pack archives cannot exceed {MaximumArchiveBytes} compressed bytes.");
        if (request.ContentType is not ("application/zip" or "application/octet-stream"))
            throw new PackValidationException("pack_content_type_invalid", "Pack installation requires application/zip or application/octet-stream.");
        await using var buffered = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (buffered.Length + read > MaximumArchiveBytes)
                throw new PackValidationException("pack_archive_size_limit", $"Pack archives cannot exceed {MaximumArchiveBytes} compressed bytes.");
            await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        buffered.Position = 0;
        var source = request.Headers["X-Pack-File-Name"].FirstOrDefault() ?? "local-upload.pack.zip";
        return await archiveReader.ReadAsync(buffered, source, cancellationToken);
    }

    private static Task<IResult> ListAsync(PackManagementService service, CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok((await service.ListAsync(cancellationToken)).Select(value => value.Value)));

    private static Task<IResult> GetAsync(
        string publisher,
        string name,
        HttpResponse response,
        PackManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.GetAsync(new(publisher, name), cancellationToken) ?? throw new PackNotFoundException(new(publisher, name));
            return ManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> UninstallAsync(
        string publisher,
        string name,
        HttpRequest request,
        PackManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var identity = new PackIdentity(publisher, name);
            var installed = await service.GetAsync(identity, cancellationToken) ?? throw new PackNotFoundException(identity);
            var ifMatch = ManagementHttp.IfMatch(request);
            if (ifMatch is not null && !string.Equals(ifMatch, installed.ETag, StringComparison.Ordinal))
                throw new ControlPlaneConcurrencyException("The supplied ETag does not match the installed Pack.");
            await service.UninstallAsync(identity, cancellationToken);
            return Results.NoContent();
        });
}
