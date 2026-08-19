using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Core;

public sealed record ModelProviderView(ModelProviderConfiguration Configuration, ModelProviderHealth Health, IReadOnlyList<DiscoveredModel> Models, DateTimeOffset CheckedAt);
public sealed record ModelProfileUsage(string Kind, string Name, string DisplayName);
public sealed record ModelProviderUsage(string Kind, string Name, string DisplayName);
public sealed record ModelProfileResolution(
    ModelProfileResource Profile,
    ModelProviderConfiguration? Provider,
    ModelProviderHealth ProviderHealth,
    DiscoveredModel? Model,
    string Status,
    IReadOnlyList<string> Warnings,
    ResolvedModelProviderCapabilities? CapabilityLevels = null,
    EffectiveCapabilities? EffectiveCapabilities = null,
    IReadOnlyList<ExecutionCapabilityIssue>? Incompatibilities = null);

public sealed class ModelProfileValidationException(string code, string message, IReadOnlyDictionary<string, string[]>? errors = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
}
public sealed class ModelProfileInUseException(string profileName, IReadOnlyList<ModelProfileUsage> usages) : Exception($"The model profile '{profileName}' is used by {usages.Count} agent(s).")
{
    public string ProfileName { get; } = profileName;
    public IReadOnlyList<ModelProfileUsage> Usages { get; } = usages;
}
public sealed class ModelProviderUnavailableException(string providerName, string? details) : Exception($"Model provider '{providerName}' is unavailable{(string.IsNullOrWhiteSpace(details) ? "." : $": {details}")}");
public sealed class ModelProviderResourceNotFoundException(string providerName) : Exception($"Model provider '{providerName}' was not found.");
public sealed class ModelProviderValidationException(string message) : Exception(message);
public sealed class ModelProviderInUseException(string providerName, IReadOnlyList<ModelProviderUsage> usages) : Exception($"The model provider '{providerName}' is used by {usages.Count} model profile(s).")
{
    public IReadOnlyList<ModelProviderUsage> Usages { get; } = usages;
}

public sealed class ModelProviderManagementService(
    IControlPlaneStore store,
    IEnumerable<IModelProviderDiscovery> discoveries,
    IEnumerable<IModelProviderOptionsValidator> optionsValidators,
    TimeProvider timeProvider) : IModelProviderConfigurationStore
{
    public static string ModelProviderId(string name) => name;
    public async Task<StoredResource<ModelProviderResource>> CreateAsync(ModelProviderResource resource, CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        await ValidateCredentialAsync(resource.Namespace, resource.Definition.Credential, cancellationToken);
        if (await GetAsync(resource.Namespace, resource.Metadata.Name, cancellationToken) is not null) throw new ControlPlaneConcurrencyException($"Model provider '{resource.Address}' already exists.");
        return await store.PutAsync(resource with
        {
            Generation = 1,
            Definition = ValidateAndNormalize(resource.Definition),
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, null, true, cancellationToken);
    }

    public async Task<StoredResource<ModelProviderResource>> PutAsync(string name, ModelProviderProperties definition, string? ifMatch, CancellationToken cancellationToken)
        => await PutAsync(ResourceNamespace.Default, name, definition, ifMatch, cancellationToken);

    public async Task<StoredResource<ModelProviderResource>> PutAsync(ResourceNamespace @namespace, string name, ModelProviderProperties definition, string? ifMatch, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken) ?? throw new ModelProviderResourceNotFoundException(name);
        await ValidateCredentialAsync(existing.Value.Namespace, definition.Credential, cancellationToken);
        return await store.PutAsync(existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Definition = ValidateAndNormalize(definition),
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, ifMatch, false, cancellationToken);
    }

    public Task<StoredResource<ModelProviderResource>?> GetAsync(string name, CancellationToken cancellationToken) => store.GetAsync<ModelProviderResource>(new ResourceKey(ResourceKinds.ModelProvider, name), cancellationToken);
    public Task<StoredResource<ModelProviderResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => store.GetAsync<ModelProviderResource>(new ResourceKey(ResourceKinds.ModelProvider, name, @namespace), cancellationToken);

    public async Task<IReadOnlyList<ModelProviderView>> ListAsync(CancellationToken cancellationToken)
    {
        var resources = await store.ListAllAsync<ModelProviderResource>(ResourceKinds.ModelProvider, cancellationToken);
        return await Task.WhenAll(resources.Select(resource => InspectAsync(ToConfiguration(resource.Value), true, cancellationToken)));
    }

    public async Task<ModelProviderView> GetViewRequiredAsync(string name, CancellationToken cancellationToken)
        => await GetViewRequiredAsync(ResourceNamespace.Default, name, cancellationToken);

    public async Task<ModelProviderView> GetViewRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var stored = await GetAsync(@namespace, name, cancellationToken) ?? throw new ModelProviderResourceNotFoundException(name);
        return await InspectAsync(ToConfiguration(stored.Value), false, cancellationToken);
    }

    public async Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(string name, CancellationToken cancellationToken)
        => await ListModelsAsync(ResourceNamespace.Default, name, cancellationToken);

    public async Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var provider = await GetConfigurationRequiredAsync(@namespace, name, cancellationToken);
        var discovery = FindDiscovery(provider.ProviderType) ?? throw new ModelProviderUnavailableException(name, "No discovery adapter is registered in this host.");
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
        var discovery = FindDiscovery(provider.ProviderType);
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
        return ToConfiguration(resource.Value);
    }

    ValueTask<ModelProviderConfiguration> IModelProviderConfigurationStore.GetRequiredAsync(string name, CancellationToken cancellationToken) => new(GetConfigurationRequiredAsync(name, cancellationToken));

    async ValueTask<IReadOnlyList<ModelProviderConfiguration>> IModelProviderConfigurationStore.ListAsync(CancellationToken cancellationToken) =>
        (await store.ListAllAsync<ModelProviderResource>(ResourceKinds.ModelProvider, cancellationToken)).Select(resource => ToConfiguration(resource.Value)).ToArray();

    private ModelProviderProperties ValidateAndNormalize(ModelProviderProperties definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ProviderType);
        if (FindDiscovery(definition.ProviderType) is null) throw new ModelProviderValidationException($"Provider type '{definition.ProviderType}' is not registered in this host.");
        if (!definition.Endpoint.IsAbsoluteUri || definition.Endpoint.Scheme is not ("http" or "https")) throw new ModelProviderValidationException("Provider endpoint must be an absolute HTTP(S) URL.");
        if (!string.IsNullOrEmpty(definition.Endpoint.UserInfo) || !string.IsNullOrEmpty(definition.Endpoint.Query) || !string.IsNullOrEmpty(definition.Endpoint.Fragment))
            throw new ModelProviderValidationException("Provider endpoint cannot contain credentials, a query string, or a fragment.");
        foreach (var key in definition.ProviderOptions.Keys)
            if (!string.Equals(key, definition.ProviderType, StringComparison.OrdinalIgnoreCase)) throw new ModelProviderValidationException($"Provider options for '{key}' cannot be used with provider '{definition.ProviderType}'.");
        try { optionsValidators.SingleOrDefault(validator => validator.CanHandle(definition.ProviderType))?.Validate(definition.ProviderOptions); }
        catch (ModelProviderConfigurationException exception) { throw new ModelProviderValidationException(exception.Message); }
        return definition with
        {
            DisplayName = definition.DisplayName.Trim(),
            ProviderType = definition.ProviderType.Trim().ToLowerInvariant(),
            Endpoint = new Uri(definition.Endpoint.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute)
        };
    }

    private static ModelProviderConfiguration ToConfiguration(ModelProviderResource resource) => new()
    {
        Uid = resource.Uid,
        Namespace = resource.Namespace,
        Name = resource.Metadata.Name,
        ProviderType = resource.Definition.ProviderType,
        Endpoint = resource.Definition.Endpoint,
        DisplayName = resource.Definition.DisplayName,
        ManagementMode = resource.Definition.ManagementMode,
        EndpointDisplayName = resource.Definition.Endpoint.Authority,
        Credential = resource.Definition.Credential
    };

    private async Task ValidateCredentialAsync(ResourceNamespace ownerNamespace, ResourceReference? credential, CancellationToken cancellationToken)
    {
        if (credential is null) return;
        if (credential.WorkspaceRef is not null) throw new ModelProviderValidationException("Cross-workspace secret references are not supported.");
        var address = credential.Resolve(ownerNamespace, ResourceKinds.Secret);
        if (await store.GetAsync<SecretResource>(new(address.Kind, address.Name, address.Namespace), cancellationToken) is null)
            throw new ModelProviderValidationException($"Referenced secret '{address}' does not exist.");
    }

    private static void ValidateIdentity(ModelProviderResource resource)
    {
        if (resource.Kind != ResourceKinds.ModelProvider) throw new ModelProviderValidationException($"Kind must be '{ResourceKinds.ModelProvider}'.");
        if (resource.ApiVersion != ManagementApiVersions.CoreV1) throw new ModelProviderValidationException($"ApiVersion must be '{ManagementApiVersions.CoreV1}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name);
    }

    private IModelProviderDiscovery? FindDiscovery(string providerType) => discoveries.SingleOrDefault(discovery => discovery.CanHandle(providerType));
}

public sealed class ModelProfileManagementService(
    IControlPlaneStore store,
    ModelProviderManagementService providerConfigurations,
    IEnumerable<IModelProviderDiscovery> discoveries,
    IEnumerable<IModelProviderCapabilitiesResolver> capabilityResolvers) : IModelProfileStore, IModelDeploymentStore, IModelProfileReferenceValidator
{
    public static string ProfileId(string name) => name;
    public async Task<StoredResource<ModelProfileResource>> CreateAsync(ModelProfileResource resource, CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        await ValidateDefinitionAsync(resource.Namespace, resource.Definition, cancellationToken);
        if (await GetAsync(resource.Namespace, resource.Metadata.Name, cancellationToken) is not null) throw new ControlPlaneConcurrencyException($"Model profile '{resource.Address}' already exists.");
        return await store.PutAsync(resource with { Generation = 1, Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded } }, null, true, cancellationToken);
    }

    public async Task<StoredResource<ModelProfileResource>> PutAsync(string name, ModelProfileProperties definition, string? ifMatch, CancellationToken cancellationToken)
        => await PutAsync(ResourceNamespace.Default, name, definition, ifMatch, cancellationToken);

    public async Task<StoredResource<ModelProfileResource>> PutAsync(ResourceNamespace @namespace, string name, ModelProfileProperties definition, string? ifMatch, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ModelProfile, name, @namespace));
        await ValidateDefinitionAsync(existing.Value.Namespace, definition, cancellationToken);
        return await store.PutAsync(existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Definition = definition,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, ifMatch, false, cancellationToken);
    }

    public Task<StoredResource<ModelProfileResource>?> GetAsync(string name, CancellationToken cancellationToken) => store.GetAsync<ModelProfileResource>(new ResourceKey(ResourceKinds.ModelProfile, name), cancellationToken);
    public Task<IReadOnlyList<StoredResource<ModelProfileResource>>> ListAsync(CancellationToken cancellationToken) => store.ListAllAsync<ModelProfileResource>(ResourceKinds.ModelProfile, cancellationToken);
    public Task<StoredResource<ModelProfileResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => store.GetAsync<ModelProfileResource>(new ResourceKey(ResourceKinds.ModelProfile, name, @namespace), cancellationToken);

    public async Task DeleteAsync(string name, string? ifMatch, CancellationToken cancellationToken)
    {
        _ = await GetAsync(name, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ModelProfile, name));
        var usages = await GetUsagesAsync(name, cancellationToken);
        if (usages.Count > 0) throw new ModelProfileInUseException(name, usages);
        await store.DeleteAsync(new(ResourceKinds.ModelProfile, name), ifMatch, cancellationToken);
    }

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        _ = await GetAsync(@namespace, name, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ModelProfile, name, @namespace));
        var usages = await GetUsagesAsync(@namespace, name, cancellationToken);
        if (usages.Count > 0) throw new ModelProfileInUseException(name, usages);
        await store.DeleteAsync(new(ResourceKinds.ModelProfile, name, @namespace), ifMatch, cancellationToken);
    }

    public async Task<IReadOnlyList<ModelProfileUsage>> GetUsagesAsync(string profileName, CancellationToken cancellationToken) =>
        await GetUsagesAsync(ResourceNamespace.Default, profileName, cancellationToken);

    public async Task<IReadOnlyList<ModelProfileUsage>> GetUsagesAsync(ResourceNamespace @namespace, string profileName, CancellationToken cancellationToken) =>
        (await store.ListAllAsync<AgentResource>(ResourceKinds.Agent, cancellationToken))
            .Where(agent => agent.Value.Definition.ModelProfile.Resolve(agent.Value.Namespace, ResourceKinds.ModelProfile).Namespace == @namespace
                && agent.Value.Definition.ModelProfile.Name == profileName)
            .Select(agent => new ModelProfileUsage(agent.Value.Kind, agent.Value.Metadata.Name, agent.Value.Definition.DisplayName))
            .ToArray();

    public async Task<ModelProfileResolution> ResolveAsync(
        ModelProfileResource profile,
        CancellationToken cancellationToken,
        bool includeCapabilityDiagnostics = false)
    {
        ModelProviderConfiguration provider;
        var providerAddress = profile.Definition.Provider.Resolve(profile.Namespace, ResourceKinds.ModelProvider);
        try { provider = await providerConfigurations.GetConfigurationRequiredAsync(providerAddress.Namespace, providerAddress.Name, cancellationToken); }
        catch (ModelProviderResolutionException) { return new(profile, null, new("unavailable", "Provider not found."), null, "unavailable", ["The referenced provider does not exist."]); }
        var discovery = discoveries.SingleOrDefault(candidate => candidate.CanHandle(provider.ProviderType));
        if (discovery is null) return new(profile, provider, new("unknown", "No discovery adapter."), null, "unknown", ["No provider discovery adapter is registered."]);
        var health = await discovery.GetHealthAsync(provider, cancellationToken);
        if (!string.Equals(health.Status, "available", StringComparison.OrdinalIgnoreCase)) return new(profile, provider, health, null, "unavailable", [health.Details ?? "Provider unavailable."]);
        var models = await discovery.ListModelsAsync(provider, cancellationToken);
        var model = models.FirstOrDefault(value => value.Name == profile.Definition.Model.Name);
        if (model is null) return new(profile, provider, health, null, "unavailable", ["The configured model is not installed."]);
        if (!includeCapabilityDiagnostics) return new(profile, provider, health, model, "available", []);
        var capabilityResolver = capabilityResolvers.SingleOrDefault(candidate => candidate.CanHandle(provider.ProviderType));
        if (capabilityResolver is null)
        {
            return new(profile, provider, health, model, "unknown", ["Effective capabilities cannot be determined because no capability resolver is registered."]);
        }
        try
        {
            var deployment = new ModelDeploymentConfiguration
            {
                Name = profile.Metadata.Name,
                ProviderName = provider.Name,
                ModelName = profile.Definition.Model.Name,
                ProviderOptions = profile.Definition.ProviderOptions
            };
            var levels = await capabilityResolver.ResolveCapabilitiesAsync(provider, deployment, cancellationToken);
            var effective = EffectiveCapabilityResolver.Intersect(levels.Provider, levels.Model, levels.Adapter);
            IReadOnlyList<ExecutionCapabilityIssue> incompatibilities = [];
            try
            {
                ExecutionCompatibilityValidator.Validate(
                    profile.Definition.Reasoning,
                    profile.Definition.Output,
                    new ModelExecutionOptions(Streaming: RuntimeStreamingMode.Disabled),
                    effective,
                    provider.ProviderType,
                    model.Name,
                    "runtime not evaluated");
            }
            catch (ExecutionCompatibilityException exception)
            {
                incompatibilities = exception.Issues;
            }
            return new(
                profile,
                provider,
                health,
                model,
                incompatibilities.Count == 0 ? "available" : "incompatible",
                incompatibilities.Select(issue => issue.Message).ToArray(),
                levels,
                effective,
                incompatibilities);
        }
        catch (ModelProviderConfigurationException exception)
        {
            return new(profile, provider, health, model, "incompatible", [exception.Message]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new(profile, provider, health, model, "unknown", [$"Effective capabilities could not be resolved: {exception.Message}"]);
        }
    }

    public async ValueTask<ModelProfileConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default)
    {
        var profile = await GetAsync(name, cancellationToken) ?? throw new ModelProfileNotFoundException(name);
        return new()
        {
            Name = profile.Value.Metadata.Name,
            DeploymentName = profile.Value.Metadata.Name,
            Generation = profile.Value.Definition.Generation,
            Reasoning = profile.Value.Definition.Reasoning,
            Output = profile.Value.Definition.Output,
            ProviderOptions = profile.Value.Definition.ProviderOptions
        };
    }

    async ValueTask<ModelDeploymentConfiguration> IModelDeploymentStore.GetRequiredAsync(string name, CancellationToken cancellationToken)
    {
        var profile = await GetAsync(name, cancellationToken) ?? throw new ModelDeploymentNotFoundException(name);
        return new() { Name = profile.Value.Metadata.Name, ProviderName = profile.Value.Definition.Provider.Name, ModelName = profile.Value.Definition.Model.Name, ProviderOptions = profile.Value.Definition.ProviderOptions };
    }

    public async Task ValidateReferenceAsync(ResourceReference profileReference, CancellationToken cancellationToken)
    {
        if (profileReference.WorkspaceRef is not null) throw Invalid("definition.modelProfileRef.workspaceRef", "Cross-workspace references are not enabled in this installation.");
        var profileNamespace = profileReference.Namespace ?? ResourceNamespace.Default;
        var profile = await GetAsync(profileNamespace, profileReference.Name, cancellationToken) ?? throw Invalid("definition.modelProfileRef.name", "The referenced model profile does not exist.");
        await ValidateDefinitionAsync(profile.Value.Namespace, profile.Value.Definition, cancellationToken);
    }

    Task IModelProfileReferenceValidator.ValidateAsync(ResourceReference profileReference, CancellationToken cancellationToken) => ValidateReferenceAsync(profileReference, cancellationToken);

    private async Task ValidateDefinitionAsync(ResourceNamespace ownerNamespace, ModelProfileProperties definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DisplayName);
        if (definition.Provider.WorkspaceRef is not null) throw Invalid("definition.provider.workspaceRef", "Cross-workspace references are not enabled in this installation.");
        ModelProviderConfiguration provider;
        var providerAddress = definition.Provider.Resolve(ownerNamespace, ResourceKinds.ModelProvider);
        try { provider = await providerConfigurations.GetConfigurationRequiredAsync(providerAddress.Namespace, providerAddress.Name, cancellationToken); }
        catch (ModelProviderResolutionException) { throw Invalid("definition.provider.name", "The referenced model provider does not exist."); }
        if (string.IsNullOrWhiteSpace(definition.Model.Name)) throw Invalid("definition.model.name", "A model name is required.");
        if (definition.Generation.Temperature is < 0 or > 2) throw Invalid("definition.generation.temperature", "Temperature must be between 0 and 2.");
        foreach (var option in definition.ProviderOptions.Keys)
            if (!string.Equals(option, provider.ProviderType, StringComparison.OrdinalIgnoreCase)) throw Invalid($"definition.providerOptions.{option}", $"Provider options for '{option}' cannot be used with provider '{provider.ProviderType}'.");
        if (definition.ProviderOptions.TryGetValue(provider.ProviderType, out var options))
            ValidateVersionedOptions(provider.ProviderType, options);
    }

    private static void ValidateVersionedOptions(string providerType, VersionedExtensionOptions options)
    {
        var path = $"definition.providerOptions.{providerType}";
        if (string.IsNullOrWhiteSpace(options.OptionSet)) throw Invalid($"{path}.optionSet", "An option set identifier is required.");
        if (string.IsNullOrWhiteSpace(options.Version)) throw Invalid($"{path}.version", "An option set version is required.");
        if (string.IsNullOrWhiteSpace(options.SchemaDigest)
            || !options.SchemaDigest.StartsWith("sha256:", StringComparison.Ordinal)
            || options.SchemaDigest.Length != 71)
            throw Invalid($"{path}.schemaDigest", "A sha256 schema digest is required.");
        if (options.Values.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw Invalid($"{path}.values", "Extension option values must be a JSON object.");
    }

    private static ModelProfileValidationException Invalid(string field, string message) => new("model_profile_invalid", message, new Dictionary<string, string[]> { [field] = [message] });

    private static void ValidateIdentity(ModelProfileResource resource)
    {
        if (resource.Kind != ResourceKinds.ModelProfile) throw Invalid("kind", $"Kind must be '{ResourceKinds.ModelProfile}'.");
        if (resource.ApiVersion != ManagementApiVersions.CoreV1) throw Invalid("apiVersion", $"ApiVersion must be '{ManagementApiVersions.CoreV1}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name);
    }
}

public static class ModelManagementServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationModelManagement(this IServiceCollection services)
    {
        services.AddSingleton<ModelProviderManagementService>();
        services.AddSingleton<IModelProviderConfigurationStore>(provider => provider.GetRequiredService<ModelProviderManagementService>());
        services.AddSingleton<ModelProfileManagementService>();
        services.AddSingleton<IModelProfileStore>(provider => provider.GetRequiredService<ModelProfileManagementService>());
        services.AddSingleton<IModelDeploymentStore>(provider => provider.GetRequiredService<ModelProfileManagementService>());
        services.AddSingleton<IModelProfileReferenceValidator>(provider => provider.GetRequiredService<ModelProfileManagementService>());
        services.AddSingleton<ExtensionManagementService>();
        return services;
    }
}
