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

public sealed class SecretManagementService(IControlPlaneStore store, IEnumerable<ISecretVaultProvider> providers) : ISecretResolver
{
    public Task<IReadOnlyList<StoredResource<VaultResource>>> ListVaultsAsync(CancellationToken cancellationToken) => store.ListAllAsync<VaultResource>(ResourceKinds.Vault, cancellationToken);
    public async Task<IReadOnlyList<VaultView>> ListVaultViewsAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll((await ListVaultsAsync(cancellationToken)).Select(value => ViewAsync(value.Value, cancellationToken)));
    public Task<StoredResource<VaultResource>?> GetVaultAsync(string name, CancellationToken cancellationToken) => store.GetAsync<VaultResource>(new(ResourceKinds.Vault, name), cancellationToken);
    public async Task<VaultView> GetVaultViewAsync(string name, CancellationToken cancellationToken) => await ViewAsync((await GetVaultAsync(name, cancellationToken))?.Value ?? throw new VaultResourceNotFoundException(name), cancellationToken);
    public async Task<StoredResource<VaultResource>> CreateVaultAsync(VaultResource resource, CancellationToken cancellationToken)
    {
        ValidateVault(resource);
        return await store.PutAsync(resource with { Generation = 1, Status = Succeeded() }, null, true, cancellationToken);
    }
    public async Task<StoredResource<VaultResource>> PutVaultAsync(string name, VaultProperties definition, string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetVaultAsync(name, cancellationToken) ?? throw new VaultResourceNotFoundException(name);
        ValidateVault(existing.Value with { Definition = definition });
        return await store.PutAsync(existing.Value with { Definition = definition, Generation = checked(existing.Value.Generation + 1), Status = Succeeded() }, etag, false, cancellationToken);
    }
    public async Task DeleteVaultAsync(string name, string? etag, CancellationToken cancellationToken)
    {
        _ = await GetVaultAsync(name, cancellationToken) ?? throw new VaultResourceNotFoundException(name);
        if ((await store.ListAllAsync<SecretResource>(ResourceKinds.Secret, cancellationToken)).Any(value => value.Value.Definition.Vault.Name == name)) throw new VaultInUseException(name);
        await store.DeleteAsync(new(ResourceKinds.Vault, name), etag, cancellationToken);
    }
    public async Task<SecretVaultInitializationResult> InitializeVaultAsync(string name, CancellationToken cancellationToken)
    {
        var vault = (await GetVaultAsync(name, cancellationToken))?.Value ?? throw new VaultResourceNotFoundException(name);
        var initializer = providers.OfType<ISecretVaultInitializer>().SingleOrDefault(value => string.Equals(value.ProviderType, vault.Definition.ProviderType, StringComparison.OrdinalIgnoreCase))
            ?? throw new VaultInitializationNotSupportedException(vault.Definition.ProviderType);
        var result = await initializer.InitializeAsync(new(vault.TenantId, vault.WorkspaceId, vault.Address, vault.Definition.ProviderOptions), cancellationToken);
        return result.Created ? result : throw new VaultAlreadyInitializedException(name);
    }

    public async Task<IReadOnlyList<SecretView>> ListSecretsAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll((await store.ListAllAsync<SecretResource>(ResourceKinds.Secret, cancellationToken)).Select(value => ViewAsync(value.Value, cancellationToken)));
    public Task<StoredResource<SecretResource>?> GetSecretAsync(string name, CancellationToken cancellationToken) => store.GetAsync<SecretResource>(new(ResourceKinds.Secret, name), cancellationToken);
    public async Task<SecretView> GetSecretViewAsync(string name, CancellationToken cancellationToken) => await ViewAsync((await GetSecretAsync(name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name), cancellationToken);
    public async Task<StoredResource<SecretResource>> CreateSecretAsync(SecretResource resource, CancellationToken cancellationToken)
    {
        await ValidateSecretAsync(resource, cancellationToken);
        return await store.PutAsync(resource with { Generation = 1, Status = Succeeded() }, null, true, cancellationToken);
    }
    public async Task<StoredResource<SecretResource>> PutSecretAsync(string name, SecretProperties definition, string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetSecretAsync(name, cancellationToken) ?? throw new SecretResourceNotFoundException(name);
        var changed = existing.Value with { Definition = definition };
        await ValidateSecretAsync(changed, cancellationToken);
        return await store.PutAsync(changed with { Generation = checked(existing.Value.Generation + 1), Status = Succeeded() }, etag, false, cancellationToken);
    }
    public async Task SetValueAsync(string name, SecretValue value, CancellationToken cancellationToken)
    {
        var secret = (await GetSecretAsync(name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name);
        var (provider, context) = await ProviderAsync(secret, cancellationToken);
        await provider.SetAsync(context, secret.Definition.Key, value, cancellationToken);
    }
    public async Task DeleteValueAsync(string name, CancellationToken cancellationToken)
    {
        var secret = (await GetSecretAsync(name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name);
        var (provider, context) = await ProviderAsync(secret, cancellationToken);
        await provider.DeleteAsync(context, secret.Definition.Key, cancellationToken);
    }
    public async Task DeleteSecretAsync(string name, string? etag, CancellationToken cancellationToken)
    {
        if ((await store.ListAllAsync<ExtensionRegistrationResource>(ResourceKinds.ExtensionRegistration, cancellationToken)).Any(value => value.Value.Definition.Credential?.Name == name))
            throw new SecretManagementException($"Secret '{name}' is referenced by an extension registration.");
        await DeleteValueAsync(name, cancellationToken);
        await store.DeleteAsync(new(ResourceKinds.Secret, name), etag, cancellationToken);
    }

    public async Task<IReadOnlyList<ModelProviderUsage>> GetSecretUsagesAsync(string name, CancellationToken cancellationToken) =>
        (await store.ListAllAsync<ExtensionRegistrationResource>(ResourceKinds.ExtensionRegistration, cancellationToken))
            .Where(value => value.Value.Definition.Credential?.Name == name)
            .Select(value => new ModelProviderUsage(value.Value.Kind, value.Value.Name, value.Value.Definition.DisplayName))
            .ToArray();

    public async Task<ResolvedSecret?> ResolveAsync(ResourceAddress address, SecretResolutionContext resolution, CancellationToken cancellationToken = default)
    {
        if (address.Kind != ResourceKinds.Secret) throw new SecretManagementException("The referenced resource must be a Secret.");
        var secret = (await store.GetAsync<SecretResource>(new(address.Kind, address.Name, address.Namespace), cancellationToken))?.Value;
        if (secret is null) return null;
        if (secret.TenantId != resolution.TenantId || secret.WorkspaceId != resolution.WorkspaceId) throw new SecretAccessDeniedException(address);
        var (provider, context) = await ProviderAsync(secret, cancellationToken);
        var value = await provider.GetAsync(context, secret.Definition.Key, cancellationToken);
        return value is null ? null : new ResolvedSecret(address, context.Vault, value);
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
        return new(vault, provider is null ? "unavailable" : await provider.GetHealthAsync(new(vault.TenantId, vault.WorkspaceId, vault.Address, vault.Definition.ProviderOptions), cancellationToken));
    }
    private async Task<(ISecretVaultProvider Provider, SecretVaultContext Context)> ProviderAsync(SecretResource secret, CancellationToken cancellationToken)
    {
        if (secret.Definition.Vault.WorkspaceRef is not null) throw new SecretManagementException("Cross-workspace vault references are not supported.");
        var address = secret.Definition.Vault.Resolve(secret.Namespace, ResourceKinds.Vault);
        var vault = (await store.GetAsync<VaultResource>(new(address.Kind, address.Name, address.Namespace), cancellationToken))?.Value ?? throw new VaultResourceNotFoundException(address.Name);
        var provider = providers.SingleOrDefault(value => string.Equals(value.ProviderType, vault.Definition.ProviderType, StringComparison.OrdinalIgnoreCase)) ?? throw new SecretVaultUnavailableException(vault.Definition.ProviderType);
        return (provider, new(vault.TenantId, vault.WorkspaceId, vault.Address, vault.Definition.ProviderOptions));
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
        if (resource.Definition.Vault.WorkspaceRef is not null) throw new SecretManagementException("Cross-workspace vault references are not supported.");
        _ = await store.GetAsync<VaultResource>(new(ResourceKinds.Vault, resource.Definition.Vault.Name, resource.Definition.Vault.Namespace ?? resource.Namespace), cancellationToken) ?? throw new VaultResourceNotFoundException(resource.Definition.Vault.Name);
    }
    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
}
