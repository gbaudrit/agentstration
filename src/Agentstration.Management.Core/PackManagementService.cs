using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed partial class PackValidationException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

public sealed class PackNotFoundException(PackIdentity identity) : KeyNotFoundException($"Pack '{identity}' is not installed.");
public sealed class PackAlreadyInstalledException(PackIdentity identity) : InvalidOperationException($"Pack '{identity}' is already registered.");
public sealed class PackResourceConflictException(string kind, string name) : InvalidOperationException($"Resource '{kind}/{name}' already exists and cannot be replaced by Pack V1.");
public sealed class PackResourceModifiedException(string kind, string name) : InvalidOperationException($"Resource '{kind}/{name}' changed after installation and was preserved.");

public sealed partial class PackManagementService
{
    private readonly IControlPlaneStore store;
    private readonly TimeProvider timeProvider;
    private readonly IPackArtifactStore? artifacts;
    private readonly IReadOnlyDictionary<string, IPackResourceHandler> handlers;

    public PackManagementService(IControlPlaneStore store, IEnumerable<IPackResourceHandler> resourceHandlers, TimeProvider timeProvider)
        : this(store, resourceHandlers, timeProvider, null) { }

    public PackManagementService(IControlPlaneStore store, IEnumerable<IPackResourceHandler> resourceHandlers, TimeProvider timeProvider, IPackArtifactStore? artifacts)
    {
        this.store = store;
        this.timeProvider = timeProvider;
        this.artifacts = artifacts;
        handlers = resourceHandlers.ToDictionary(value => value.Kind, StringComparer.Ordinal);
    }

    public Task<IReadOnlyList<StoredResource<InstalledPackResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<InstalledPackResource>(ResourceKinds.InstalledPack, cancellationToken);

    public Task<StoredResource<InstalledPackResource>?> GetAsync(PackIdentity identity, CancellationToken cancellationToken) =>
        store.GetAsync<InstalledPackResource>(new(ResourceKinds.InstalledPack, identity.ResourceName), cancellationToken);

    public async Task<PackInstallationPreview> PreviewAsync(PackArchive archive, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(archive, cancellationToken);
        return prepared.Preview;
    }

    public async Task<StoredResource<InstalledPackResource>> InstallAsync(PackArchive archive, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(archive, cancellationToken);
        var identity = new PackIdentity(archive.Manifest.Metadata.Publisher, archive.Manifest.Metadata.Name);
        var @namespace = identity.Namespace;
        if (prepared.Preview.AlreadyInstalled) throw new PackAlreadyInstalledException(identity);
        var conflict = prepared.Preview.Resources.FirstOrDefault(resource => resource.AlreadyExists);
        if (conflict is not null) throw new PackResourceConflictException(conflict.Kind, conflict.Name);

        var now = timeProvider.GetUtcNow();
        var sourceArtifact = artifacts is not null && !archive.Content.IsEmpty
            ? await artifacts.SaveAsync(archive.Content, archive.Source, cancellationToken)
            : null;
        var installed = await store.PutAsync(new InstalledPackResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.InstalledPack,
            Metadata = new ResourceMetadata { Name = identity.ResourceName },
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Creating },
            Definition = new InstalledPackProperties
            {
                Publisher = identity.Publisher,
                PackName = identity.Name,
                Namespace = @namespace,
                Version = archive.Manifest.Metadata.Version,
                DisplayName = archive.Manifest.Metadata.DisplayName,
                Description = archive.Manifest.Metadata.Description,
                Source = archive.Source,
                SourceArtifact = sourceArtifact,
                InstalledAt = now,
                State = InstalledPackState.Installing
            }
        }, null, true, cancellationToken);

        var applied = new List<(ManagedPackResource Resource, IPackResourceHandler Handler)>();
        try
        {
            foreach (var pair in prepared.Handlers.OrderBy(value => value.Value.InstallOrder).ThenBy(value => value.Key.Path, StringComparer.Ordinal))
            {
                var managed = await pair.Value.InstallAsync(pair.Key, identity, @namespace, archive.Manifest.Metadata.Version, cancellationToken);
                applied.Add((managed, pair.Value));
                installed = await UpdateAsync(installed, installed.Value.Definition with { ManagedResources = applied.Select(value => value.Resource).ToArray() }, ProvisioningState.Creating, cancellationToken);
            }

            return await UpdateAsync(installed, installed.Value.Definition with
            {
                State = InstalledPackState.Installed,
                ErrorCode = null,
                ErrorMessage = null
            }, ProvisioningState.Succeeded, cancellationToken);
        }
        catch (Exception exception)
        {
            var remaining = new List<ManagedPackResource>();
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                try { await item.Handler.DeleteAsync(item.Resource, CancellationToken.None); }
                catch { remaining.Add(item.Resource); }
            }

            _ = await UpdateAsync(installed, installed.Value.Definition with
            {
                State = remaining.Count == 0 ? InstalledPackState.Failed : InstalledPackState.Degraded,
                ManagedResources = remaining,
                ErrorCode = "pack_installation_failed",
                ErrorMessage = exception.Message
            }, ProvisioningState.Failed, CancellationToken.None);
            throw;
        }
    }

    public async Task<StoredResource<InstalledPackResource>> AttachSourceAsync(PackIdentity identity, PackArchive archive, string etag, CancellationToken cancellationToken)
    {
        var installed = await GetAsync(identity, cancellationToken) ?? throw new PackNotFoundException(identity);
        if (installed.Value.Definition.SourceArtifact is not null)
            throw new PackValidationException("pack_source_already_retained", "This installed Pack already has an immutable source archive.");
        if (artifacts is null || archive.Content.IsEmpty)
            throw new PackValidationException("pack_source_retention_unavailable", "Pack source retention is not available.");
        if (!string.Equals(archive.Manifest.Metadata.Publisher, identity.Publisher, StringComparison.Ordinal)
            || !string.Equals(archive.Manifest.Metadata.Name, identity.Name, StringComparison.Ordinal)
            || !string.Equals(archive.Manifest.Metadata.Version, installed.Value.Definition.Version, StringComparison.Ordinal))
            throw new PackValidationException("pack_source_identity_mismatch", "The selected archive must have the same publisher, name, and version as the installed Pack.");

        _ = await PrepareAsync(archive, cancellationToken);
        var expected = installed.Value.Definition.ManagedResources
            .Select(resource => (resource.Kind, resource.Name, resource.Path))
            .OrderBy(resource => resource.Kind, StringComparer.Ordinal)
            .ThenBy(resource => resource.Name, StringComparer.Ordinal)
            .ThenBy(resource => resource.Path, StringComparer.Ordinal)
            .ToArray();
        var supplied = archive.Resources
            .Select(resource => (resource.Kind, resource.Name, resource.Path))
            .OrderBy(resource => resource.Kind, StringComparer.Ordinal)
            .ThenBy(resource => resource.Name, StringComparer.Ordinal)
            .ThenBy(resource => resource.Path, StringComparer.Ordinal)
            .ToArray();
        if (!expected.SequenceEqual(supplied))
            throw new PackValidationException("pack_source_resources_mismatch", "The selected archive does not contain the same resource inventory as the installed Pack.");

        var sourceArtifact = await artifacts.SaveAsync(archive.Content, archive.Source, cancellationToken);
        return await store.PutAsync(installed.Value with
        {
            Generation = checked(installed.Value.Generation + 1),
            Definition = installed.Value.Definition with { Source = archive.Source, SourceArtifact = sourceArtifact },
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, etag, false, cancellationToken);
    }

    private async Task<(PackInstallationPreview Preview, IReadOnlyDictionary<PackResourceDocument, IPackResourceHandler> Handlers)> PrepareAsync(
        PackArchive archive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ValidateManifest(archive);
        var identity = new PackIdentity(archive.Manifest.Metadata.Publisher, archive.Manifest.Metadata.Name);
        var @namespace = identity.Namespace;
        var selectedHandlers = new Dictionary<PackResourceDocument, IPackResourceHandler>();
        var resources = new List<PackResourcePreview>();
        foreach (var resource in archive.Resources)
        {
            if (resource.ApiVersion != ManagementApiVersions.CoreV1)
                throw new PackValidationException("pack_resource_api_version_unsupported", $"Resource '{resource.Path}' uses unsupported apiVersion '{resource.ApiVersion}'.");
            if (!handlers.TryGetValue(resource.Kind, out var handler))
                throw new PackValidationException("pack_resource_kind_unsupported", $"Resource kind '{resource.Kind}' is not supported by this installation.");
            await handler.ValidateAsync(resource, archive.Resources, cancellationToken);
            resources.Add(new(resource.Path, resource.Kind, resource.Name, await handler.ExistsAsync(@namespace, resource.Name, cancellationToken)));
            selectedHandlers.Add(resource, handler);
        }

        var preview = new PackInstallationPreview(
            archive.Manifest.Metadata,
            resources,
            await GetAsync(identity, cancellationToken) is not null)
        {
            Namespace = @namespace
        };
        return (preview, selectedHandlers);
    }

    public async Task UninstallAsync(PackIdentity identity, CancellationToken cancellationToken)
    {
        var installed = await GetAsync(identity, cancellationToken) ?? throw new PackNotFoundException(identity);
        installed = await UpdateAsync(installed, installed.Value.Definition with { State = InstalledPackState.Uninstalling }, ProvisioningState.Deleting, cancellationToken);
        var remaining = installed.Value.Definition.ManagedResources.ToList();
        try
        {
            foreach (var resource in installed.Value.Definition.ManagedResources.Reverse())
            {
                if (!handlers.TryGetValue(resource.Kind, out var handler))
                    throw new PackValidationException("pack_resource_kind_unsupported", $"Resource kind '{resource.Kind}' has no installed handler.");
                var currentToken = await handler.GetVersionTokenAsync(resource.Namespace, resource.Name, cancellationToken);
                if (currentToken is not null)
                {
                    if (!string.Equals(currentToken, resource.VersionToken, StringComparison.Ordinal))
                        throw new PackResourceModifiedException(resource.Kind, resource.Name);
                    await handler.DeleteAsync(resource, cancellationToken);
                }
                remaining.Remove(resource);
                installed = await UpdateAsync(installed, installed.Value.Definition with { ManagedResources = remaining.ToArray() }, ProvisioningState.Deleting, cancellationToken);
            }

            await store.DeleteAsync(new(ResourceKinds.InstalledPack, identity.ResourceName), installed.ETag, cancellationToken);
        }
        catch (Exception exception)
        {
            _ = await UpdateAsync(installed, installed.Value.Definition with
            {
                State = InstalledPackState.Degraded,
                ManagedResources = remaining.ToArray(),
                ErrorCode = "pack_uninstallation_failed",
                ErrorMessage = exception.Message
            }, ProvisioningState.Failed, CancellationToken.None);
            throw;
        }
    }

    private async Task<StoredResource<InstalledPackResource>> UpdateAsync(
        StoredResource<InstalledPackResource> current,
        InstalledPackProperties definition,
        ProvisioningState state,
        CancellationToken cancellationToken) =>
        await store.PutAsync(current.Value with
        {
            Generation = checked(current.Value.Generation + 1),
            Definition = definition,
            Status = new ResourceStatus { ProvisioningState = state }
        }, current.ETag, false, cancellationToken);

    private void ValidateManifest(PackArchive archive)
    {
        var manifest = archive.Manifest;
        if (manifest.ApiVersion != ManagementApiVersions.CoreV1)
            throw new PackValidationException("pack_api_version_unsupported", $"Supported Pack apiVersion is '{ManagementApiVersions.CoreV1}'.");
        if (manifest.Kind != PackKinds.Pack) throw new PackValidationException("pack_kind_invalid", $"Pack manifest kind must be '{PackKinds.Pack}'.");
        ValidateName(manifest.Metadata.Publisher, "publisher");
        ValidateName(manifest.Metadata.Name, "name");
        if (!SemanticVersionRegex().IsMatch(manifest.Metadata.Version))
            throw new PackValidationException("pack_version_invalid", "Pack version must use Semantic Versioning.");
        if (manifest.Definition.Requirements.Count > 0)
            throw new PackValidationException("pack_requirements_unsupported", "Pack requirements are declared but dependency resolution is not available in V1.");
        if (manifest.Definition.Resources.Count != archive.Resources.Count)
            throw new PackValidationException("pack_resource_count_mismatch", "The Pack resource list does not match the validated archive content.");
        if (!manifest.Definition.Resources.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(archive.Resources.Select(value => value.Path)))
            throw new PackValidationException("pack_resource_path_mismatch", "The Pack resource paths do not match the validated archive content.");
        var duplicates = archive.Resources.GroupBy(value => (value.Kind, value.Name)).FirstOrDefault(value => value.Count() > 1);
        if (duplicates is not null)
            throw new PackValidationException("pack_resource_duplicate", $"Resource '{duplicates.Key.Kind}/{duplicates.Key.Name}' is declared more than once.");
    }

    private static void ValidateName(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 60 || !char.IsAsciiLetterOrDigit(value[0]) || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-') || value.Any(char.IsUpper))
            throw new PackValidationException($"pack_{field}_invalid", $"Pack {field} must contain 1 to 60 lowercase ASCII letters, digits or '-' and start with a letter or digit.");
    }

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();
}
