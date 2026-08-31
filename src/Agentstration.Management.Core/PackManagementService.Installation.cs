using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed partial class PackManagementService
{
    public Task<IReadOnlyList<StoredResource<InstalledPackResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<InstalledPackResource>(ResourceKinds.InstalledPack, cancellationToken);

    public Task<StoredResource<InstalledPackResource>?> GetAsync(PackIdentity identity, CancellationToken cancellationToken) =>
        store.GetAsync<InstalledPackResource>(new(ResourceKinds.InstalledPack, identity.ResourceName), cancellationToken);

    public async Task<PackInstallationPreview> PreviewAsync(PackArchive archive, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(archive, [], cancellationToken);
        return prepared.Preview;
    }

    public async Task<StoredResource<InstalledPackResource>> InstallAsync(PackArchive archive, CancellationToken cancellationToken)
        => await InstallAsync(archive, false, [], cancellationToken);

    public async Task<StoredResource<InstalledPackResource>> InstallAsync(
        PackArchive archive,
        IReadOnlyList<PackBindingSelection> bindings,
        CancellationToken cancellationToken)
        => await InstallAsync(archive, false, bindings, cancellationToken);

    public async Task<StoredResource<InstalledPackResource>> InstallAsync(
        PackArchive archive,
        bool replaceExisting,
        IReadOnlyList<PackBindingSelection> bindings,
        CancellationToken cancellationToken) =>
        await InstallAsync(archive, replaceExisting, bindings, new PackRemovalOptions(), cancellationToken);

    public async Task<StoredResource<InstalledPackResource>> InstallAsync(
        PackArchive archive,
        bool replaceExisting,
        IReadOnlyList<PackBindingSelection> bindings,
        PackRemovalOptions removalOptions,
        CancellationToken cancellationToken)
    {
        var identity = new PackIdentity(archive.Manifest.Metadata.Publisher, archive.Manifest.Metadata.Name);
        var prepared = await PrepareAsync(archive, bindings, cancellationToken);
        var unresolved = prepared.Preview.Bindings.FirstOrDefault(binding => !binding.IsResolved);
        if (unresolved is not null)
            throw new PackValidationException("pack_binding_unresolved", $"Pack binding '{unresolved.Name}' requires an available {unresolved.TargetKind} resource.");
        if (prepared.Preview.AlreadyInstalled)
        {
            if (!replaceExisting) throw new PackAlreadyInstalledException(identity);
            return await UpdateInstallationAsync(archive, prepared, removalOptions, cancellationToken);
        }

        var @namespace = identity.Namespace;
        var conflict = prepared.Preview.Resources.FirstOrDefault(resource => resource.AlreadyExists);
        if (conflict is not null) throw new PackResourceConflictException(conflict.Kind, conflict.Name);
        var resolutions = prepared.Preview.Bindings
            .Where(binding => binding.Target is not null && binding.TargetAvailable)
            .Select(binding => new PackBindingResolution(binding.Name, binding.TargetKind, binding.Target!))
            .ToArray();

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
                Audience = archive.Manifest.Metadata.Audience,
                Purpose = archive.Manifest.Metadata.Purpose,
                DisplayName = archive.Manifest.Metadata.DisplayName,
                Description = archive.Manifest.Metadata.Description,
                Source = archive.Source,
                SourceArtifact = sourceArtifact,
                InstalledAt = now,
                State = InstalledPackState.Installing,
                Bindings = resolutions
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

            installed = await UpdateAsync(installed, installed.Value.Definition with
            {
                State = InstalledPackState.Installed,
                ErrorCode = null,
                ErrorMessage = null
            }, ProvisioningState.Succeeded, cancellationToken);
            await SaveConfigurationAsync(identity, resolutions, cancellationToken);
            return installed;
        }
        catch (Exception exception)
        {
            var remaining = new List<ManagedPackResource>();
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                try { await item.Handler.DeleteAsync(item.Resource, new PackRemovalOptions(), CancellationToken.None); }
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

        _ = await PrepareAsync(archive, installed.Value.Definition.Bindings.Select(binding => new PackBindingSelection(binding.Name, binding.Target)).ToArray(), cancellationToken);
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

    private async Task<StoredResource<InstalledPackResource>> UpdateInstallationAsync(
        PackArchive archive,
        (PackInstallationPreview Preview, IReadOnlyDictionary<PackResourceDocument, IPackResourceHandler> Handlers) prepared,
        PackRemovalOptions removalOptions,
        CancellationToken cancellationToken)
    {
        var identity = new PackIdentity(archive.Manifest.Metadata.Publisher, archive.Manifest.Metadata.Name);
        var installed = await GetAsync(identity, cancellationToken) ?? throw new PackNotFoundException(identity);
        var previous = installed.Value.Definition.ManagedResources.ToDictionary(value => (value.Kind, value.Name));
        var existingTokens = new Dictionary<(string Kind, string Name), string?>();
        foreach (var resource in previous.Values)
        {
            if (!handlers.TryGetValue(resource.Kind, out var handler))
                throw new PackValidationException("pack_resource_kind_unsupported", $"Resource kind '{resource.Kind}' has no installed handler.");
            var currentToken = await handler.GetVersionTokenAsync(resource.Namespace, resource.Name, cancellationToken);
            existingTokens[(resource.Kind, resource.Name)] = currentToken;
            if (currentToken is not null && !string.Equals(currentToken, resource.VersionToken, StringComparison.Ordinal))
                throw new PackResourceModifiedException(resource.Kind, resource.Name);
        }

        var incomingKeys = prepared.Handlers.Keys.Select(value => (value.Kind, value.Name)).ToHashSet();
        var unmanagedConflict = prepared.Preview.Resources.FirstOrDefault(value => value.AlreadyExists && !previous.ContainsKey((value.Kind, value.Name)));
        if (unmanagedConflict is not null) throw new PackResourceConflictException(unmanagedConflict.Kind, unmanagedConflict.Name);

        installed = await UpdateAsync(installed, installed.Value.Definition with { State = InstalledPackState.Updating, ErrorCode = null, ErrorMessage = null }, ProvisioningState.Updating, cancellationToken);
        var managed = installed.Value.Definition.ManagedResources.ToList();
        try
        {
            foreach (var pair in prepared.Handlers.OrderBy(value => value.Value.InstallOrder).ThenBy(value => value.Key.Path, StringComparer.Ordinal))
            {
                var key = (pair.Key.Kind, pair.Key.Name);
                ManagedPackResource applied;
                if (previous.TryGetValue(key, out var current) && existingTokens[key] is not null)
                    applied = await pair.Value.UpdateAsync(pair.Key, current, identity, archive.Manifest.Metadata.Version, cancellationToken);
                else
                    applied = await pair.Value.InstallAsync(pair.Key, identity, identity.Namespace, archive.Manifest.Metadata.Version, cancellationToken);
                managed.RemoveAll(value => value.Kind == key.Kind && value.Name == key.Name);
                managed.Add(applied);
                installed = await UpdateAsync(installed, installed.Value.Definition with { ManagedResources = managed.ToArray() }, ProvisioningState.Updating, cancellationToken);
            }

            foreach (var removed in previous.Values.Where(value => !incomingKeys.Contains((value.Kind, value.Name))).OrderByDescending(value => handlers[value.Kind].InstallOrder))
            {
                if (existingTokens[(removed.Kind, removed.Name)] is not null)
                    await handlers[removed.Kind].DeleteAsync(removed, removalOptions, cancellationToken);
                managed.RemoveAll(value => value.Kind == removed.Kind && value.Name == removed.Name);
                installed = await UpdateAsync(installed, installed.Value.Definition with { ManagedResources = managed.ToArray() }, ProvisioningState.Updating, cancellationToken);
            }

            var sourceArtifact = artifacts is not null && !archive.Content.IsEmpty
                ? await artifacts.SaveAsync(archive.Content, archive.Source, cancellationToken)
                : installed.Value.Definition.SourceArtifact;
            var resolutions = prepared.Preview.Bindings.Where(value => value.Target is not null && value.TargetAvailable)
                .Select(value => new PackBindingResolution(value.Name, value.TargetKind, value.Target!)).ToArray();
            installed = await UpdateAsync(installed, installed.Value.Definition with
            {
                Version = archive.Manifest.Metadata.Version,
                Audience = archive.Manifest.Metadata.Audience,
                Purpose = archive.Manifest.Metadata.Purpose,
                DisplayName = archive.Manifest.Metadata.DisplayName,
                Description = archive.Manifest.Metadata.Description,
                Source = archive.Source,
                SourceArtifact = sourceArtifact,
                InstalledAt = timeProvider.GetUtcNow(),
                State = InstalledPackState.Installed,
                Bindings = resolutions,
                ManagedResources = managed.ToArray(),
                ErrorCode = null,
                ErrorMessage = null
            }, ProvisioningState.Succeeded, cancellationToken);
            await SaveConfigurationAsync(identity, resolutions, cancellationToken);
            return installed;
        }
        catch (Exception exception)
        {
            _ = await UpdateAsync(installed, installed.Value.Definition with { State = InstalledPackState.Degraded, ManagedResources = managed.ToArray(), ErrorCode = "pack_update_failed", ErrorMessage = exception.Message }, ProvisioningState.Failed, CancellationToken.None);
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
}

