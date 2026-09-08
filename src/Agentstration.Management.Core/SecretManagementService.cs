using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Management.Core;

public sealed class SecretManagementException(string message) : Exception(message);
public sealed class SecretResourceNotFoundException(string name) : Exception($"Secret '{name}' was not found.");
public sealed class VaultResourceNotFoundException(string name) : Exception($"Vault '{name}' was not found.");
public sealed class VaultInUseException(string name) : Exception($"Vault '{name}' is referenced by one or more secrets.");
public sealed class VaultAlreadyInitializedException(string name) : Exception($"Vault '{name}' already has a configured master key.");
public sealed class VaultInitializationNotSupportedException(string providerType) : Exception($"Vault provider '{providerType}' does not support Console initialization.");
public sealed record SecretView(SecretResource Resource, SecretValueStatus ValueStatus);
public sealed record VaultView(VaultResource Resource, string Status);

public sealed class SecretManagementService(
    IControlPlaneStore store,
    IResourceReferenceResolver references,
    ResourceScopeOperationService scopeOperations,
    IEnumerable<ISecretVaultProvider> providers) : ISecretResolver
{
    public Task<IReadOnlyList<StoredResource<VaultResource>>> ListVaultsAsync(CancellationToken cancellationToken) => store.ListAllAsync<VaultResource>(ResourceKinds.Vault, cancellationToken);
    public async Task<IReadOnlyList<VaultView>> ListVaultViewsAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll((await ListVaultsAsync(cancellationToken)).Select(value => ViewAsync(value.Value, cancellationToken)));
    public Task<StoredResource<VaultResource>?> GetVaultAsync(string name, CancellationToken cancellationToken) => store.GetAsync<VaultResource>(new(ResourceKinds.Vault, name), cancellationToken);
    public Task<StoredResource<VaultResource>?> GetVaultExactAsync(ResourceScopeRef scopeRef, string name, CancellationToken cancellationToken) =>
        store.GetExactAsync<VaultResource>(ScopedResourceAddress.Create(scopeRef, ResourceNamespace.Default, ResourceKinds.Vault, name), cancellationToken);
    public async Task<VaultView> GetVaultViewAsync(string name, CancellationToken cancellationToken) => await ViewAsync((await GetVaultAsync(name, cancellationToken))?.Value ?? throw new VaultResourceNotFoundException(name), cancellationToken);
    public async Task<VaultView> GetVaultViewExactAsync(ResourceScopeRef scopeRef, string name, CancellationToken cancellationToken) =>
        await ViewAsync((await GetVaultExactAsync(scopeRef, name, cancellationToken))?.Value ?? throw new VaultResourceNotFoundException(name), cancellationToken);
    public async Task<StoredResource<VaultResource>> CreateVaultAsync(VaultResource resource, CancellationToken cancellationToken)
    {
        ValidateVault(resource);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ResourceKinds.Vault);
        return await scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, resource with { ScopeRef = scopeRef, Generation = 1, Status = Succeeded() }, null, true, token), cancellationToken);
    }
    public async Task<StoredResource<VaultResource>> PutVaultAsync(string name, VaultProperties definition, string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetVaultAsync(name, cancellationToken) ?? throw new VaultResourceNotFoundException(name);
        return await PutVaultAsync(existing, definition, etag, cancellationToken);
    }
    public async Task<StoredResource<VaultResource>> PutVaultExactAsync(ResourceScopeRef scopeRef, string name, VaultProperties definition, string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetVaultExactAsync(scopeRef, name, cancellationToken) ?? throw new VaultResourceNotFoundException(name);
        return await PutVaultAsync(existing, definition, etag, cancellationToken);
    }
    private async Task<StoredResource<VaultResource>> PutVaultAsync(StoredResource<VaultResource> existing, VaultProperties definition, string? etag, CancellationToken cancellationToken)
    {
        ValidateVault(existing.Value with { Definition = definition });
        var scopeRef = RequireScope(existing.Value);
        return await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, existing.Value with { Definition = definition, Generation = checked(existing.Value.Generation + 1), Status = Succeeded() }, etag, false, token), cancellationToken);
    }
    public async Task DeleteVaultAsync(string name, string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetVaultAsync(name, cancellationToken) ?? throw new VaultResourceNotFoundException(name);
        await DeleteVaultAsync(existing, etag, cancellationToken);
    }
    public async Task DeleteVaultExactAsync(ResourceScopeRef scopeRef, string name, string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetVaultExactAsync(scopeRef, name, cancellationToken) ?? throw new VaultResourceNotFoundException(name);
        await DeleteVaultAsync(existing, etag, cancellationToken);
    }
    private async Task DeleteVaultAsync(StoredResource<VaultResource> existing, string? etag, CancellationToken cancellationToken)
    {
        var name = existing.Value.Name;
        var scopeRef = RequireScope(existing.Value);
        if ((await store.ListAllAsync<SecretResource>(ResourceKinds.Secret, cancellationToken)).Any(value =>
                value.Value.ScopeRef == scopeRef && value.Value.Definition.Vault.Name == name)) throw new VaultInUseException(name);
        await scopeOperations.WriteAsync(ResourceKinds.Vault, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await store.DeleteExactAsync(ScopedResourceAddress.Create(scopeRef, ResourceNamespace.Default, ResourceKinds.Vault, name), etag, token);
            return true;
        }, cancellationToken);
    }
    public async Task<SecretVaultInitializationResult> InitializeVaultAsync(string name, CancellationToken cancellationToken)
    {
        var vault = (await GetVaultAsync(name, cancellationToken))?.Value ?? throw new VaultResourceNotFoundException(name);
        return await InitializeVaultAsync(vault, cancellationToken);
    }
    public async Task<SecretVaultInitializationResult> InitializeVaultExactAsync(ResourceScopeRef scopeRef, string name, CancellationToken cancellationToken)
    {
        var vault = (await GetVaultExactAsync(scopeRef, name, cancellationToken))?.Value ?? throw new VaultResourceNotFoundException(name);
        return await InitializeVaultAsync(vault, cancellationToken);
    }
    private async Task<SecretVaultInitializationResult> InitializeVaultAsync(VaultResource vault, CancellationToken cancellationToken)
    {
        var initializer = providers.OfType<ISecretVaultInitializer>().SingleOrDefault(value => string.Equals(value.ProviderType, vault.Definition.ProviderType, StringComparison.OrdinalIgnoreCase))
            ?? throw new VaultInitializationNotSupportedException(vault.Definition.ProviderType);
        var scopeRef = RequireScope(vault);
        var result = await scopeOperations.WriteAsync(vault, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => initializer.InitializeAsync(new(scopeRef, vault.Address, vault.Definition.ProviderOptions), token), cancellationToken);
        return result.Created ? result : throw new VaultAlreadyInitializedException(vault.Name);
    }

    public async Task<IReadOnlyList<SecretView>> ListSecretsAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll((await store.ListAllAsync<SecretResource>(ResourceKinds.Secret, cancellationToken)).Select(value => ViewAsync(value.Value, cancellationToken)));
    public Task<StoredResource<SecretResource>?> GetSecretAsync(string name, CancellationToken cancellationToken) => store.GetAsync<SecretResource>(new(ResourceKinds.Secret, name), cancellationToken);
    public Task<StoredResource<SecretResource>?> GetSecretExactAsync(ResourceScopeRef scopeRef, string name, CancellationToken cancellationToken) =>
        store.GetExactAsync<SecretResource>(ScopedResourceAddress.Create(scopeRef, ResourceNamespace.Default, ResourceKinds.Secret, name), cancellationToken);
    public async Task<SecretView> GetSecretViewAsync(string name, CancellationToken cancellationToken) => await ViewAsync((await GetSecretAsync(name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name), cancellationToken);
    public async Task<SecretView> GetSecretViewExactAsync(ResourceScopeRef scopeRef, string name, CancellationToken cancellationToken) =>
        await ViewAsync((await GetSecretExactAsync(scopeRef, name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name), cancellationToken);
    public async Task<StoredResource<SecretResource>> CreateSecretAsync(SecretResource resource, CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ResourceKinds.Secret);
        var scoped = resource with { ScopeRef = scopeRef };
        return await scopeOperations.WriteAsync(scoped, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            await ValidateSecretAsync(scoped, token);
            return await store.PutExactAsync(scopeRef, scoped with { Generation = 1, Status = Succeeded() }, null, true, token);
        }, cancellationToken);
    }
    public async Task<StoredResource<SecretResource>> PutSecretAsync(string name, SecretProperties definition, string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetSecretAsync(name, cancellationToken) ?? throw new SecretResourceNotFoundException(name);
        return await PutSecretAsync(existing, definition, etag, cancellationToken);
    }
    public async Task<StoredResource<SecretResource>> PutSecretExactAsync(ResourceScopeRef scopeRef, string name, SecretProperties definition, string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetSecretExactAsync(scopeRef, name, cancellationToken) ?? throw new SecretResourceNotFoundException(name);
        return await PutSecretAsync(existing, definition, etag, cancellationToken);
    }
    private async Task<StoredResource<SecretResource>> PutSecretAsync(StoredResource<SecretResource> existing, SecretProperties definition, string? etag, CancellationToken cancellationToken)
    {
        var changed = existing.Value with { Definition = definition };
        var scopeRef = RequireScope(existing.Value);
        return await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            await ValidateSecretAsync(changed, token);
            return await store.PutExactAsync(scopeRef, changed with { Generation = checked(existing.Value.Generation + 1), Status = Succeeded() }, etag, false, token);
        }, cancellationToken);
    }
    public async Task SetValueAsync(string name, SecretValue value, CancellationToken cancellationToken)
    {
        var secret = (await GetSecretAsync(name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name);
        await SetValueAsync(secret, value, cancellationToken);
    }
    public async Task SetValueExactAsync(ResourceScopeRef scopeRef, string name, SecretValue value, CancellationToken cancellationToken)
    {
        var secret = (await GetSecretExactAsync(scopeRef, name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name);
        await SetValueAsync(secret, value, cancellationToken);
    }
    private async Task SetValueAsync(SecretResource secret, SecretValue value, CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(secret);
        await scopeOperations.WriteAsync(secret, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var (provider, context) = await ProviderAsync(secret, token);
            await provider.SetAsync(context, secret.Definition.Key, value, token);
            return true;
        }, cancellationToken);
    }
    public async Task DeleteValueAsync(string name, CancellationToken cancellationToken)
    {
        var secret = (await GetSecretAsync(name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name);
        await DeleteValueAsync(secret, cancellationToken);
    }
    public async Task DeleteValueExactAsync(ResourceScopeRef scopeRef, string name, CancellationToken cancellationToken)
    {
        var secret = (await GetSecretExactAsync(scopeRef, name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name);
        await DeleteValueAsync(secret, cancellationToken);
    }
    private async Task DeleteValueAsync(SecretResource secret, CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(secret);
        await scopeOperations.WriteAsync(secret, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            var (provider, context) = await ProviderAsync(secret, token);
            await provider.DeleteAsync(context, secret.Definition.Key, token);
            return true;
        }, cancellationToken);
    }
    public async Task DeleteSecretAsync(string name, string? etag, CancellationToken cancellationToken)
    {
        var secret = (await GetSecretAsync(name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name);
        await DeleteSecretAsync(secret, etag, cancellationToken);
    }
    public async Task DeleteSecretExactAsync(ResourceScopeRef scopeRef, string name, string? etag, CancellationToken cancellationToken)
    {
        var secret = (await GetSecretExactAsync(scopeRef, name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name);
        await DeleteSecretAsync(secret, etag, cancellationToken);
    }
    private async Task DeleteSecretAsync(SecretResource secret, string? etag, CancellationToken cancellationToken)
    {
        var name = secret.Name;
        var scopeRef = RequireScope(secret);
        if ((await GetSecretUsagesAsync(scopeRef, name, cancellationToken)).Count > 0)
            throw new SecretManagementException($"Secret '{name}' is referenced by an extension registration.");
        await DeleteValueAsync(secret, cancellationToken);
        await scopeOperations.WriteAsync(ResourceKinds.Secret, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await store.DeleteExactAsync(ScopedResourceAddress.Create(scopeRef, ResourceNamespace.Default, ResourceKinds.Secret, name), etag, token);
            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ModelProviderUsage>> GetSecretUsagesAsync(string name, CancellationToken cancellationToken)
    {
        var secret = (await GetSecretAsync(name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name);
        return await GetSecretUsagesAsync(RequireScope(secret), name, cancellationToken);
    }

    public async Task<IReadOnlyList<ModelProviderUsage>> GetSecretUsagesAsync(ResourceScopeRef scopeRef, string name, CancellationToken cancellationToken)
    {
        var usages = new List<ModelProviderUsage>();
        foreach (var registration in await store.ListAllAsync<ExtensionRegistrationResource>(ResourceKinds.ExtensionRegistration, cancellationToken))
        {
            var credential = registration.Value.Definition.Credential;
            if (credential is null || !string.Equals(credential.Name, name, StringComparison.Ordinal)) continue;
            var registrationScope = RequireScope(registration.Value);
            var resolved = await references.ResolveAsync<SecretResource>(
                credential,
                registration.Value.Namespace,
                ResourceKinds.Secret,
                registrationScope,
                cancellationToken);
            if (resolved?.Value.ScopeRef == scopeRef)
                usages.Add(new(registration.Value.Kind, registration.Value.Name, registration.Value.Definition.DisplayName));
        }
        return usages;
    }

    public async Task<ResolvedSecret?> ResolveAsync(SecretReference reference, SecretResolutionContext resolution, CancellationToken cancellationToken = default)
    {
        if (reference.Address.Kind != ResourceKinds.Secret)
            throw new SecretManagementException("The referenced resource must be a Secret.");
        var secret = (await references.ResolveAsync<SecretResource>(
            new ResourceReference(reference.Address.Name, reference.ScopeRef, reference.Address.Namespace),
            resolution.Consumer.Namespace,
            ResourceKinds.Secret,
            resolution.ConsumerScopeRef,
            cancellationToken))?.Value;
        if (secret is null) return null;
        var (provider, context) = await ProviderAsync(secret, cancellationToken);
        var value = await provider.GetAsync(context, secret.Definition.Key, cancellationToken);
        return value is null ? null : new ResolvedSecret(secret.Address, context.Vault, value);
    }

    private async Task<SecretView> ViewAsync(SecretResource secret, CancellationToken cancellationToken)
    {
        try { var (provider, context) = await ProviderAsync(secret, cancellationToken); return new(secret, await provider.GetStatusAsync(context, secret.Definition.Key, cancellationToken)); }
        catch (SecretVaultUnavailableException) { return new(secret, SecretValueStatus.VaultUnavailable); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return new(secret, SecretValueStatus.Unavailable); }
    }
    private async Task<VaultView> ViewAsync(VaultResource vault, CancellationToken cancellationToken)
    {
        var provider = providers.SingleOrDefault(value => string.Equals(value.ProviderType, vault.Definition.ProviderType, StringComparison.OrdinalIgnoreCase));
        return new(vault, provider is null ? "unavailable" : await provider.GetHealthAsync(new(RequireScope(vault), vault.Address, vault.Definition.ProviderOptions), cancellationToken));
    }
    private async Task<(ISecretVaultProvider Provider, SecretVaultContext Context)> ProviderAsync(SecretResource secret, CancellationToken cancellationToken)
    {
        var address = secret.Definition.Vault.Resolve(secret.Namespace, ResourceKinds.Vault);
        var secretScopeRef = RequireScope(secret);
        if (secret.Definition.Vault.ScopeRef is { } requestedVaultScope && requestedVaultScope != secretScopeRef)
            throw new SecretManagementException("A Secret and its Vault must belong to the exact same scope.");
        var vault = (await store.GetExactAsync<VaultResource>(
            ScopedResourceAddress.Create(secretScopeRef, address.Namespace, address.Kind, address.Name),
            cancellationToken))?.Value ?? throw new VaultResourceNotFoundException(address.Name);
        var provider = providers.SingleOrDefault(value => string.Equals(value.ProviderType, vault.Definition.ProviderType, StringComparison.OrdinalIgnoreCase)) ?? throw new SecretVaultUnavailableException(vault.Definition.ProviderType);
        return (provider, new(secretScopeRef, vault.Address, vault.Definition.ProviderOptions));
    }

    private static ResourceScopeRef RequireScope(Resource resource)
    {
        return resource.ScopeRef
            ?? throw new SecretManagementException($"Resource '{resource.Address}' has no ownership scope.");
    }
    private static void ValidateVault(VaultResource resource)
    {
        if (resource.Kind != ResourceKinds.Vault || resource.ApiVersion != ManagementApiVersions.CoreV1) throw new SecretManagementException("Invalid Vault resource envelope.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name); ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.DisplayName); ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.ProviderType);
    }
    private async Task ValidateSecretAsync(SecretResource resource, CancellationToken cancellationToken)
    {
        if (resource.Kind != ResourceKinds.Secret || resource.ApiVersion != ManagementApiVersions.CoreV1) throw new SecretManagementException("Invalid Secret resource envelope.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name); ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.DisplayName); ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.Key);
        var scopeRef = RequireScope(resource);
        if (resource.Definition.Vault.ScopeRef is { } requestedVaultScope && requestedVaultScope != scopeRef)
            throw new SecretManagementException("A Secret and its Vault must belong to the exact same scope.");
        var @namespace = resource.Definition.Vault.Namespace ?? resource.Namespace;
        _ = await store.GetExactAsync<VaultResource>(
            ScopedResourceAddress.Create(scopeRef, @namespace, ResourceKinds.Vault, resource.Definition.Vault.Name),
            cancellationToken) ?? throw new VaultResourceNotFoundException(resource.Definition.Vault.Name);
    }

    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
}
