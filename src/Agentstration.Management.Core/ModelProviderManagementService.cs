using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Core;

public sealed class ModelProviderManagementService(
    IControlPlaneStore store,
    IEnumerable<IModelProviderDiscovery> discoveries,
    TimeProvider timeProvider) : IModelProviderConfigurationStore
{
    public static string ModelProviderId(string name) => name;
    public async Task ValidateForCreateAsync(ModelProviderResource resource, CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        _ = await ValidateAndNormalizeAsync(resource.Namespace, resource.Definition, cancellationToken);
    }

    public async Task<StoredResource<ModelProviderResource>> CreateAsync(ModelProviderResource resource, CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        if (await GetAsync(resource.Namespace, resource.Metadata.Name, cancellationToken) is not null) throw new ControlPlaneConcurrencyException($"Model provider '{resource.Address}' already exists.");
        var definition = await ValidateAndNormalizeAsync(resource.Namespace, resource.Definition, cancellationToken);
        return await store.PutAsync(resource with
        {
            Generation = 1,
            Definition = definition,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, null, true, cancellationToken);
    }

    public async Task<StoredResource<ModelProviderResource>> PutAsync(string name, ModelProviderProperties definition, string? ifMatch, CancellationToken cancellationToken)
        => await PutAsync(ResourceNamespace.Default, name, definition, ifMatch, cancellationToken);

    public async Task<StoredResource<ModelProviderResource>> PutAsync(ResourceNamespace @namespace, string name, ModelProviderProperties definition, string? ifMatch, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken) ?? throw new ModelProviderResourceNotFoundException(name);
        var validated = await ValidateAndNormalizeAsync(existing.Value.Namespace, definition, cancellationToken);
        return await store.PutAsync(existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Definition = validated,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, ifMatch, false, cancellationToken);
    }

    public Task<StoredResource<ModelProviderResource>?> GetAsync(string name, CancellationToken cancellationToken) => store.GetAsync<ModelProviderResource>(new ResourceKey(ResourceKinds.ModelProvider, name), cancellationToken);
    public Task<StoredResource<ModelProviderResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => store.GetAsync<ModelProviderResource>(new ResourceKey(ResourceKinds.ModelProvider, name, @namespace), cancellationToken);

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

    public async Task DeleteAsync(string name, string? ifMatch, CancellationToken cancellationToken)
    {
        _ = await GetAsync(name, cancellationToken) ?? throw new ModelProviderResourceNotFoundException(name);
        var usages = await GetUsagesAsync(name, cancellationToken);
        if (usages.Count > 0) throw new ModelProviderInUseException(name, usages);
        await store.DeleteAsync(new(ResourceKinds.ModelProvider, name), ifMatch, cancellationToken);
    }

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        _ = await GetAsync(@namespace, name, cancellationToken) ?? throw new ModelProviderResourceNotFoundException(name);
        var usages = await GetUsagesAsync(@namespace, name, cancellationToken);
        if (usages.Count > 0) throw new ModelProviderInUseException(name, usages);
        await store.DeleteAsync(new(ResourceKinds.ModelProvider, name, @namespace), ifMatch, cancellationToken);
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

    ValueTask<ModelProviderConfiguration> IModelProviderConfigurationStore.GetRequiredAsync(string name, CancellationToken cancellationToken) => new(GetConfigurationRequiredAsync(name, cancellationToken));
    ValueTask<ModelProviderConfiguration> IModelProviderConfigurationStore.GetRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => new(GetConfigurationRequiredAsync(@namespace, name, cancellationToken));

    async ValueTask<IReadOnlyList<ModelProviderConfiguration>> IModelProviderConfigurationStore.ListAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll((await store.ListAllAsync<ModelProviderResource>(ResourceKinds.ModelProvider, cancellationToken))
            .Select(resource => ToConfigurationAsync(resource.Value, cancellationToken)));

    private async Task<ModelProviderProperties> ValidateAndNormalizeAsync(
        ResourceNamespace ownerNamespace,
        ModelProviderProperties definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ContributionId);
        if (definition.Extension.WorkspaceRef is not null)
            throw new ModelProviderValidationException("Cross-workspace extension references are not supported.");
        var extensionAddress = definition.Extension.Resolve(ownerNamespace, ResourceKinds.ExtensionRegistration);
        if (await store.GetAsync<ExtensionRegistrationResource>(new(extensionAddress.Kind, extensionAddress.Name, extensionAddress.Namespace), cancellationToken) is null)
            throw new ModelProviderValidationException($"Referenced extension registration '{extensionAddress}' does not exist.");
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
        var extension = await store.GetAsync<ExtensionRegistrationResource>(
            new(extensionAddress.Kind, extensionAddress.Name, extensionAddress.Namespace), cancellationToken)
            ?? throw new ModelProviderConfigurationException($"Extension registration '{extensionAddress}' was not found.");
        return new()
        {
            Uid = resource.Uid,
            Namespace = resource.Namespace,
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

