using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Core;

public sealed class ModelProviderManagementService(
    IResourceStore store,
    IResourceReferenceResolver references,
    ResourceScopeOperationService scopeOperations,
    IEnumerable<IModelProviderDiscovery> discoveries,
    TimeProvider timeProvider) : IModelProviderConfigurationStore
{
    public static string ModelProviderId(string name) => name;
    public async Task ValidateForCreateAsync(ModelProviderResource resource, CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ResourceKinds.ModelProvider);
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        _ = await ValidateAndNormalizeAsync(resource.Namespace, resource.Definition, scopeRef, cancellationToken);
    }

    public async Task<StoredResource<ModelProviderResource>> CreateAsync(ModelProviderResource resource, CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ResourceKinds.ModelProvider);
        return await scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var address = ScopedResourceAddress.Create(scopeRef, resource.Namespace, ResourceKinds.ModelProvider, resource.Name);
            if (await store.GetExactAsync<ModelProviderResource>(address, token) is not null)
                throw new ResourceConcurrencyException($"Model provider '{resource.Address}' already exists in scope '{scopeRef}'.");
            var definition = await ValidateAndNormalizeAsync(resource.Namespace, resource.Definition, scopeRef, token);
            return await store.PutExactAsync(scopeRef, resource with
            {
                ScopeRef = scopeRef,
                Generation = 1,
                Definition = definition,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            }, null, true, token);
        }, cancellationToken);
    }

    public async Task<StoredResource<ModelProviderResource>> PutAsync(string name, ModelProviderProperties definition, string? ifMatch, CancellationToken cancellationToken)
        => await PutAsync(ResourceNamespace.Default, name, definition, ifMatch, cancellationToken);

    public async Task<StoredResource<ModelProviderResource>> PutAsync(ResourceNamespace @namespace, string name, ModelProviderProperties definition, string? ifMatch, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken) ?? throw new ModelProviderResourceNotFoundException(name);
        var scopeRef = existing.Value.ScopeRef ?? throw new ModelProviderValidationException("The model provider has no ownership scope.");
        return await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var validated = await ValidateAndNormalizeAsync(existing.Value.Namespace, definition, scopeRef, token);
            return await store.PutExactAsync(scopeRef, existing.Value with
            {
                Generation = checked(existing.Value.Generation + 1),
                Definition = validated,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            }, ifMatch, false, token);
        }, cancellationToken);
    }

    public Task<StoredResource<ModelProviderResource>?> GetAsync(string name, CancellationToken cancellationToken) => store.GetAsync<ModelProviderResource>(new ResourceKey(ResourceKinds.ModelProvider, name), cancellationToken);
    public Task<StoredResource<ModelProviderResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => store.GetAsync<ModelProviderResource>(new ResourceKey(ResourceKinds.ModelProvider, name, @namespace), cancellationToken);
    public Task<StoredResource<ModelProviderResource>?> GetExactAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        store.GetExactAsync<ModelProviderResource>(ScopedResourceAddress.Create(scopeRef, @namespace, ResourceKinds.ModelProvider, name), cancellationToken);

    public async Task<IReadOnlyList<ModelProviderView>> ListAsync(CancellationToken cancellationToken)
    {
        var resources = await store.ListAllAsync<ModelProviderResource>(ResourceKinds.ModelProvider, cancellationToken);
        return await Task.WhenAll(resources.Select(async resource =>
            await InspectAsync(await ToConfigurationAsync(resource.Value, cancellationToken), true, cancellationToken)));
    }

    public async Task<ModelProviderView> GetViewRequiredAsync(string name, CancellationToken cancellationToken)
        => await GetViewRequiredAsync(ResourceNamespace.Default, name, cancellationToken);

    public async Task<ModelProviderView> GetViewRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var stored = await GetAsync(@namespace, name, cancellationToken) ?? throw new ModelProviderResourceNotFoundException(name);
        return await InspectAsync(await ToConfigurationAsync(stored.Value, cancellationToken), false, cancellationToken);
    }

    public async Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(string name, CancellationToken cancellationToken)
        => await ListModelsAsync(ResourceNamespace.Default, name, cancellationToken);

    public async Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var provider = await GetConfigurationRequiredAsync(@namespace, name, cancellationToken);
        var discovery = FindDiscovery(provider.AdapterType) ?? throw new ModelProviderUnavailableException(name, "No discovery adapter is registered in this host.");
        var health = await discovery.GetHealthAsync(provider, cancellationToken);
        if (!string.Equals(health.Status, "available", StringComparison.OrdinalIgnoreCase)) throw new ModelProviderUnavailableException(name, health.Details);
        try { return await discovery.ListModelsAsync(provider, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { throw new ModelProviderUnavailableException(name, exception.Message); }
    }

    public Task<ModelProviderView> GetStatusAsync(string name, CancellationToken cancellationToken) => GetViewRequiredAsync(name, cancellationToken);
    public Task<ModelProviderView> GetStatusAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => GetViewRequiredAsync(@namespace, name, cancellationToken);

    public async Task<IReadOnlyList<ModelProviderUsage>> GetUsagesAsync(string providerName, CancellationToken cancellationToken) =>
        await GetUsagesAsync(ResourceNamespace.Default, providerName, cancellationToken);

    public async Task<IReadOnlyList<ModelProviderUsage>> GetUsagesAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken) =>
        (await store.ListAllAsync<ModelProfileResource>(ResourceKinds.ModelProfile, cancellationToken))
            .Where(profile => profile.Value.Definition.Provider.Resolve(profile.Value.Namespace, ResourceKinds.ModelProvider).Namespace == @namespace
                && profile.Value.Definition.Provider.Name == providerName)
            .Select(profile => new ModelProviderUsage(profile.Value.Kind, profile.Value.Metadata.Name, profile.Value.Definition.DisplayName))
            .ToArray();

    public Task DeleteAsync(string name, string? ifMatch, CancellationToken cancellationToken) =>
        DeleteAsync(ResourceNamespace.Default, name, ifMatch, cancellationToken);

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken) ?? throw new ModelProviderResourceNotFoundException(name);
        var usages = await GetUsagesAsync(@namespace, name, cancellationToken);
        if (usages.Count > 0) throw new ModelProviderInUseException(name, usages);
        var scopeRef = existing.Value.ScopeRef ?? throw new ModelProviderValidationException("The model provider has no ownership scope.");
        await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await store.DeleteExactAsync(ScopedResourceAddress.Create(scopeRef, @namespace, ResourceKinds.ModelProvider, name), ifMatch, token);
            return true;
        }, cancellationToken);
    }

    private async Task<ModelProviderView> InspectAsync(ModelProviderConfiguration provider, bool includeModels, CancellationToken cancellationToken)
    {
        var discovery = FindDiscovery(provider.AdapterType);
        if (discovery is null) return new(provider, new("unknown", "No discovery adapter is registered in this host."), [], timeProvider.GetUtcNow());
        var health = await discovery.GetHealthAsync(provider, cancellationToken);
        IReadOnlyList<DiscoveredModel> models = [];
        if (includeModels && string.Equals(health.Status, "available", StringComparison.OrdinalIgnoreCase))
        {
            try { models = await discovery.ListModelsAsync(provider, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { health = new("unavailable", exception.Message); }
        }
        return new(provider, health, models, timeProvider.GetUtcNow());
    }

    public async Task<ModelProviderConfiguration> GetConfigurationRequiredAsync(string name, CancellationToken cancellationToken)
        => await GetConfigurationRequiredAsync(ResourceNamespace.Default, name, cancellationToken);

    public async Task<ModelProviderConfiguration> GetConfigurationRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var resource = await GetAsync(@namespace, name, cancellationToken) ?? throw new ModelProviderConfigurationNotFoundException(name);
        return await ToConfigurationAsync(resource.Value, cancellationToken);
    }

    public async Task<ModelProviderConfiguration> GetConfigurationRequiredAsync(
        ResourceReference reference,
        ResourceNamespace ownerNamespace,
        ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken)
    {
        var resource = await references.ResolveAsync<ModelProviderResource>(
            reference, ownerNamespace, ResourceKinds.ModelProvider, consumerScopeRef, cancellationToken)
            ?? throw new ModelProviderConfigurationNotFoundException(reference.Name);
        return await ToConfigurationAsync(resource.Value, cancellationToken);
    }

    ValueTask<ModelProviderConfiguration> IModelProviderConfigurationStore.GetRequiredAsync(string name, CancellationToken cancellationToken) => new(GetConfigurationRequiredAsync(name, cancellationToken));
    ValueTask<ModelProviderConfiguration> IModelProviderConfigurationStore.GetRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => new(GetConfigurationRequiredAsync(@namespace, name, cancellationToken));

    async ValueTask<IReadOnlyList<ModelProviderConfiguration>> IModelProviderConfigurationStore.ListAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll((await store.ListAllAsync<ModelProviderResource>(ResourceKinds.ModelProvider, cancellationToken))
            .Select(resource => ToConfigurationAsync(resource.Value, cancellationToken)));

    private async Task<ModelProviderProperties> ValidateAndNormalizeAsync(
        ResourceNamespace ownerNamespace,
        ModelProviderProperties definition,
        ResourceScopeRef ownerScopeRef,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ContributionId);
        var extensionAddress = definition.Extension.Resolve(ownerNamespace, ResourceKinds.ExtensionRegistration);
        if (await references.ResolveAsync<ExtensionRegistrationResource>(definition.Extension, ownerNamespace, ResourceKinds.ExtensionRegistration, ownerScopeRef, cancellationToken) is null)
            throw new ModelProviderValidationException($"Referenced extension registration '{extensionAddress}' does not exist or is not visible from '{ownerScopeRef}'.");
        if (FindDiscovery(AepModelProvider.AdapterType) is null)
            throw new ModelProviderValidationException("The AEP model-provider adapter is not registered in this host.");
        return definition with
        {
            DisplayName = definition.DisplayName.Trim(),
            ContributionId = definition.ContributionId.Trim()
        };
    }

    private async Task<ModelProviderConfiguration> ToConfigurationAsync(ModelProviderResource resource, CancellationToken cancellationToken)
    {
        var extensionAddress = resource.Definition.Extension.Resolve(resource.Namespace, ResourceKinds.ExtensionRegistration);
        var extension = await references.ResolveAsync<ExtensionRegistrationResource>(
            resource.Definition.Extension,
            resource.Namespace,
            ResourceKinds.ExtensionRegistration,
            resource.ScopeRef ?? throw new ModelProviderConfigurationException("The model provider has no ownership scope."),
            cancellationToken)
            ?? throw new ModelProviderConfigurationException($"Extension registration '{extensionAddress}' was not found.");
        return new()
        {
            Uid = resource.Uid,
            Namespace = resource.Namespace,
            ScopeRef = resource.ScopeRef,
            Name = resource.Metadata.Name,
            AdapterType = AepModelProvider.AdapterType,
            ContributionId = resource.Definition.ContributionId,
            Extension = resource.Definition.Extension,
            Endpoint = extension.Value.Definition.Endpoint,
            ExtensionEnabled = extension.Value.Definition.Enabled,
            ExpectedExtensionId = extension.Value.Definition.ExpectedExtensionId,
            DisplayName = resource.Definition.DisplayName,
            RegistrationSource = extension.Value.Definition.Source,
            EndpointDisplayName = extension.Value.Definition.DisplayName,
            ExtensionScopeRef = extension.Value.ScopeRef,
            AuthenticationMode = extension.Value.Definition.AuthenticationMode,
            Credential = extension.Value.Definition.Credential
        };
    }

    private static void ValidateIdentity(ModelProviderResource resource)
    {
        if (resource.Kind != ResourceKinds.ModelProvider) throw new ModelProviderValidationException($"Kind must be '{ResourceKinds.ModelProvider}'.");
        if (resource.ApiVersion != ManagementApiVersions.CoreV1) throw new ModelProviderValidationException($"ApiVersion must be '{ManagementApiVersions.CoreV1}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name);
    }

    private IModelProviderDiscovery? FindDiscovery(string providerType) => discoveries.SingleOrDefault(discovery => discovery.CanHandle(providerType));
}

