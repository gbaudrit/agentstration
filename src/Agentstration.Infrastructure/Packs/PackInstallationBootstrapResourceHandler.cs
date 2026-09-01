using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Infrastructure.Packs;

public sealed record BootstrapPackSource
{
    public string Path { get; init; } = string.Empty;
}

public sealed record PackInstallationBootstrapDefinition
{
    public BootstrapPackSource Source { get; init; } = new();
    public IReadOnlyList<PackBindingSelection> Bindings { get; init; } = [];
}

public sealed class PackInstallationBootstrapResourceHandler(
    IPackArchiveReader archiveReader,
    PackManagementService packs) : IBootstrapResourceHandler
{
    private const long MaximumArchiveBytes = 8L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Kind => BootstrapResourceKinds.PackInstallation;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var (archive, definition) = await ReadAsync(resource, operation, cancellationToken);
        var preview = await packs.PreviewAsync(archive, definition.Bindings, cancellationToken);
        var details = preview.Resources.Select(item => new BootstrapResourcePlanDetail(
            item.Kind,
            item.Name,
            item.Change switch
            {
                PackResourceChange.Add => BootstrapResourceDisposition.Create,
                PackResourceChange.Conflict => BootstrapResourceDisposition.Conflict,
                PackResourceChange.Update or PackResourceChange.Remove => BootstrapResourceDisposition.Conflict,
                _ => BootstrapResourceDisposition.Invalid
            },
            "Pack-managed resource")).ToArray();
        if (preview.RequiresConfiguration)
        {
            var missing = string.Join(", ", preview.Bindings.Where(binding => !binding.IsResolved).Select(binding => binding.Name));
            throw new InvalidOperationException($"Pack bindings are unresolved: {missing}.");
        }
        if (preview.AlreadyInstalled)
        {
            var existing = await packs.GetAsync(new(preview.Metadata.Publisher, preview.Metadata.Name), cancellationToken);
            return new(
                existing is not null && string.Equals(existing.Value.Definition.Version, preview.Metadata.Version, StringComparison.Ordinal)
                    ? BootstrapResourceDisposition.Skip
                    : BootstrapResourceDisposition.Conflict,
                details);
        }
        return new(preview.CanInstall ? BootstrapResourceDisposition.Create : BootstrapResourceDisposition.Conflict, details);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var (archive, definition) = await ReadAsync(resource, operation, cancellationToken);
        var identity = new PackIdentity(archive.Manifest.Metadata.Publisher, archive.Manifest.Metadata.Name);
        var existing = await packs.GetAsync(identity, cancellationToken);
        if (existing is not null)
            return string.Equals(existing.Value.Definition.Version, archive.Manifest.Metadata.Version, StringComparison.Ordinal)
                ? BootstrapResourceApplyResult.Skipped
                : BootstrapResourceApplyResult.Conflict;
        var preview = await packs.PreviewAsync(archive, definition.Bindings, cancellationToken);
        if (!preview.CanInstall || preview.RequiresConfiguration)
            return BootstrapResourceApplyResult.Conflict;
        _ = await packs.InstallAsync(archive, definition.Bindings, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }

    private async Task<(PackArchive Archive, PackInstallationBootstrapDefinition Definition)> ReadAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var definition = resource.Definition.Deserialize<PackInstallationBootstrapDefinition>(JsonOptions)
            ?? throw new InvalidOperationException("PackInstallation definition is required.");
        var relativePath = definition.Source.Path.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.StartsWith('/')
            || Path.IsPathRooted(relativePath)
            || relativePath.Split('/', StringSplitOptions.None).Any(segment => segment is "" or "." or ".."))
            throw new InvalidOperationException("PackInstallation definition.source.path must be a safe path relative to the profile directory.");
        if (!string.Equals(Path.GetExtension(relativePath), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PackInstallation source must be a .zip archive.");
        var path = Path.GetFullPath(Path.Combine(operation.ProfilePath, relativePath));
        var profilePrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(operation.ProfilePath)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidOperationException($"Pack archive '{relativePath}' was not found inside bootstrap profile '{operation.ProfileName}'.");
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("PackInstallation source cannot be a symbolic link or reparse point.");
        if (info.Length > MaximumArchiveBytes)
            throw new InvalidOperationException($"PackInstallation archive cannot exceed {MaximumArchiveBytes} bytes.");
        await using var stream = File.OpenRead(path);
        return (await archiveReader.ReadAsync(stream, relativePath, cancellationToken), definition);
    }
}
