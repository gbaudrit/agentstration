using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Core;

public sealed class ModelProfileManagementService(
    IControlPlaneStore store,
    ModelProviderManagementService providerConfigurations,
    IEnumerable<IModelProviderDiscovery> discoveries,
    IEnumerable<IModelProviderCapabilitiesResolver> capabilityResolvers) : IModelProfileStore, IModelDeploymentStore, IModelProfileReferenceValidator
{
    public static string ProfileId(string name) => name;
    public async Task ValidateForCreateAsync(ModelProfileResource resource, CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        await ValidateDefinitionAsync(resource.Namespace, resource.Definition, cancellationToken);
    }

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
        var discovery = discoveries.SingleOrDefault(candidate => candidate.CanHandle(provider.AdapterType));
        if (discovery is null) return new(profile, provider, new("unknown", "No discovery adapter."), null, "unknown", ["No provider discovery adapter is registered."]);
        var health = await discovery.GetHealthAsync(provider, cancellationToken);
        if (!string.Equals(health.Status, "available", StringComparison.OrdinalIgnoreCase)) return new(profile, provider, health, null, "unavailable", [health.Details ?? "Provider unavailable."]);
        var models = await discovery.ListModelsAsync(provider, cancellationToken);
        var model = models.FirstOrDefault(value => value.Name == profile.Definition.Model.Name);
        if (model is null) return new(profile, provider, health, null, "unavailable", ["The configured model is not installed."]);
        if (!includeCapabilityDiagnostics) return new(profile, provider, health, model, "available", []);
        var capabilityResolver = capabilityResolvers.SingleOrDefault(candidate => candidate.CanHandle(provider.AdapterType));
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
                    provider.ContributionId,
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
        => await GetRequiredAsync(ResourceNamespace.Default, name, cancellationToken);

    public async ValueTask<ModelProfileConfiguration> GetRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default)
    {
        var profile = await GetAsync(@namespace, name, cancellationToken) ?? throw new ModelProfileNotFoundException($"{@namespace}/{name}");
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
        => await ((IModelDeploymentStore)this).GetRequiredAsync(ResourceNamespace.Default, name, cancellationToken);

    async ValueTask<ModelDeploymentConfiguration> IModelDeploymentStore.GetRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var profile = await GetAsync(@namespace, name, cancellationToken) ?? throw new ModelDeploymentNotFoundException($"{@namespace}/{name}");
        var provider = profile.Value.Definition.Provider.Resolve(profile.Value.Namespace, ResourceKinds.ModelProvider);
        return new() { Name = profile.Value.Metadata.Name, ProviderName = provider.Name, ProviderNamespace = provider.Namespace, ModelName = profile.Value.Definition.Model.Name, ProviderOptions = profile.Value.Definition.ProviderOptions };
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
            if (!string.Equals(option, provider.ContributionId, StringComparison.OrdinalIgnoreCase)) throw Invalid($"definition.providerOptions.{option}", $"Provider options for '{option}' cannot be used with contribution '{provider.ContributionId}'.");
        if (definition.ProviderOptions.TryGetValue(provider.ContributionId, out var options))
            ValidateVersionedOptions(provider.ContributionId, options);
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

