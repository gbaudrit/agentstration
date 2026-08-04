using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Core;

public sealed record ModelProviderView(
    ModelProviderConfiguration Configuration,
    ModelProviderHealth Health,
    IReadOnlyList<DiscoveredModel> Models,
    DateTimeOffset CheckedAt);

public sealed record ModelProfileUsage(string ResourceType, string ResourceId, string Name, string DisplayName);
public sealed record ModelProviderUsage(string ResourceType, string ResourceId, string Name, string DisplayName);

public sealed record ModelProfileResolution(
    ModelProfileResource Profile,
    ModelProviderConfiguration? Provider,
    ModelProviderHealth ProviderHealth,
    DiscoveredModel? Model,
    string Status,
    IReadOnlyList<string> Warnings);

public sealed class ModelProfileValidationException(
    string code,
    string message,
    IReadOnlyDictionary<string, string[]>? errors = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
}

public sealed class ModelProfileInUseException(string profileName, IReadOnlyList<ModelProfileUsage> usages)
    : Exception($"The model profile '{profileName}' is used by {usages.Count} agent(s).")
{
    public string ProfileName { get; } = profileName;
    public IReadOnlyList<ModelProfileUsage> Usages { get; } = usages;
}

public sealed class ModelProviderUnavailableException(string providerName, string? details)
    : Exception($"Model provider '{providerName}' is unavailable{(string.IsNullOrWhiteSpace(details) ? "." : $": {details}")}");

public sealed class ModelProviderResourceNotFoundException(string providerName)
    : Exception($"Model provider '{providerName}' was not found.");
public sealed class ModelProviderValidationException(string message) : Exception(message);
public sealed class ModelProviderInUseException(string providerName, IReadOnlyList<ModelProviderUsage> usages)
    : Exception($"The model provider '{providerName}' is used by {usages.Count} model profile(s).")
{
    public IReadOnlyList<ModelProviderUsage> Usages { get; } = usages;
}

public sealed class ModelProviderManagementService(
    IControlPlaneStore store,
    IEnumerable<IModelProviderDiscovery> discoveries,
    IEnumerable<IModelProviderOptionsValidator> optionsValidators,
    TimeProvider timeProvider) : IModelProviderConfigurationStore
{
    public async Task<StoredResource<ModelProviderResource>> CreateAsync(ModelProviderResource resource, CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        var properties = ValidateAndNormalize(resource.Properties);
        if (await store.GetAsync<ModelProviderResource>(resource.Id, cancellationToken) is not null)
            throw new ControlPlaneConcurrencyException($"Model provider '{resource.Name}' already exists.");
        return await store.PutAsync(resource with
        {
            Generation = 1,
            Properties = properties,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, null, true, cancellationToken);
    }

    public async Task<StoredResource<ModelProviderResource>> PutAsync(
        string resourceGroup,
        string name,
        ModelProviderProperties properties,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var id = ModelProviderId(name, resourceGroup);
        var existing = await store.GetAsync<ModelProviderResource>(id, cancellationToken)
            ?? throw new ModelProviderResourceNotFoundException(name);
        return await store.PutAsync(existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Properties = ValidateAndNormalize(properties),
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, ifMatch, false, cancellationToken);
    }

    public Task<StoredResource<ModelProviderResource>?> GetAsync(string resourceGroup, string name, CancellationToken cancellationToken) =>
        store.GetAsync<ModelProviderResource>(ModelProviderId(name, resourceGroup), cancellationToken);

    public async Task<IReadOnlyList<ModelProviderView>> ListAsync(string? resourceGroup, CancellationToken cancellationToken)
    {
        var resources = await store.ListAsync<ModelProviderResource>(AgentstrationResourceTypes.ModelProviders, resourceGroup, 0, 1000, cancellationToken);
        return await Task.WhenAll(resources.Select(resource =>
            InspectAsync(ToConfiguration(resource.Value), includeModels: true, cancellationToken)));
    }

    public async Task<ModelProviderView> GetViewRequiredAsync(string resourceGroup, string name, CancellationToken cancellationToken)
    {
        var stored = await GetAsync(resourceGroup, name, cancellationToken) ?? throw new ModelProviderResourceNotFoundException(name);
        return await InspectAsync(ToConfiguration(stored.Value), includeModels: false, cancellationToken);
    }

    public async Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(string resourceGroup, string name, CancellationToken cancellationToken)
    {
        var provider = await GetConfigurationRequiredAsync(ModelProviderId(name, resourceGroup), cancellationToken);
        var discovery = FindDiscovery(provider.ProviderType)
            ?? throw new ModelProviderUnavailableException(name, "No discovery adapter is registered in this host.");
        var health = await discovery.GetHealthAsync(provider, cancellationToken);
        if (!string.Equals(health.Status, "available", StringComparison.OrdinalIgnoreCase))
            throw new ModelProviderUnavailableException(name, health.Details);
        try { return await discovery.ListModelsAsync(provider, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new ModelProviderUnavailableException(name, exception.Message);
        }
    }

    public async Task<ModelProviderView> GetStatusAsync(string resourceGroup, string name, CancellationToken cancellationToken) =>
        await GetViewRequiredAsync(resourceGroup, name, cancellationToken);

    public async Task<IReadOnlyList<ModelProviderUsage>> GetUsagesAsync(string providerResourceId, CancellationToken cancellationToken)
    {
        var profiles = await store.ListAsync<ModelProfileResource>(AgentstrationResourceTypes.ModelProfiles, null, 0, 1000, cancellationToken);
        return profiles.Where(profile => string.Equals(profile.Value.Properties.Provider.ResourceId, providerResourceId, StringComparison.Ordinal))
            .Select(profile => new ModelProviderUsage(profile.Value.Type, profile.Value.Id, profile.Value.Name, profile.Value.Properties.DisplayName))
            .ToArray();
    }

    public async Task DeleteAsync(string resourceGroup, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        var id = ModelProviderId(name, resourceGroup);
        _ = await store.GetAsync<ModelProviderResource>(id, cancellationToken) ?? throw new ModelProviderResourceNotFoundException(name);
        var usages = await GetUsagesAsync(id, cancellationToken);
        if (usages.Count > 0) throw new ModelProviderInUseException(name, usages);
        await store.DeleteAsync(id, ifMatch, cancellationToken);
    }

    public static string ModelProviderId(string name, string resourceGroup = "default") =>
        ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.ModelProviders, "modelProviders", name).Value;

    private async Task<ModelProviderView> InspectAsync(ModelProviderConfiguration provider, bool includeModels, CancellationToken cancellationToken)
    {
        var discovery = FindDiscovery(provider.ProviderType);
        if (discovery is null)
            return new ModelProviderView(provider, new ModelProviderHealth("unknown", "No discovery adapter is registered in this host."), [], timeProvider.GetUtcNow());
        var health = await discovery.GetHealthAsync(provider, cancellationToken);
        IReadOnlyList<DiscoveredModel> models = [];
        if (includeModels && string.Equals(health.Status, "available", StringComparison.OrdinalIgnoreCase))
        {
            try { models = await discovery.ListModelsAsync(provider, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                health = new ModelProviderHealth("unavailable", exception.Message);
            }
        }
        return new ModelProviderView(provider, health, models, timeProvider.GetUtcNow());
    }

    private async Task<ModelProviderConfiguration> GetConfigurationRequiredAsync(string nameOrResourceId, CancellationToken cancellationToken)
    {
        var id = ResourceIdentifier.TryParse(nameOrResourceId, out var parsed)
            ? parsed.Value
            : ModelProviderId(nameOrResourceId);
        var resource = await store.GetAsync<ModelProviderResource>(id, cancellationToken)
            ?? throw new ModelProviderConfigurationNotFoundException(nameOrResourceId);
        return ToConfiguration(resource.Value);
    }

    ValueTask<ModelProviderConfiguration> IModelProviderConfigurationStore.GetRequiredAsync(string name, CancellationToken cancellationToken) =>
        new(GetConfigurationRequiredAsync(name, cancellationToken));

    async ValueTask<IReadOnlyList<ModelProviderConfiguration>> IModelProviderConfigurationStore.ListAsync(CancellationToken cancellationToken)
    {
        var resources = await store.ListAsync<ModelProviderResource>(AgentstrationResourceTypes.ModelProviders, null, 0, 1000, cancellationToken);
        return resources.Select(resource => ToConfiguration(resource.Value)).ToArray();
    }

    private ModelProviderProperties ValidateAndNormalize(ModelProviderProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (string.IsNullOrWhiteSpace(properties.DisplayName)) throw new ModelProviderValidationException("Provider displayName is required.");
        if (string.IsNullOrWhiteSpace(properties.ProviderType)) throw new ModelProviderValidationException("Provider type is required.");
        if (FindDiscovery(properties.ProviderType) is null) throw new ModelProviderValidationException($"Provider type '{properties.ProviderType}' is not registered in this host.");
        if (!properties.Endpoint.IsAbsoluteUri || properties.Endpoint.Scheme is not ("http" or "https"))
            throw new ModelProviderValidationException("Provider endpoint must be an absolute HTTP(S) URL.");
        if (!string.IsNullOrEmpty(properties.Endpoint.UserInfo) || !string.IsNullOrEmpty(properties.Endpoint.Query) || !string.IsNullOrEmpty(properties.Endpoint.Fragment))
            throw new ModelProviderValidationException("Provider endpoint cannot contain credentials, a query string, or a fragment.");
        foreach (var key in properties.ProviderOptions.Keys)
            if (!string.Equals(key, properties.ProviderType, StringComparison.OrdinalIgnoreCase))
                throw new ModelProviderValidationException($"Provider options for '{key}' cannot be used with provider '{properties.ProviderType}'.");
        try
        {
            optionsValidators.SingleOrDefault(validator => string.Equals(validator.ProviderType, properties.ProviderType, StringComparison.OrdinalIgnoreCase))
                ?.Validate(properties.ProviderOptions);
        }
        catch (ModelProviderConfigurationException exception)
        {
            throw new ModelProviderValidationException(exception.Message);
        }
        var endpoint = new Uri(properties.Endpoint.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        return properties with { DisplayName = properties.DisplayName.Trim(), ProviderType = properties.ProviderType.Trim().ToLowerInvariant(), Endpoint = endpoint };
    }

    private static ModelProviderConfiguration ToConfiguration(ModelProviderResource resource) => new()
    {
        ResourceId = resource.Id,
        Name = resource.Name,
        ResourceGroup = resource.ResourceGroup ?? "default",
        Location = resource.Location ?? "local",
        ProviderType = resource.Properties.ProviderType,
        Endpoint = resource.Properties.Endpoint,
        DisplayName = resource.Properties.DisplayName,
        ManagementMode = resource.Properties.ManagementMode,
        EndpointDisplayName = resource.Properties.Endpoint.Authority,
        Capabilities = ["chat"]
    };

    private static void ValidateIdentity(ModelProviderResource resource)
    {
        if (!string.Equals(resource.Type, AgentstrationResourceTypes.ModelProviders, StringComparison.Ordinal))
            throw new ModelProviderValidationException($"Type must be '{AgentstrationResourceTypes.ModelProviders}'.");
        if (!string.Equals(resource.ApiVersion, ManagementApiVersions.V20260801, StringComparison.Ordinal))
            throw new ModelProviderValidationException($"ApiVersion must be '{ManagementApiVersions.V20260801}'.");
        if (string.IsNullOrWhiteSpace(resource.ResourceGroup)
            || string.IsNullOrWhiteSpace(resource.Location)
            || !string.Equals(resource.Id, ModelProviderId(resource.Name, resource.ResourceGroup), StringComparison.Ordinal))
            throw new ModelProviderValidationException("The model provider resource identity is invalid.");
    }

    private IModelProviderDiscovery? FindDiscovery(string providerType) => discoveries.SingleOrDefault(
        discovery => string.Equals(discovery.ProviderType, providerType, StringComparison.OrdinalIgnoreCase));
}

public sealed class ModelProfileManagementService(
    IControlPlaneStore store,
    IModelProviderConfigurationStore providerConfigurations,
    IEnumerable<IModelProviderDiscovery> discoveries,
    IEnumerable<IModelProviderOptionsValidator> optionsValidators) : IModelProfileStore, IModelDeploymentStore, IModelProfileReferenceValidator
{
    public async Task<StoredResource<ModelProfileResource>> CreateAsync(ModelProfileResource resource, CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        await ValidateDefinitionAsync(resource.Properties, cancellationToken);
        if (await store.GetAsync<ModelProfileResource>(resource.Id, cancellationToken) is not null)
            throw new ControlPlaneConcurrencyException($"Model profile '{resource.Name}' already exists.");
        return await store.PutAsync(resource with
        {
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, null, true, cancellationToken);
    }

    public async Task<StoredResource<ModelProfileResource>> PutAsync(
        string resourceGroup,
        string name,
        ModelProfileProperties properties,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var id = ProfileId(resourceGroup, name);
        var existing = await store.GetAsync<ModelProfileResource>(id, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(id);
        await ValidateDefinitionAsync(properties, cancellationToken);
        return await store.PutAsync(existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Properties = properties,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, ifMatch, false, cancellationToken);
    }

    public Task<StoredResource<ModelProfileResource>?> GetAsync(string resourceGroup, string name, CancellationToken cancellationToken) =>
        store.GetAsync<ModelProfileResource>(ProfileId(resourceGroup, name), cancellationToken);

    public async Task<IReadOnlyList<StoredResource<ModelProfileResource>>> ListAsync(
        string? provider,
        string? model,
        string? status,
        string? search,
        CancellationToken cancellationToken)
    {
        var profiles = await store.ListAsync<ModelProfileResource>(AgentstrationResourceTypes.ModelProfiles, null, 0, 1000, cancellationToken);
        var filtered = new List<StoredResource<ModelProfileResource>>();
        foreach (var profile in profiles)
        {
            if (!string.IsNullOrWhiteSpace(provider)
                && !string.Equals(ResourceIdentifier.Parse(profile.Value.Properties.Provider.ResourceId).Name, provider, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(model)
                && !string.Equals(profile.Value.Properties.Model.Name, model, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(search)
                && !profile.Value.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !profile.Value.Properties.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !(profile.Value.Properties.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            if (!string.IsNullOrWhiteSpace(status))
            {
                var resolution = await ResolveAsync(profile.Value, cancellationToken);
                if (!string.Equals(resolution.Status, status, StringComparison.OrdinalIgnoreCase)) continue;
            }
            filtered.Add(profile);
        }
        return filtered;
    }

    public async Task DeleteAsync(string resourceGroup, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        var profile = await GetAsync(resourceGroup, name, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(ProfileId(resourceGroup, name));
        var usages = await GetUsagesAsync(profile.Value.Id, cancellationToken);
        if (usages.Count > 0) throw new ModelProfileInUseException(profile.Value.Name, usages);
        await store.DeleteAsync(profile.Value.Id, ifMatch, cancellationToken);
    }

    public async Task<IReadOnlyList<ModelProfileUsage>> GetUsagesAsync(string profileResourceId, CancellationToken cancellationToken)
    {
        var agents = await store.ListAsync<AgentResource>(AgentstrationResourceTypes.Agents, null, 0, 1000, cancellationToken);
        return agents.Where(agent => string.Equals(agent.Value.Properties.ModelProfile.ResourceId, profileResourceId, StringComparison.Ordinal))
            .Select(agent => new ModelProfileUsage(agent.Value.Type, agent.Value.Id, agent.Value.Name, agent.Value.Properties.DisplayName))
            .ToArray();
    }

    public async Task<ModelProfileResolution> ResolveAsync(ModelProfileResource profile, CancellationToken cancellationToken)
    {
        try
        {
            await ValidateDefinitionAsync(profile.Properties, cancellationToken);
            var provider = await providerConfigurations.GetRequiredAsync(profile.Properties.Provider.ResourceId, cancellationToken);
            var discovery = discoveries.SingleOrDefault(candidate => string.Equals(candidate.ProviderType, provider.ProviderType, StringComparison.OrdinalIgnoreCase));
            if (discovery is null)
                return new ModelProfileResolution(profile, provider, new ModelProviderHealth("unknown"), null, "unknown", ["No provider discovery adapter is registered in this host."]);
            var health = await discovery.GetHealthAsync(provider, cancellationToken);
            if (!string.Equals(health.Status, "available", StringComparison.OrdinalIgnoreCase))
                return new ModelProfileResolution(profile, provider, health, null, "providerUnavailable", [health.Details ?? "The provider is unavailable."]);
            IReadOnlyList<DiscoveredModel> models;
            try { models = await discovery.ListModelsAsync(provider, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new ModelProfileResolution(profile, provider, new ModelProviderHealth("unavailable", exception.Message), null, "providerUnavailable", [exception.Message]);
            }
            var model = models.SingleOrDefault(candidate => string.Equals(candidate.Name, profile.Properties.Model.Name, StringComparison.OrdinalIgnoreCase));
            return model is null
                ? new ModelProfileResolution(profile, provider, health, null, "modelUnavailable", ["The configured model is not currently available from the provider."])
                : new ModelProfileResolution(profile, provider, health, model, "ready", []);
        }
        catch (ModelProfileValidationException exception)
        {
            return new ModelProfileResolution(profile, null, new ModelProviderHealth("unknown"), null, "invalidConfiguration", [exception.Message]);
        }
    }

    public async ValueTask<ModelProfileConfiguration> GetRequiredAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        var profile = await store.GetAsync<ModelProfileResource>(resourceId, cancellationToken) ?? throw new ModelProfileNotFoundException(resourceId);
        return new ModelProfileConfiguration
        {
            Name = profile.Value.Name,
            DeploymentName = profile.Value.Id,
            Generation = profile.Value.Properties.Generation,
            Reasoning = profile.Value.Properties.Reasoning,
            Output = profile.Value.Properties.Output,
            ProviderOptions = profile.Value.Properties.ProviderOptions
        };
    }

    async ValueTask<ModelDeploymentConfiguration> IModelDeploymentStore.GetRequiredAsync(string name, CancellationToken cancellationToken)
    {
        var profile = await store.GetAsync<ModelProfileResource>(name, cancellationToken) ?? throw new ModelDeploymentNotFoundException(name);
        return new ModelDeploymentConfiguration
        {
            Name = profile.Value.Name,
            ProviderName = profile.Value.Properties.Provider.ResourceId,
            ModelName = profile.Value.Properties.Model.Name,
            ProviderOptions = profile.Value.Properties.ProviderOptions
        };
    }

    public async Task ValidateReferenceAsync(string profileResourceId, CancellationToken cancellationToken)
    {
        var profile = await store.GetAsync<ModelProfileResource>(profileResourceId, cancellationToken)
            ?? throw new ModelProfileValidationException(
                "model_profile_reference_invalid",
                "The referenced model profile does not exist.",
                new Dictionary<string, string[]> { ["properties.modelProfile.resourceId"] = ["The referenced model profile does not exist."] });
        await ValidateDefinitionAsync(profile.Value.Properties, cancellationToken);
    }

    Task IModelProfileReferenceValidator.ValidateAsync(string profileResourceId, CancellationToken cancellationToken) =>
        ValidateReferenceAsync(profileResourceId, cancellationToken);

    public static string ProfileId(string resourceGroup, string name) =>
        ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Models, "modelProfiles", name).Value;

    private async Task ValidateDefinitionAsync(ModelProfileProperties properties, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentException.ThrowIfNullOrWhiteSpace(properties.DisplayName);
        if (!ResourceIdentifier.TryParse(properties.Provider.ResourceId, out var providerId)
            || !string.Equals(providerId.ProviderNamespace, AgentstrationProviderNamespaces.ModelProviders, StringComparison.Ordinal)
            || !string.Equals(providerId.ResourceType, "modelProviders", StringComparison.Ordinal))
            throw Invalid("properties.provider.resourceId", "The provider reference must target Agentstration.ModelProviders/modelProviders.");
        ModelProviderConfiguration provider;
        try { provider = await providerConfigurations.GetRequiredAsync(providerId.Value, cancellationToken); }
        catch (ModelProviderResolutionException) { throw Invalid("properties.provider.resourceId", "The referenced model provider does not exist."); }
        if (!provider.Capabilities.Contains("chat", StringComparer.OrdinalIgnoreCase))
            throw Invalid("properties.provider.resourceId", "The referenced model provider does not support chat.");
        if (string.IsNullOrWhiteSpace(properties.Model.Name)) throw Invalid("properties.model.name", "A model name is required.");
        if (properties.Generation.Temperature is < 0 or > 2) throw Invalid("properties.generation.temperature", "Temperature must be between 0 and 2.");
        if (properties.Generation.TopP is < 0 or > 1) throw Invalid("properties.generation.topP", "TopP must be between 0 and 1.");
        if (properties.Generation.TopK is <= 0) throw Invalid("properties.generation.topK", "TopK must be positive.");
        if (properties.Generation.MaxOutputTokens is <= 0) throw Invalid("properties.generation.maxOutputTokens", "MaxOutputTokens must be positive.");
        if (properties.Generation.StopSequences?.Any(string.IsNullOrEmpty) is true)
            throw Invalid("properties.generation.stopSequences", "Stop sequences cannot be empty.");
        if (properties.Reasoning.Mode == ReasoningMode.Disabled && properties.Reasoning.Effort is not null)
            throw Invalid("properties.reasoning.effort", "Reasoning effort cannot be set when reasoning is disabled.");
        if (properties.Output.Format == ModelOutputFormat.JsonSchema && properties.Output.JsonSchema is null)
            throw Invalid("properties.output.jsonSchema", "A JSON schema is required for JsonSchema output.");
        if (properties.Output.Format != ModelOutputFormat.JsonSchema && properties.Output.JsonSchema is not null)
            throw Invalid("properties.output.jsonSchema", "A JSON schema is only valid with JsonSchema output.");
        foreach (var providerOption in properties.ProviderOptions.Keys)
        {
            if (!string.Equals(providerOption, provider.ProviderType, StringComparison.OrdinalIgnoreCase))
                throw Invalid($"properties.providerOptions.{providerOption}", $"Provider options for '{providerOption}' cannot be used with provider '{provider.ProviderType}'.");
        }
        var providerValidator = optionsValidators.SingleOrDefault(candidate =>
            string.Equals(candidate.ProviderType, provider.ProviderType, StringComparison.OrdinalIgnoreCase));
        if (providerValidator is not null)
        {
            try { providerValidator.Validate(properties.ProviderOptions); }
            catch (ModelProviderConfigurationException exception)
            {
                throw Invalid($"properties.providerOptions.{provider.ProviderType}", exception.Message);
            }
        }
    }

    private static ModelProfileValidationException Invalid(string field, string message) => new(
        "model_profile_invalid",
        message,
        new Dictionary<string, string[]> { [field] = [message] });

    private static void ValidateIdentity(ModelProfileResource resource)
    {
        if (!string.Equals(resource.Type, AgentstrationResourceTypes.ModelProfiles, StringComparison.Ordinal))
            throw Invalid("type", $"Type must be '{AgentstrationResourceTypes.ModelProfiles}'.");
        if (!string.Equals(resource.ApiVersion, ManagementApiVersions.V20260801, StringComparison.Ordinal))
            throw Invalid("apiVersion", $"ApiVersion must be '{ManagementApiVersions.V20260801}'.");
        if (string.IsNullOrWhiteSpace(resource.ResourceGroup)) throw Invalid("resourceGroup", "ResourceGroup is required.");
        var expectedId = ProfileId(resource.ResourceGroup, resource.Name);
        if (!string.Equals(resource.Id, expectedId, StringComparison.Ordinal)) throw Invalid("name", "The model profile resource identity is invalid.");
    }
}

public static class ModelManagementServiceCollectionExtensions
{
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddAgentstrationModelManagement(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        services.AddSingleton<ModelProviderManagementService>();
        services.AddSingleton<IModelProviderConfigurationStore>(provider => provider.GetRequiredService<ModelProviderManagementService>());
        services.AddSingleton<ModelProfileManagementService>();
        services.AddSingleton<IModelProfileStore>(provider => provider.GetRequiredService<ModelProfileManagementService>());
        services.AddSingleton<IModelDeploymentStore>(provider => provider.GetRequiredService<ModelProfileManagementService>());
        services.AddSingleton<IModelProfileReferenceValidator>(provider => provider.GetRequiredService<ModelProfileManagementService>());
        return services;
    }
}
