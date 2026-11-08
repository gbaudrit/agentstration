using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Storage;

public sealed class StorageTypeManagementService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    DescendantResourceUseAuthorizer useAuthorizer,
    IResourceGovernanceContext governance,
    IStorageTypeAvailabilityProvider availability,
    IRequestContextScopeFactory requestScopes)
{
    public async Task<IReadOnlyList<StoredResource<StorageTypeResource>>> ListAsync(
        ResourceScopeRef scopeRef, int skip, int take, CancellationToken token)
    {
        var resources = await store.ListVisibleAsync<StorageTypeResource>(scopeRef, StorageResourceKinds.StorageType, skip, take, token);
        var result = new List<StoredResource<StorageTypeResource>>();
        foreach (var resource in resources) result.Add(await ObserveAsync(resource, token));
        return result;
    }

    public async Task<StoredResource<StorageTypeResource>?> GetExactAsync(
        ResourceScopeRef scopeRef, ResourceNamespace ns, string name, CancellationToken token)
    {
        var resource = await store.GetExactAsync<StorageTypeResource>(Address(scopeRef, ns, name), token);
        return resource is null ? null : await ObserveAsync(resource, token);
    }

    private async Task<StoredResource<StorageTypeResource>> ObserveAsync(StoredResource<StorageTypeResource> resource, CancellationToken token)
    {
        var versions = new List<StorageTypeVersionAvailability>();
        foreach (var version in resource.Value.Definition.Versions)
            versions.Add(new(version.Version, !resource.Value.Definition.Enabled ? StorageAvailability.Disabled
                : await availability.IsAvailableAsync(resource.Value, version, token) ? StorageAvailability.Ready : StorageAvailability.Unavailable));
        return resource with { Value = resource.Value with { ObservedVersions = versions } };
    }

    public Task ValidateForCreateAsync(StorageTypeResource resource, CancellationToken token) =>
        ValidateAsync(resource with { ScopeRef = resource.ScopeRef
            ?? scopeOperations.DefaultScopeRef(StorageResourceKinds.StorageType) }, token);

    public async Task<StoredResource<StorageTypeResource>> CreateAsync(StorageTypeResource resource, CancellationToken token)
    {
        var scope = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(StorageResourceKinds.StorageType);
        var scoped = resource with { ScopeRef = scope };
        return await scopeOperations.WriteAsync(scoped, scope, AuthorizationPermissions.ResourcesWrite, async ct =>
        {
            await ValidateAsync(scoped, ct);
            return await store.PutExactAsync(scope, scoped with
            {
                Generation = 1, Status = new() { ProvisioningState = ProvisioningState.Succeeded }
            }, null, true, ct);
        }, token);
    }

    public async Task<StoredResource<StorageTypeResource>> PutExactAsync(ResourceScopeRef scope, ResourceNamespace ns,
        string name, StorageTypeProperties definition, string? etag, CancellationToken token)
    {
        var existing = await GetExactAsync(scope, ns, name, token)
            ?? throw new StorageException("storage_type_not_found", "Storage type not found.", 404);
        StorageValidation.ETag(etag, existing.ETag);
        var merged = Merge(existing.Value.Definition, definition);
        var changed = existing.Value with { Definition = merged, Generation = checked(existing.Value.Generation + 1) };
        return await scopeOperations.WriteAsync(existing.Value, scope, AuthorizationPermissions.ResourcesWrite, async ct =>
        {
            await ValidateAsync(changed, ct);
            return await store.PutExactAsync(scope, changed, etag, false, ct);
        }, token);
    }

    /// <summary>Trusted Core/extension adapters synchronize only their own types.</summary>
    public async Task<StoredResource<StorageTypeResource>> SynchronizeManagedAsync(StorageTypeResource desired,
        CancellationToken token)
    {
        var authority = governance.Current?.ManagedBy
            ?? throw new ResourceGovernanceViolationException("resource_governance_authority_required",
                "Type synchronization requires an active management authority.");
        var scope = desired.ScopeRef ?? throw new StorageException("storage_scope_required", "An exact scope is required.");
        var existing = await GetExactAsync(scope, desired.Namespace, desired.Name, token);
        if (existing is null)
        {
            try { return await CreateAsync(desired, token); }
            catch (ResourceConcurrencyException)
            {
                existing = await GetExactAsync(scope, desired.Namespace, desired.Name, token);
                if (existing is null) throw;
            }
        }
        if (existing.Value.Governance.ManagedBy?.Kind != authority.Kind
            || existing.Value.Governance.ManagedBy.Reference != authority.Reference)
            throw new ResourceGovernanceViolationException("resource_managed", "The type belongs to another management authority.");
        var merged = Merge(existing.Value.Definition, desired.Definition);
        if (JsonSerializer.Serialize(existing.Value.Definition) == JsonSerializer.Serialize(merged)) return existing;
        return await PutExactAsync(scope, desired.Namespace, desired.Name, merged, existing.ETag, token);
    }

    public async Task DeleteExactAsync(ResourceScopeRef scope, ResourceNamespace ns, string name, string? etag,
        CancellationToken token)
    {
        var existing = await GetExactAsync(scope, ns, name, token)
            ?? throw new StorageException("storage_type_not_found", "Storage type not found.", 404);
        StorageValidation.ETag(etag, existing.ETag);
        await scopeOperations.WriteAsync(existing.Value, scope, AuthorizationPermissions.ResourcesDelete, async ct =>
        {
            if (await HasUsagesAsync(existing.Value.Uid, ct))
                throw new StorageException("storage_type_in_use", "The type is referenced by a Storage.", 409);
            await store.DeleteExactAsync(Address(scope, ns, name), etag, ct);
            return true;
        }, token);
    }

    private async Task<bool> HasUsagesAsync(Guid typeUid, CancellationToken token)
    {
        using var system = requestScopes.PushSystem();
        return (await store.ListAllAsync<StorageResource>(StorageResourceKinds.Storage, token))
            .Any(item => item.Value.Definition.Type.TypeUid == typeUid);
    }

    private async Task ValidateAsync(StorageTypeResource resource, CancellationToken token)
    {
        if (resource.Kind != StorageResourceKinds.StorageType || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw new StorageException("storage_type_invalid", "Invalid StorageType resource envelope.");
        StorageValidation.Name(resource.Name);
        var definition = resource.Definition ?? throw new StorageException("storage_type_invalid", "Type properties are required.");
        StorageValidation.Display(definition.DisplayName, definition.Description);
        var scope = resource.ScopeRef!.Value;
        ResourceScopePolicy.EnsureAllowed(resource, scope);
        await useAuthorizer.ValidateAsync(scope, definition.UsePolicy, token);
        if (definition.Versions is not { Count: >= 1 and <= 32 }
            || definition.Versions.Any(v => v is null)
            || definition.Versions.Select(v => v.Version).Distinct(StringComparer.Ordinal).Count() != definition.Versions.Count
            || !definition.Versions.Any(v => v.Version == definition.PreferredVersion))
            throw new StorageException("storage_type_versions_invalid", "Types require 1 to 32 unique versions and a published preferred version.");
        foreach (var version in definition.Versions)
        {
            if (version.Version is not { Length: >= 1 and <= 64 }
                || version.Version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))
                throw new StorageException("storage_type_version_invalid", "Invalid type version.");
            await StorageValidation.SchemaAsync(version.ConfigurationSchema, token);
            await StorageValidation.SchemaAsync(version.EnvelopeSchema, token);
            if (StorageDigests.Schema(version.ConfigurationSchema) != version.SchemaDigest
                || StorageDigests.Schema(version.EnvelopeSchema) != version.EnvelopeSchemaDigest)
                throw new StorageException("storage_type_digest_invalid", "Schema digests must match the published schemas.");
            if (version.Requirements is null || version.Requirements.Count > 64
                || version.Requirements.Any(r => r is null)
                || version.Requirements.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != version.Requirements.Count
                || (!version.RequiresConnectedService && version.Requirements.Count != 0))
                throw new StorageException("storage_requirements_invalid", "Requirements must be unique, bounded and require a Connected Service.");
            foreach (var requirement in version.Requirements)
            {
                StorageValidation.MemberId(requirement.Id);
                if (!Enum.IsDefined(requirement.Type) || !Enum.IsDefined(requirement.Protection)
                    || requirement.Format?.Length > 128)
                    throw new StorageException("storage_requirement_invalid", "Invalid requirement type, protection or format.");
            }
            if (version.Capabilities is null || version.Limits is null
                || version.Limits.MaximumPageSize is < 1 or > 1000
                || version.Limits.MaximumReadBytes is < 1 or > 16 * 1024 * 1024
                || version.Limits.MaximumWriteBytes is < 1 or > 16 * 1024 * 1024
                || (version.Capabilities.DurableRetention && !version.Capabilities.StableVersions))
                throw new StorageException("storage_capabilities_invalid", "Invalid capabilities or bounded limits.");
            var authority = governance.Current?.ManagedBy ?? resource.Governance.ManagedBy;
            if (version.Contribution is { } proof)
            {
                if (authority?.Kind != ResourceManagementAuthorityKind.ExtensionInstallation
                    || authority.Reference != $"{proof.Provider.ScopeRef.Value}/{proof.Provider.Kind}/{proof.Provider.Namespace}/{proof.Provider.Name}"
                    || proof.Provider.Kind != "ExtensionRegistration" || proof.ProviderUid == Guid.Empty || proof.ProviderGeneration < 1
                    || proof.Provider.ScopeRef != scope || proof.ContributionId != resource.Name
                    || resource.Namespace.Value != (governance.Current ?? resource.Governance).Origin.Reference
                    || string.IsNullOrWhiteSpace(proof.ContributionId) || proof.ContributionId.Length > 128
                    || proof.Version != version.Version || string.IsNullOrWhiteSpace(proof.Digest) || proof.Digest.Length > 256)
                    throw new StorageException("storage_contribution_invalid", "Contribution evidence must identify the exact managing extension.");
            }
            else if (authority?.Kind == ResourceManagementAuthorityKind.ExtensionInstallation)
                throw new StorageException("storage_contribution_required", "Extension types require exact contribution evidence.");
        }
    }

    private static StorageTypeProperties Merge(StorageTypeProperties existing, StorageTypeProperties desired)
    {
        if (desired?.Versions is null || desired.Versions.Any(v => v is null)
            || desired.Versions.Select(v => v.Version).Distinct(StringComparer.Ordinal).Count() != desired.Versions.Count)
            throw new StorageException("storage_type_versions_invalid", "Unique non-null version descriptors are required.");
        var versions = existing.Versions.ToDictionary(v => v.Version, StringComparer.Ordinal);
        foreach (var version in desired.Versions)
        {
            if (versions.TryGetValue(version.Version, out var previous)
                && StorageDigests.Version(previous) != StorageDigests.Version(version))
                throw new StorageException("storage_type_version_immutable", "Published Storage type versions are immutable.");
            versions[version.Version] = version;
        }
        return desired with { Versions = versions.Values.OrderBy(v => v.Version, StringComparer.Ordinal).ToArray() };
    }

    private static ScopedResourceAddress Address(ResourceScopeRef scope, ResourceNamespace ns, string name) =>
        new(scope, ns, StorageResourceKinds.StorageType, name);
}
