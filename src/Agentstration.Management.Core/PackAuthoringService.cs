using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed partial class PackAuthoringService(
    IControlPlaneStore store,
    IPackArtifactStore artifacts,
    IPackArchiveReader archiveReader,
    PackManagementService installations,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public Task<IReadOnlyList<StoredResource<PackProjectResource>>> ListProjectsAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<PackProjectResource>(PackAuthoringKinds.PackProject, cancellationToken);

    public async Task<StoredResource<PackProjectResource>?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        (await ListProjectsAsync(cancellationToken)).SingleOrDefault(value => value.Value.Uid == projectId);

    public async Task<StoredResource<PackProjectResource>> ForkAsync(PackIdentity source, ForkPackCommand command, CancellationToken cancellationToken)
    {
        ValidateCoordinate(command.Publisher, command.Name, command.Version);
        var installed = await installations.GetAsync(source, cancellationToken) ?? throw new PackNotFoundException(source);
        var sourceArtifact = installed.Value.Definition.SourceArtifact
            ?? throw new PackValidationException("pack_source_unavailable", "The installed Pack predates source retention and cannot be forked exactly.");
        var duplicate = (await ListProjectsAsync(cancellationToken)).Any(value =>
            string.Equals(value.Value.Definition.Publisher, command.Publisher, StringComparison.Ordinal)
            && string.Equals(value.Value.Definition.PackName, command.Name, StringComparison.Ordinal));
        if (duplicate) throw new PackValidationException("pack_project_identity_conflict", $"Pack Project '{command.Publisher}/{command.Name}' already exists.");

        var sourceBytes = await ReadArtifactAsync(sourceArtifact, cancellationToken);
        await using var sourceStream = new MemoryStream(sourceBytes, writable: false);
        var sourceArchive = await archiveReader.ReadAsync(sourceStream, sourceArtifact.FileName, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var id = Guid.NewGuid();
        return await store.PutAsync(new PackProjectResource
        {
            Uid = id,
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = PackAuthoringKinds.PackProject,
            Metadata = new ResourceMetadata { Name = id.ToString("N") },
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
            Definition = new PackProjectProperties
            {
                Publisher = command.Publisher,
                PackName = command.Name,
                Version = command.Version,
                DisplayName = command.DisplayName ?? sourceArchive.Manifest.Metadata.DisplayName ?? command.Name,
                Description = command.Description ?? sourceArchive.Manifest.Metadata.Description,
                Categories = sourceArchive.Manifest.Metadata.Categories.ToArray(),
                Tags = sourceArchive.Manifest.Metadata.Tags.ToArray(),
                Origin = new(source.Publisher, source.Name, installed.Value.Definition.Version, sourceArtifact.Sha256, installed.Value.Name),
                SourceArtifact = sourceArtifact,
                CreatedAt = now,
                UpdatedAt = now
            }
        }, null, true, cancellationToken);
    }

    public async Task<StoredResource<PackProjectResource>> UpdateProjectAsync(Guid projectId, UpdatePackProjectCommand command, string etag, CancellationToken cancellationToken)
    {
        var current = await RequiredProjectAsync(projectId, cancellationToken);
        ValidateCoordinate(current.Value.Definition.Publisher, current.Value.Definition.PackName, command.Version);
        var definition = current.Value.Definition with
        {
            Version = command.Version,
            DisplayName = command.DisplayName,
            Description = command.Description,
            Categories = command.Categories?.ToArray() ?? current.Value.Definition.Categories,
            Tags = command.Tags?.ToArray() ?? current.Value.Definition.Tags,
            State = PackProjectState.Draft,
            Revision = checked(current.Value.Definition.Revision + 1),
            UpdatedAt = timeProvider.GetUtcNow()
        };
        return await store.PutAsync(current.Value with
        {
            Generation = checked(current.Value.Generation + 1),
            Definition = definition,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, etag, false, cancellationToken);
    }

    public async Task<StoredResource<PackProjectBuildResource>> BuildAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await RequiredProjectAsync(projectId, cancellationToken);
        var source = await ReadArtifactAsync(project.Value.Definition.SourceArtifact, cancellationToken);
        await using var sourceStream = new MemoryStream(source, writable: false);
        var parsed = await archiveReader.ReadAsync(sourceStream, project.Value.Definition.SourceArtifact.FileName, cancellationToken);
        var manifest = parsed.Manifest with
        {
            Metadata = parsed.Manifest.Metadata with
            {
                Publisher = project.Value.Definition.Publisher,
                Name = project.Value.Definition.PackName,
                Version = project.Value.Definition.Version,
                DisplayName = project.Value.Definition.DisplayName,
                Description = project.Value.Definition.Description,
                Categories = project.Value.Definition.Categories,
                Tags = project.Value.Definition.Tags
            }
        };
        var bytes = BuildArchive(source, manifest);
        await using (var validationStream = new MemoryStream(bytes, writable: false))
            _ = await archiveReader.ReadAsync(validationStream, BuildFileName(project.Value.Definition), cancellationToken);
        var artifact = await artifacts.SaveAsync(bytes, BuildFileName(project.Value.Definition), cancellationToken);
        var now = timeProvider.GetUtcNow();
        var buildId = Guid.NewGuid();
        var build = await store.CreateImmutableAsync(new PackProjectBuildResource
        {
            Uid = buildId,
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = PackAuthoringKinds.PackProjectBuild,
            Metadata = new ResourceMetadata { Name = buildId.ToString("N") },
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
            Definition = new PackProjectBuildProperties
            {
                ProjectId = projectId,
                ProjectRevision = project.Value.Definition.Revision,
                Publisher = project.Value.Definition.Publisher,
                PackName = project.Value.Definition.PackName,
                Version = project.Value.Definition.Version,
                Artifact = artifact,
                CreatedAt = now
            }
        }, cancellationToken);
        _ = await store.PutAsync(project.Value with
        {
            Generation = checked(project.Value.Generation + 1),
            Definition = project.Value.Definition with { State = PackProjectState.Built, LastBuildId = buildId, UpdatedAt = now },
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, project.ETag, false, cancellationToken);
        return build;
    }

    public async Task<IReadOnlyList<StoredResource<PackProjectBuildResource>>> ListBuildsAsync(Guid projectId, CancellationToken cancellationToken) =>
        (await store.ListAllAsync<PackProjectBuildResource>(PackAuthoringKinds.PackProjectBuild, cancellationToken))
            .Where(value => value.Value.Definition.ProjectId == projectId)
            .OrderByDescending(value => value.Value.Definition.CreatedAt)
            .ToArray();

    public async Task<StoredResource<PackProjectBuildResource>> GetBuildAsync(Guid projectId, Guid buildId, CancellationToken cancellationToken)
    {
        _ = await RequiredProjectAsync(projectId, cancellationToken);
        var build = (await store.ListAllAsync<PackProjectBuildResource>(PackAuthoringKinds.PackProjectBuild, cancellationToken))
            .SingleOrDefault(value => value.Value.Uid == buildId)
            ?? throw new KeyNotFoundException($"Pack build '{buildId}' was not found.");
        if (build.Value.Definition.ProjectId != projectId) throw new KeyNotFoundException($"Pack build '{buildId}' was not found in project '{projectId}'.");
        return build;
    }

    public async Task<Stream> OpenBuildAsync(Guid projectId, Guid buildId, CancellationToken cancellationToken)
    {
        var build = await GetBuildAsync(projectId, buildId, cancellationToken);
        return await artifacts.OpenReadAsync(build.Value.Definition.Artifact, cancellationToken);
    }

    public async Task<PackInstallationPreview> PreviewBuildAsync(Guid projectId, Guid buildId, CancellationToken cancellationToken)
    {
        var archive = await ReadBuildArchiveAsync(projectId, buildId, cancellationToken);
        return await installations.PreviewAsync(archive, cancellationToken);
    }

    public async Task<StoredResource<InstalledPackResource>> InstallBuildAsync(Guid projectId, Guid buildId, bool replaceExisting, bool replaceOrigin, CancellationToken cancellationToken)
    {
        var project = await RequiredProjectAsync(projectId, cancellationToken);
        var archive = await ReadBuildArchiveAsync(projectId, buildId, cancellationToken);
        var identity = new PackIdentity(archive.Manifest.Metadata.Publisher, archive.Manifest.Metadata.Name);
        var existing = await installations.GetAsync(identity, cancellationToken);
        if (existing is not null)
        {
            if (!replaceExisting)
                throw new PackValidationException("pack_already_installed", $"Pack '{identity}' is already installed. Enable replacement to reinstall this development build.");
            await installations.UninstallAsync(identity, cancellationToken);
        }

        var origin = new PackIdentity(project.Value.Definition.Origin.Publisher, project.Value.Definition.Origin.Name);
        if (replaceOrigin && origin != identity)
        {
            var installedOrigin = await installations.GetAsync(origin, cancellationToken)
                ?? throw new PackValidationException("pack_origin_not_installed", $"The source Pack '{origin}' is no longer installed.");
            var preview = await installations.PreviewAsync(archive, cancellationToken);
            var conflicts = preview.Resources.Where(resource => resource.AlreadyExists).ToArray();
            if (conflicts.Length == 0)
                throw new PackValidationException("pack_origin_replacement_not_required", $"The build has no resource conflict with source Pack '{origin}'.");
            var managedByOrigin = installedOrigin.Value.Definition.ManagedResources
                .Select(resource => (resource.Kind, resource.Name))
                .ToHashSet();
            var unrelatedConflict = conflicts.FirstOrDefault(resource => !managedByOrigin.Contains((resource.Kind, resource.Name)));
            if (unrelatedConflict is not null)
                throw new PackResourceConflictException(unrelatedConflict.Kind, unrelatedConflict.Name);
            await installations.UninstallAsync(origin, cancellationToken);
        }
        return await installations.InstallAsync(archive, cancellationToken);
    }

    public Task<StoredResource<InstalledPackResource>> InstallBuildAsync(Guid projectId, Guid buildId, bool replaceExisting, CancellationToken cancellationToken) =>
        InstallBuildAsync(projectId, buildId, replaceExisting, false, cancellationToken);

    public Task<StoredResource<InstalledPackResource>> InstallBuildAsync(Guid projectId, Guid buildId, CancellationToken cancellationToken) =>
        InstallBuildAsync(projectId, buildId, false, false, cancellationToken);

    private async Task<PackArchive> ReadBuildArchiveAsync(Guid projectId, Guid buildId, CancellationToken cancellationToken)
    {
        var build = await GetBuildAsync(projectId, buildId, cancellationToken);
        var bytes = await ReadArtifactAsync(build.Value.Definition.Artifact, cancellationToken);
        await using var stream = new MemoryStream(bytes, writable: false);
        return await archiveReader.ReadAsync(stream, build.Value.Definition.Artifact.FileName, cancellationToken);
    }

    private async Task<StoredResource<PackProjectResource>> RequiredProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await GetProjectAsync(projectId, cancellationToken) ?? throw new KeyNotFoundException($"Pack Project '{projectId}' was not found.");

    private async Task<byte[]> ReadArtifactAsync(PackArtifactReference reference, CancellationToken cancellationToken)
    {
        await using var stream = await artifacts.OpenReadAsync(reference, cancellationToken);
        await using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.LongLength != reference.Length || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), reference.Sha256, StringComparison.Ordinal))
            throw new PackValidationException("pack_artifact_integrity_failed", "The stored Pack artifact does not match its recorded hash and length.");
        return bytes;
    }

    private static byte[] BuildArchive(byte[] source, PackManifest manifest)
    {
        using var input = new ZipArchive(new MemoryStream(source, writable: false), ZipArchiveMode.Read);
        using var outputStream = new MemoryStream();
        using (var output = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            Write(output, "pack.json", manifestBytes);
            foreach (var entry in input.Entries
                         .Where(value => !string.IsNullOrEmpty(value.Name) && !IsManifest(value.FullName))
                         .OrderBy(value => value.FullName.Replace('\\', '/'), StringComparer.Ordinal))
            {
                using var sourceStream = entry.Open();
                using var content = new MemoryStream();
                sourceStream.CopyTo(content);
                Write(output, entry.FullName.Replace('\\', '/'), content.ToArray());
            }
        }
        return outputStream.ToArray();
    }

    private static void Write(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static bool IsManifest(string path) => path.Replace('\\', '/') is "pack.yaml" or "pack.yml" or "pack.json";
    private static string BuildFileName(PackProjectProperties value) => $"{value.Publisher}-{value.PackName}-{value.Version}.pack.zip";

    private static void ValidateCoordinate(string publisher, string name, string version)
    {
        if (!NameRegex().IsMatch(publisher) || !NameRegex().IsMatch(name)) throw new PackValidationException("pack_project_identity_invalid", "Pack Project publisher and name must use letters, digits, '-' or '_' and start with a letter or digit.");
        if (!VersionRegex().IsMatch(version)) throw new PackValidationException("pack_project_version_invalid", "Pack Project versions must use Semantic Versioning.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,59}$", RegexOptions.CultureInvariant)] private static partial Regex NameRegex();
    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)] private static partial Regex VersionRegex();
}
