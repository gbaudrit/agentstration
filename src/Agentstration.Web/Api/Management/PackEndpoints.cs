using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class PackEndpoints : IManagementEndpoint
{
    private const int MaximumArchiveBytes = 8 * 1024 * 1024;

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/packs/preview", PreviewAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/packs", InstallAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapGet("/packs", ListAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/packs/{publisher}/{name}", GetAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/packs/{publisher}/{name}/resources/source", ListInstalledPackResourcesAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/packs/{publisher}/{name}/source", AttachSourceAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPost("/packs/{publisher}/{name}/fork", ForkAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapDelete("/packs/{publisher}/{name}", UninstallAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
        group.MapGet("/pack-projects/composer/resources", ListCompositionResourcesAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/pack-projects/composer/preview", PreviewCompositionAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/pack-projects", CreateProjectFromWorkspaceAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapGet("/pack-projects", ListProjectsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/pack-projects/{projectId:guid}", GetProjectAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPut("/pack-projects/{projectId:guid}", UpdateProjectAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapGet("/pack-projects/{projectId:guid}/resources", ListProjectResourcesAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPut("/pack-projects/{projectId:guid}/resources", UpdateProjectResourceAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPost("/pack-projects/{projectId:guid}/builds", BuildAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapGet("/pack-projects/{projectId:guid}/builds", ListBuildsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/pack-projects/{projectId:guid}/builds/{buildId:guid}/download", DownloadBuildAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/pack-projects/{projectId:guid}/builds/{buildId:guid}/preview", PreviewBuildAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/pack-projects/{projectId:guid}/builds/{buildId:guid}/install", InstallBuildAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
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
        bool? replaceExisting,
        bool? removeDashboardReferences,
        HttpRequest request,
        HttpResponse response,
        IPackArchiveReader archiveReader,
        PackManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var (archive, bindings) = await ReadInstallationAsync(request, archiveReader, cancellationToken);
            var installed = await service.InstallAsync(archive, replaceExisting ?? false, bindings, new PackRemovalOptions(removeDashboardReferences ?? false), cancellationToken);
            response.Headers.ETag = installed.ETag;
            response.Headers.Location = $"/api/packs/{Uri.EscapeDataString(installed.Value.Definition.Publisher)}/{Uri.EscapeDataString(installed.Value.Definition.PackName)}";
            return Results.Created(response.Headers.Location, installed.Value);
        });

    private static async Task<(PackArchive Archive, IReadOnlyList<PackBindingSelection> Bindings)> ReadInstallationAsync(
        HttpRequest request,
        IPackArchiveReader archiveReader,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return (await ReadArchiveAsync(request, archiveReader, cancellationToken), []);

        if (request.ContentLength is > MaximumArchiveBytes + 1024 * 1024)
            throw new PackValidationException("pack_archive_size_limit", $"Pack archives cannot exceed {MaximumArchiveBytes} compressed bytes.");
        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("archive")
            ?? throw new PackValidationException("pack_archive_missing", "Pack installation requires an archive file.");
        if (file.Length > MaximumArchiveBytes)
            throw new PackValidationException("pack_archive_size_limit", $"Pack archives cannot exceed {MaximumArchiveBytes} compressed bytes.");
        var bindingsJson = form["bindings"].FirstOrDefault();
        var bindings = string.IsNullOrWhiteSpace(bindingsJson)
            ? []
            : ResourceManifestSerializer.FromJson<PackBindingSelection[]>(bindingsJson);
        await using var stream = file.OpenReadStream();
        return (await archiveReader.ReadAsync(stream, file.FileName, cancellationToken), bindings);
    }

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
        bool? removeDashboardReferences,
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
            await service.UninstallAsync(identity, new PackRemovalOptions(removeDashboardReferences ?? false), cancellationToken);
            return Results.NoContent();
        });

    private static Task<IResult> AttachSourceAsync(
        string publisher,
        string name,
        HttpRequest request,
        HttpResponse response,
        IPackArchiveReader archiveReader,
        PackManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var etag = ManagementHttp.IfMatch(request) ?? throw new ControlPlaneConcurrencyException("Attaching a Pack source requires If-Match.");
            var archive = await ReadArchiveAsync(request, archiveReader, cancellationToken);
            var installed = await service.AttachSourceAsync(new(publisher, name), archive, etag, cancellationToken);
            return ManagementHttp.ResourceResult(installed, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> ForkAsync(string publisher, string name, ForkPackCommand command, HttpResponse response, PackAuthoringService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var project = await service.ForkAsync(new(publisher, name), command, token);
            response.Headers.ETag = project.ETag;
            response.Headers.Location = $"/api/pack-projects/{project.Value.Uid:D}";
            return Results.Created(response.Headers.Location, project.Value);
        });

    private static Task<IResult> ListProjectsAsync(PackAuthoringService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok((await service.ListProjectsAsync(token)).Select(value => value.Value)));

    private static Task<IResult> ListCompositionResourcesAsync(PackCompositionService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(await service.ListResourcesAsync(token)));

    private static Task<IResult> PreviewCompositionAsync(PreviewPackCompositionCommand command, PackCompositionService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(await service.PreviewAsync(command, token)));

    private static Task<IResult> CreateProjectFromWorkspaceAsync(CreatePackProjectFromWorkspaceCommand command, HttpResponse response, PackCompositionService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var project = await service.CreateProjectAsync(command, token);
            response.Headers.ETag = project.ETag;
            response.Headers.Location = $"/api/pack-projects/{project.Value.Uid:D}";
            return Results.Created(response.Headers.Location, project.Value);
        });

    private static Task<IResult> GetProjectAsync(Guid projectId, HttpResponse response, PackAuthoringService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var project = await service.GetProjectAsync(projectId, token) ?? throw new KeyNotFoundException($"Pack Project '{projectId}' was not found.");
            return ManagementHttp.ResourceResult(project, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> UpdateProjectAsync(Guid projectId, UpdatePackProjectCommand command, HttpRequest request, HttpResponse response, PackAuthoringService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var etag = ManagementHttp.IfMatch(request) ?? throw new ControlPlaneConcurrencyException("Updating a Pack Project requires If-Match.");
            var project = await service.UpdateProjectAsync(projectId, command, etag, token);
            return ManagementHttp.ResourceResult(project, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> ListProjectResourcesAsync(Guid projectId, PackAuthoringService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var resources = await service.ListSourceDocumentsAsync(projectId, token);
            return Results.Ok(resources.Select(resource => resource with
            {
                Source = ResourceManifestSerializer.ToYaml(ResourceManifestSerializer.FromJson<JsonElement>(resource.Source))
            }));
        });

    private static Task<IResult> ListInstalledPackResourcesAsync(string publisher, string name, PackAuthoringService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var resources = await service.ListInstalledSourceDocumentsAsync(new(publisher, name), token);
            return Results.Ok(resources.Select(resource => resource with
            {
                Source = ResourceManifestSerializer.ToYaml(ResourceManifestSerializer.FromJson<JsonElement>(resource.Source))
            }));
        });

    private static Task<IResult> UpdateProjectResourceAsync(Guid projectId, UpdatePackProjectSourceCommand command, HttpRequest request, HttpResponse response, PackAuthoringService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var etag = ManagementHttp.IfMatch(request) ?? throw new ControlPlaneConcurrencyException("Updating a Pack Project resource requires If-Match.");
            var manifest = ResourceManifestSerializer.FromYaml<JsonElement>(command.Source);
            var project = await service.UpdateSourceDocumentAsync(projectId, command with { Source = ResourceManifestSerializer.ToJson(manifest) }, etag, token);
            return ManagementHttp.ResourceResult(project, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> BuildAsync(Guid projectId, HttpResponse response, PackAuthoringService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var build = await service.BuildAsync(projectId, token);
            response.Headers.Location = $"/api/pack-projects/{projectId:D}/builds/{build.Value.Uid:D}";
            return Results.Created(response.Headers.Location, build.Value);
        });

    private static Task<IResult> ListBuildsAsync(Guid projectId, PackAuthoringService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok((await service.ListBuildsAsync(projectId, token)).Select(value => value.Value)));

    private static Task<IResult> DownloadBuildAsync(Guid projectId, Guid buildId, PackAuthoringService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var build = await service.GetBuildAsync(projectId, buildId, token);
            var stream = await service.OpenBuildAsync(projectId, buildId, token);
            return Results.File(stream, "application/zip", build.Value.Definition.Artifact.FileName, enableRangeProcessing: true);
        });

    private static Task<IResult> PreviewBuildAsync(Guid projectId, Guid buildId, PackAuthoringService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () => Results.Ok(await service.PreviewBuildAsync(projectId, buildId, token)));

    private static Task<IResult> InstallBuildAsync(Guid projectId, Guid buildId, bool? replaceExisting, HttpRequest request, HttpResponse response, PackAuthoringService service, CancellationToken token) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            var command = request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true
                ? await request.ReadFromJsonAsync<PackBuildInstallRequest>(token) ?? new()
                : new PackBuildInstallRequest(replaceExisting ?? false);
            var installed = await service.InstallBuildAsync(projectId, buildId, command.ReplaceExisting, command.Bindings ?? [], token);
            response.Headers.ETag = installed.ETag;
            response.Headers.Location = $"/api/packs/{Uri.EscapeDataString(installed.Value.Definition.Publisher)}/{Uri.EscapeDataString(installed.Value.Definition.PackName)}";
            return Results.Created(response.Headers.Location, installed.Value);
        });
}
