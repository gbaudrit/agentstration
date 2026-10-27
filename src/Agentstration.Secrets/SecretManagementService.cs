using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Sources.Contracts;

namespace Agentstration.Secrets;

public sealed class SecretManagementException(string message) : Exception(message);
public sealed class SecretResourceNotFoundException(string name) : Exception($"Secret '{name}' was not found.");
public sealed class VaultResourceNotFoundException(string name) : Exception($"Vault '{name}' was not found.");
public sealed class VaultInUseException(string name) : Exception($"Vault '{name}' is referenced by one or more secrets.");
public sealed class VaultAlreadyInitializedException(string name) : Exception($"Vault '{name}' already has a configured master key.");
public sealed class VaultInitializationNotSupportedException(string providerType) : Exception($"Vault provider '{providerType}' does not support Console initialization.");
public sealed record SecretView(SecretResource Resource, SecretValueStatus ValueStatus);
public sealed record VaultView(VaultResource Resource, string Status);
public sealed record SecretUsage(string Kind, string Name, string DisplayName);

public sealed class SecretManagementService(
    IResourceStore store,
    IResourceReferenceResolver references,
    IResourceScopeOperations scopeOperations,
    ICurrentRequestContext currentRequestContext,
    IRequestContextScopeFactory requestContexts,
    DescendantResourceUseAuthorizer useAuthorizer,
    IResourceAccessPolicyService accessPolicies,
    IEnumerable<ISecretVaultProvider> providers) : ISecretResolver, ISecretAccessAuthorizer
{
    public async Task<IReadOnlyList<StoredResource<VaultResource>>> ListVaultsAsync(CancellationToken cancellationToken)
    {
        var visible = new List<StoredResource<VaultResource>>();
        foreach (var stored in await store.ListAllAsync<VaultResource>(SecretResourceKinds.Vault, cancellationToken))
        {
            var hydrated = await HydrateVaultAsync(stored, cancellationToken);
            if (await CanDiscoverAsync(hydrated.Value, cancellationToken)) visible.Add(hydrated);
        }
        return visible;
    }
    public async Task<IReadOnlyList<VaultView>> ListVaultViewsAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll((await ListVaultsAsync(cancellationToken)).Select(value => ViewAsync(value.Value, cancellationToken)));
    public async Task<IReadOnlyList<VaultView>> ListVaultViewsAsync(ResourceScopeRef consumerScopeRef, CancellationToken cancellationToken)
    {
        await EnsureReadableScopeAsync(SecretResourceKinds.Vault, consumerScopeRef, cancellationToken);
        var visible = new List<VaultView>();
        foreach (var stored in await store.ListAllAsync<VaultResource>(SecretResourceKinds.Vault, cancellationToken))
        {
            var vault = (await HydrateVaultAsync(stored, cancellationToken)).Value;
            if ((await accessPolicies.EvaluateAsync(AccessIdentity(vault), consumerScopeRef,
                    ResourceAccessAction.Discover, cancellationToken)).Allowed)
                visible.Add(await ViewAsync(vault, cancellationToken));
        }
        return visible;
    }
    public async Task<StoredResource<VaultResource>?> GetVaultAsync(string name, CancellationToken cancellationToken) =>
        await HydrateVaultNullableAsync(await store.GetAsync<VaultResource>(new(SecretResourceKinds.Vault, name), cancellationToken), cancellationToken);
    public async Task<StoredResource<VaultResource>?> GetVaultExactAsync(ResourceScopeRef scopeRef, string name, CancellationToken cancellationToken) =>
        await HydrateVaultNullableAsync(await store.GetExactAsync<VaultResource>(ScopedResourceAddress.Create(scopeRef, ResourceNamespace.Default, SecretResourceKinds.Vault, name), cancellationToken), cancellationToken);
    public async Task<VaultView> GetVaultViewAsync(string name, CancellationToken cancellationToken) => await ViewAsync((await GetVaultAsync(name, cancellationToken))?.Value ?? throw new VaultResourceNotFoundException(name), cancellationToken);
    public async Task<VaultView> GetVaultViewExactAsync(ResourceScopeRef scopeRef, string name, CancellationToken cancellationToken) =>
        await ViewAsync((await GetVaultExactAsync(scopeRef, name, cancellationToken))?.Value ?? throw new VaultResourceNotFoundException(name), cancellationToken);
    public async Task<StoredResource<VaultResource>> CreateVaultAsync(VaultResource resource, CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(SecretResourceKinds.Vault);
        return await scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            ValidateVault(resource);
            await useAuthorizer.ValidateAsync(scopeRef, resource.Definition.UsePolicy, token);
            var stored = await store.PutExactAsync(scopeRef, resource with
            {
                ScopeRef = scopeRef,
                Definition = resource.Definition with { UsePolicy = new() },
                Generation = 1,
                Status = Succeeded()
            }, null, true, token);
            await PersistLegacyPolicyAsync(stored.Value, resource.Definition.UsePolicy, token);
            return await HydrateVaultAsync(stored, token);
        }, cancellationToken);
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
        var scopeRef = RequireScope(existing.Value);
        return await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            ValidateVault(existing.Value with { Definition = definition });
            await useAuthorizer.ValidateAsync(scopeRef, definition.UsePolicy, token);
            var stored = await store.PutExactAsync(scopeRef, existing.Value with
            {
                Definition = definition with { UsePolicy = new() },
                Generation = checked(existing.Value.Generation + 1),
                Status = Succeeded()
            }, etag, false, token);
            await PersistLegacyPolicyAsync(stored.Value, definition.UsePolicy, token);
            return await HydrateVaultAsync(stored, token);
        }, cancellationToken);
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
        await scopeOperations.WriteAsync(SecretResourceKinds.Vault, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            using (requestContexts.PushSystem())
            {
                if ((await store.ListAllAsync<SecretResource>(SecretResourceKinds.Secret, token)).Any(value =>
                        (value.Value.Definition.Vault.ScopeRef ?? value.Value.ScopeRef) == scopeRef
                        && value.Value.Definition.Vault.Name == name
                        && (value.Value.Definition.Vault.Namespace ?? value.Value.Namespace) == existing.Value.Namespace))
                    throw new VaultInUseException(name);
            }
            await store.DeleteExactAsync(ScopedResourceAddress.Create(scopeRef, existing.Value.Namespace, SecretResourceKinds.Vault, name), etag, token);
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

    public async Task<IReadOnlyList<SecretView>> ListSecretsAsync(CancellationToken cancellationToken)
    {
        var visible = new List<SecretView>();
        foreach (var stored in await store.ListAllAsync<SecretResource>(SecretResourceKinds.Secret, cancellationToken))
        {
            var secret = (await HydrateSecretAsync(stored, cancellationToken)).Value;
            if (await CanDiscoverAsync(secret, cancellationToken))
                visible.Add(await ViewAsync(secret, cancellationToken));
        }
        return visible;
    }
    public async Task<IReadOnlyList<SecretView>> ListSecretsAsync(ResourceScopeRef consumerScopeRef, CancellationToken cancellationToken)
    {
        await EnsureReadableScopeAsync(SecretResourceKinds.Secret, consumerScopeRef, cancellationToken);
        var visible = new List<SecretView>();
        foreach (var stored in await store.ListAllAsync<SecretResource>(SecretResourceKinds.Secret, cancellationToken))
        {
            var secret = (await HydrateSecretAsync(stored, cancellationToken)).Value;
            if ((await accessPolicies.EvaluateAsync(AccessIdentity(secret), consumerScopeRef,
                    ResourceAccessAction.Discover, cancellationToken)).Allowed)
                visible.Add(await ViewAsync(secret, cancellationToken));
        }
        return visible;
    }
    public async Task<StoredResource<SecretResource>?> GetSecretAsync(string name, CancellationToken cancellationToken) =>
        await HydrateSecretNullableAsync(await store.GetAsync<SecretResource>(new(SecretResourceKinds.Secret, name), cancellationToken), cancellationToken);
    public async Task<StoredResource<SecretResource>?> GetSecretExactAsync(ResourceScopeRef scopeRef, string name, CancellationToken cancellationToken) =>
        await HydrateSecretNullableAsync(await store.GetExactAsync<SecretResource>(ScopedResourceAddress.Create(scopeRef, ResourceNamespace.Default, SecretResourceKinds.Secret, name), cancellationToken), cancellationToken);
    public async Task<SecretView> GetSecretViewAsync(string name, CancellationToken cancellationToken) => await ViewAsync((await GetSecretAsync(name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name), cancellationToken);
    public async Task<SecretView> GetSecretViewExactAsync(ResourceScopeRef scopeRef, string name, CancellationToken cancellationToken) =>
        await ViewAsync((await GetSecretExactAsync(scopeRef, name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name), cancellationToken);
    public async Task<StoredResource<SecretResource>> CreateSecretAsync(SecretResource resource, CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(SecretResourceKinds.Secret);
        var scoped = resource with { ScopeRef = scopeRef };
        return await scopeOperations.WriteAsync(scoped, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            await ValidateSecretAsync(scoped, token);
            var stored = await store.PutExactAsync(scopeRef, scoped with
            {
                Definition = scoped.Definition with { UsePolicy = new() },
                Generation = 1,
                Status = Succeeded()
            }, null, true, token);
            await PersistLegacyPolicyAsync(stored.Value, scoped.Definition.UsePolicy, token);
            return await HydrateSecretAsync(stored, token);
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
            var stored = await store.PutExactAsync(scopeRef, changed with
            {
                Definition = definition with { UsePolicy = new() },
                Generation = checked(existing.Value.Generation + 1),
                Status = Succeeded()
            }, etag, false, token);
            await PersistLegacyPolicyAsync(stored.Value, definition.UsePolicy, token);
            return await HydrateSecretAsync(stored, token);
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
        await scopeOperations.WriteAsync(SecretResourceKinds.Secret, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            using (requestContexts.PushSystem())
            {
                if ((await GetSecretUsagesAsync(scopeRef, name, token)).Count > 0)
                    throw new SecretManagementException($"Secret '{name}' is referenced by a managed resource.");
            }
            await DeleteValueAsync(secret, token);
            await store.DeleteExactAsync(ScopedResourceAddress.Create(scopeRef, secret.Namespace, SecretResourceKinds.Secret, name), etag, token);
            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SecretUsage>> GetSecretUsagesAsync(string name, CancellationToken cancellationToken)
    {
        var secret = (await GetSecretAsync(name, cancellationToken))?.Value ?? throw new SecretResourceNotFoundException(name);
        return await GetSecretUsagesAsync(RequireScope(secret), name, cancellationToken);
    }

    public async Task<IReadOnlyList<SecretUsage>> GetSecretUsagesAsync(ResourceScopeRef scopeRef, string name, CancellationToken cancellationToken)
    {
        var usages = new List<SecretUsage>();
        foreach (var provider in await store.ListAllAsync<ModelProviderResource>(ModelResourceKinds.ModelProvider, cancellationToken))
        {
            if (provider.Value.Definition.ValueBindings.Any(binding =>
                    binding.Kind == ModelProviderValueBindingKind.Secret
                    && binding.Secret is { } reference
                    && reference.ScopeRef == scopeRef
                    && reference.Address.Namespace == ResourceNamespace.Default
                    && string.Equals(reference.Address.Name, name, StringComparison.Ordinal)))
                usages.Add(new(provider.Value.Kind, provider.Value.Name, provider.Value.Definition.DisplayName));
        }
        foreach (var profile in await store.ListAllAsync<ModelProfileResource>(ModelResourceKinds.ModelProfile, cancellationToken))
        {
            if (profile.Value.Definition.SecretBindings.Any(binding =>
                    binding.Secret.ScopeRef == scopeRef
                    && binding.Secret.Address.Namespace == ResourceNamespace.Default
                    && string.Equals(binding.Secret.Address.Name, name, StringComparison.Ordinal)))
                usages.Add(new(profile.Value.Kind, profile.Value.Name, profile.Value.Definition.DisplayName));
        }
        foreach (var registration in await store.ListAllAsync<ExtensionRegistrationResource>(ExtensionKinds.ExtensionRegistration, cancellationToken))
        {
            var credential = registration.Value.Definition.Credential;
            if (credential is null || !string.Equals(credential.Name, name, StringComparison.Ordinal)) continue;
            var registrationScope = RequireScope(registration.Value);
            var resolved = await references.ResolveAsync<SecretResource>(
                credential,
                registration.Value.Namespace,
                SecretResourceKinds.Secret,
                registrationScope,
                cancellationToken);
            if (resolved?.Value.ScopeRef == scopeRef)
                usages.Add(new(registration.Value.Kind, registration.Value.Name, registration.Value.Definition.DisplayName));
        }
        if (scopeRef == ResourceScopeRef.Instance)
        {
            foreach (var registration in await store.ListExactAsync<SourceRegistryRegistrationResource>(
                         ResourceScopeRef.Instance,
                         SourceRegistryKinds.SourceRegistryRegistration,
                         0,
                         1000,
                         cancellationToken))
            {
                var credential = registration.Value.Definition.Credential;
                if (credential is null || !string.Equals(credential.Name, name, StringComparison.Ordinal)) continue;
                var address = credential.Resolve(registration.Value.Namespace, SecretResourceKinds.Secret);
                if (address.Namespace == ResourceNamespace.Default)
                    usages.Add(new(registration.Value.Kind, registration.Value.Name, registration.Value.Definition.DisplayName));
            }
        }
        return usages;
    }

    public async Task<ResolvedSecret?> ResolveAsync(SecretReference reference, SecretResolutionContext resolution, CancellationToken cancellationToken = default)
    {
        var authorized = await AuthorizeCoreAsync(reference, resolution, cancellationToken);
        if (authorized is null) return null;
        var (secret, provider, context) = authorized.Value;
        var value = await provider.GetAsync(context, secret.Definition.Key, cancellationToken);
        return value is null ? null : new ResolvedSecret(secret.Address, context.Vault, value);
    }

    public async Task<SecretValueStatus> GetAuthorizedStatusAsync(SecretReference reference, SecretResolutionContext resolution, CancellationToken cancellationToken = default)
    {
        var authorized = await AuthorizeCoreAsync(reference, resolution, cancellationToken);
        if (authorized is null) return SecretValueStatus.Missing;
        var (secret, provider, context) = authorized.Value;
        return await provider.GetStatusAsync(context, secret.Definition.Key, cancellationToken);
    }

    private async Task<(SecretResource Secret, ISecretVaultProvider Provider, SecretVaultContext Context)?> AuthorizeCoreAsync(
        SecretReference reference,
        SecretResolutionContext resolution,
        CancellationToken cancellationToken)
    {
        if (reference.Address.Kind != SecretResourceKinds.Secret)
            throw new SecretManagementException("The referenced resource must be a Secret.");
        // An omitted scope retains the legacy same-scope reference, without searching ancestors.
        var secretScope = reference.ScopeRef ?? resolution.ConsumerScopeRef;
        if (!await useAuthorizer.IsSameOrDescendantAsync(secretScope, resolution.ConsumerScopeRef, cancellationToken))
            throw new SecretAccessDeniedException(reference.Address);
        var secret = (await store.GetExactAsync<SecretResource>(ScopedResourceAddress.Create(
            secretScope, reference.Address.Namespace, SecretResourceKinds.Secret, reference.Address.Name), cancellationToken))?.Value;
        if (secret is null) return null;
        if (!(await accessPolicies.EvaluateAsync(AccessIdentity(secret), resolution.ConsumerScopeRef,
                ResourceAccessAction.Use, cancellationToken)).Allowed)
            throw new SecretAccessDeniedException(reference.Address);
        var (provider, context) = await ProviderAsync(secret, cancellationToken);
        return (secret, provider, context);
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
        var address = secret.Definition.Vault.Resolve(secret.Namespace, SecretResourceKinds.Vault);
        var secretScopeRef = RequireScope(secret);
        var vaultScopeRef = secret.Definition.Vault.ScopeRef ?? secretScopeRef;
        if (!await useAuthorizer.IsSameOrDescendantAsync(vaultScopeRef, secretScopeRef, cancellationToken))
            throw new SecretAccessDeniedException(secret.Address);
        var vault = (await store.GetExactAsync<VaultResource>(
            ScopedResourceAddress.Create(vaultScopeRef, address.Namespace, address.Kind, address.Name),
            cancellationToken))?.Value ?? throw new VaultResourceNotFoundException(address.Name);
        if (!(await accessPolicies.EvaluateAsync(AccessIdentity(vault), secretScopeRef,
                ResourceAccessAction.Use, cancellationToken)).Allowed)
            throw new SecretAccessDeniedException(secret.Address);
        var provider = providers.SingleOrDefault(value => string.Equals(value.ProviderType, vault.Definition.ProviderType, StringComparison.OrdinalIgnoreCase)) ?? throw new SecretVaultUnavailableException(vault.Definition.ProviderType);
        return (provider, new(vaultScopeRef, vault.Address, vault.Definition.ProviderOptions));
    }

    private static ResourceScopeRef RequireScope(Resource resource)
    {
        return resource.ScopeRef
            ?? throw new SecretManagementException($"Resource '{resource.Address}' has no ownership scope.");
    }
    private static void ValidateVault(VaultResource resource)
    {
        if (resource.Kind != SecretResourceKinds.Vault || resource.ApiVersion != ResourceApiVersions.CoreV1) throw new SecretManagementException("Invalid Vault resource envelope.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name); ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.DisplayName); ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.ProviderType);
    }
    private async Task ValidateSecretAsync(SecretResource resource, CancellationToken cancellationToken)
    {
        if (resource.Kind != SecretResourceKinds.Secret || resource.ApiVersion != ResourceApiVersions.CoreV1) throw new SecretManagementException("Invalid Secret resource envelope.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name); ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.DisplayName); ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.Key);
        var scopeRef = RequireScope(resource);
        await useAuthorizer.ValidateAsync(scopeRef, resource.Definition.UsePolicy, cancellationToken);
        var @namespace = resource.Definition.Vault.Namespace ?? resource.Namespace;
        var vaultScope = resource.Definition.Vault.ScopeRef ?? scopeRef;
        var vault = (await store.GetExactAsync<VaultResource>(ScopedResourceAddress.Create(vaultScope, @namespace,
            SecretResourceKinds.Vault, resource.Definition.Vault.Name), cancellationToken))?.Value
            ?? throw new VaultResourceNotFoundException(resource.Definition.Vault.Name);
        if (!(await accessPolicies.EvaluateAsync(AccessIdentity(vault), scopeRef,
                ResourceAccessAction.Use, cancellationToken)).Allowed)
            throw new SecretManagementException("The Secret's scope has no use grant for its Vault.");
    }

    private async Task<StoredResource<VaultResource>?> HydrateVaultNullableAsync(
        StoredResource<VaultResource>? stored, CancellationToken cancellationToken) =>
        stored is null ? null : await HydrateVaultAsync(stored, cancellationToken);

    private async Task<StoredResource<VaultResource>> HydrateVaultAsync(
        StoredResource<VaultResource> stored, CancellationToken cancellationToken)
    {
        var policy = await accessPolicies.GetAsync(stored.Value.Uid, cancellationToken);
        return stored with
        {
            Value = stored.Value with
            {
                Definition = stored.Value.Definition with { UsePolicy = LegacyPolicy(policy) }
            }
        };
    }

    private async Task<StoredResource<SecretResource>?> HydrateSecretNullableAsync(
        StoredResource<SecretResource>? stored, CancellationToken cancellationToken) =>
        stored is null ? null : await HydrateSecretAsync(stored, cancellationToken);

    private async Task<StoredResource<SecretResource>> HydrateSecretAsync(
        StoredResource<SecretResource> stored, CancellationToken cancellationToken)
    {
        var policy = await accessPolicies.GetAsync(stored.Value.Uid, cancellationToken);
        return stored with
        {
            Value = stored.Value with
            {
                Definition = stored.Value.Definition with { UsePolicy = LegacyPolicy(policy) }
            }
        };
    }

    private async Task PersistLegacyPolicyAsync(Resource resource, DescendantUsePolicy legacy,
        CancellationToken cancellationToken)
    {
        var current = await accessPolicies.GetAsync(resource.Uid, cancellationToken);
        if (Equivalent(legacy, LegacyPolicy(current))) return;
        if (legacy.Grants.Count == 0)
        {
            if (current is not null && current.Policy.Grants.All(grant =>
                    grant.Actions.Contains(ResourceAccessAction.Discover)
                    && grant.Actions.Contains(ResourceAccessAction.Use)))
                await accessPolicies.DeleteAsync(resource.Uid, current.ETag, cancellationToken);
            return;
        }
        var policy = new ResourceAccessPolicy(AccessIdentity(resource), legacy.Grants.Select(grant =>
            new ResourceAccessGrant(grant.ScopeRef,
                [ResourceAccessAction.Discover, ResourceAccessAction.Use], grant.IncludeDescendants)).ToArray());
        await accessPolicies.PutAsync(policy, current?.ETag, cancellationToken);
    }

    private static DescendantUsePolicy LegacyPolicy(ResourceAccessPolicyResponse? response) => new()
    {
        Grants = response?.Policy.Grants
            .Where(grant => grant.Actions.Contains(ResourceAccessAction.Use))
            .Select(grant => new DescendantUseGrant(grant.TargetScopeRef, grant.IncludeDescendants)).ToArray() ?? []
    };

    private static bool Equivalent(DescendantUsePolicy left, DescendantUsePolicy right) =>
        left.Grants.Count == right.Grants.Count && left.Grants.All(grant => right.Grants.Any(candidate =>
            candidate.ScopeRef == grant.ScopeRef && candidate.IncludeDescendants == grant.IncludeDescendants));

    private static ResourceAccessIdentity AccessIdentity(Resource resource) => new(resource.Uid,
        RequireScope(resource), resource.Namespace, resource.Kind);

    private async Task<bool> CanDiscoverAsync(Resource resource, CancellationToken cancellationToken)
    {
        if (currentRequestContext.AccessMode is ControlPlaneAccessMode.System or ControlPlaneAccessMode.Instance)
            return currentRequestContext.AccessMode == ControlPlaneAccessMode.System
                || resource.ScopeRef == ResourceScopeRef.Instance;
        var consumerScopeRef = currentRequestContext.AccessMode switch
        {
            ControlPlaneAccessMode.Tenant => ResourceScopeRef.Tenant(currentRequestContext.Current.TenantId),
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(currentRequestContext.Current.TenantId),
            ControlPlaneAccessMode.Workspace => ResourceScopeRef.Workspace(currentRequestContext.Current.WorkspaceId),
            _ => default
        };
        return consumerScopeRef != default && (await accessPolicies.EvaluateAsync(AccessIdentity(resource),
            consumerScopeRef, ResourceAccessAction.Discover, cancellationToken)).Allowed;
    }

    private async Task EnsureReadableScopeAsync(string kind, ResourceScopeRef scopeRef, CancellationToken cancellationToken)
    {
        var target = (await scopeOperations.ListTargetsAsync(kind, AuthorizationPermissions.ResourcesRead,
            cancellationToken)).SingleOrDefault(value => value.ScopeRef == scopeRef);
        if (target is null || !target.CanWrite) throw new ResourceScopeAccessDeniedException(scopeRef);
    }

    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
}
