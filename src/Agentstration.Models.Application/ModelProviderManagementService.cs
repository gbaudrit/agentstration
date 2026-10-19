using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.ModelProviders;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Secrets.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Models;

public sealed class ModelProviderManagementService(
    IResourceStore store,
    IResourceReferenceResolver references,
    ResourceScopeOperationService scopeOperations,
    IEnumerable<IModelProviderDiscovery> discoveries,
    IEnumerable<IExtensionInspector> inspectors,
    IParameterResolver parameters,
    ISecretAccessAuthorizer secrets,
    TimeProvider timeProvider) : IModelProviderConfigurationStore
{
    public static string ModelProviderId(string name) => name;
    public async Task ValidateForCreateAsync(ModelProviderResource resource, CancellationToken cancellationToken)
        => await ValidateForCreateAsync(resource, null, cancellationToken);

    public async Task ValidateForCreateAsync(
        ModelProviderResource resource,
        IReadOnlyDictionary<ScopedResourceAddress, JsonElement>? plannedParameters,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ModelResourceKinds.ModelProvider);
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        _ = await ValidateAndNormalizeAsync(resource.Namespace, resource.Name, resource.Definition, scopeRef,
            plannedParameters, cancellationToken);
    }

    public async Task<StoredResource<ModelProviderResource>> CreateAsync(ModelProviderResource resource, CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ModelResourceKinds.ModelProvider);
        return await scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var address = ScopedResourceAddress.Create(scopeRef, resource.Namespace, ModelResourceKinds.ModelProvider, resource.Name);
            if (await store.GetExactAsync<ModelProviderResource>(address, token) is not null)
                throw new ResourceConcurrencyException($"Model provider '{resource.Address}' already exists in scope '{scopeRef}'.");
            var definition = await ValidateAndNormalizeAsync(resource.Namespace, resource.Name, resource.Definition, scopeRef, null, token);
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
            var validated = await ValidateAndNormalizeAsync(existing.Value.Namespace, existing.Value.Name, definition, scopeRef, null, token);
            return await store.PutExactAsync(scopeRef, existing.Value with
            {
                Generation = checked(existing.Value.Generation + 1),
                Definition = validated,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            }, ifMatch, false, token);
        }, cancellationToken);
    }

    public Task<StoredResource<ModelProviderResource>?> GetAsync(string name, CancellationToken cancellationToken) => store.GetAsync<ModelProviderResource>(new ResourceKey(ModelResourceKinds.ModelProvider, name), cancellationToken);
    public Task<StoredResource<ModelProviderResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => store.GetAsync<ModelProviderResource>(new ResourceKey(ModelResourceKinds.ModelProvider, name, @namespace), cancellationToken);
    public Task<StoredResource<ModelProviderResource>?> GetExactAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        store.GetExactAsync<ModelProviderResource>(ScopedResourceAddress.Create(scopeRef, @namespace, ModelResourceKinds.ModelProvider, name), cancellationToken);

    public async Task<IReadOnlyList<ModelProviderView>> ListAsync(CancellationToken cancellationToken)
    {
        var resources = await store.ListAllAsync<ModelProviderResource>(ModelResourceKinds.ModelProvider, cancellationToken);
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
        var provider = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ModelProviderResourceNotFoundException(name);
        return (await ListModelResourcesAsync(provider.Value, cancellationToken))
            .Select(value => ModelDiscoveryService.ToDiscoveredModel(
                value.Value,
                provider.Value.Definition.SpecificationOverrides.GetValueOrDefault(value.Value.Definition.ExternalId)))
            .ToArray();
    }

    public Task<ModelProviderView> GetStatusAsync(string name, CancellationToken cancellationToken) => GetViewRequiredAsync(name, cancellationToken);
    public Task<ModelProviderView> GetStatusAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => GetViewRequiredAsync(@namespace, name, cancellationToken);

    public async Task<IReadOnlyList<ModelProviderUsage>> GetUsagesAsync(string providerName, CancellationToken cancellationToken) =>
        await GetUsagesAsync(ResourceNamespace.Default, providerName, cancellationToken);

    public async Task<IReadOnlyList<ModelProviderUsage>> GetUsagesAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken) =>
        (await store.ListAllAsync<ModelProfileResource>(ModelResourceKinds.ModelProfile, cancellationToken))
            .Where(profile => profile.Value.Definition.Provider.Resolve(profile.Value.Namespace, ModelResourceKinds.ModelProvider).Namespace == @namespace
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
            foreach (var model in await ListModelResourcesAsync(existing.Value, token))
                await store.DeleteExactAsync(
                    ScopedResourceAddress.Create(scopeRef, model.Value.Namespace, ModelResourceKinds.Model, model.Value.Name),
                    model.ETag,
                    token);
            await store.DeleteExactAsync(ScopedResourceAddress.Create(scopeRef, @namespace, ModelResourceKinds.ModelProvider, name), ifMatch, token);
            return true;
        }, cancellationToken);
    }

    private async Task<ModelProviderView> InspectAsync(ModelProviderConfiguration provider, bool includeModels, CancellationToken cancellationToken)
    {
        var discovery = FindDiscovery(provider.AdapterType);
        if (discovery is null) return new(provider, new("unknown", "No discovery adapter is registered in this host."), [], timeProvider.GetUtcNow());
        var health = await discovery.GetHealthAsync(provider, cancellationToken);
        IReadOnlyList<DiscoveredModel> models = [];
        if (includeModels)
            models = (await ListModelResourcesAsync(provider, cancellationToken))
                .Select(value => ModelDiscoveryService.ToDiscoveredModel(
                    value.Value,
                    provider.SpecificationOverrides.GetValueOrDefault(value.Value.Definition.ExternalId)))
                .ToArray();
        return new(provider, health, models, timeProvider.GetUtcNow());
    }

    private async Task<IReadOnlyList<StoredResource<ModelResource>>> ListModelResourcesAsync(
        ModelProviderResource provider,
        CancellationToken cancellationToken)
    {
        var scopeRef = provider.ScopeRef
            ?? throw new ModelProviderValidationException("The model provider has no ownership scope.");
        return (await store.ListExactAsync<ModelResource>(scopeRef, ModelResourceKinds.Model, 0, ModelDiscoveryService.MaximumModels, cancellationToken))
            .Where(value => value.Value.Namespace == provider.Namespace
                && value.Value.Definition.ProviderUid == provider.Uid)
            .OrderBy(value => value.Value.Definition.ExternalId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<StoredResource<ModelResource>>> ListModelResourcesAsync(
        ModelProviderConfiguration provider,
        CancellationToken cancellationToken)
    {
        var scopeRef = provider.ScopeRef
            ?? throw new ModelProviderConfigurationException("The model provider has no ownership scope.");
        return (await store.ListExactAsync<ModelResource>(scopeRef, ModelResourceKinds.Model, 0, ModelDiscoveryService.MaximumModels, cancellationToken))
            .Where(value => value.Value.Namespace == provider.Namespace
                && value.Value.Definition.ProviderUid == provider.Uid)
            .OrderBy(value => value.Value.Definition.ExternalId, StringComparer.Ordinal)
            .ToArray();
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
            reference, ownerNamespace, ModelResourceKinds.ModelProvider, consumerScopeRef, cancellationToken)
            ?? throw new ModelProviderConfigurationNotFoundException(reference.Name);
        return await ToConfigurationAsync(resource.Value, cancellationToken);
    }

    ValueTask<ModelProviderConfiguration> IModelProviderConfigurationStore.GetRequiredAsync(string name, CancellationToken cancellationToken) => new(GetConfigurationRequiredAsync(name, cancellationToken));
    ValueTask<ModelProviderConfiguration> IModelProviderConfigurationStore.GetRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => new(GetConfigurationRequiredAsync(@namespace, name, cancellationToken));

    async ValueTask<IReadOnlyList<ModelProviderConfiguration>> IModelProviderConfigurationStore.ListAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll((await store.ListAllAsync<ModelProviderResource>(ModelResourceKinds.ModelProvider, cancellationToken))
            .Select(resource => ToConfigurationAsync(resource.Value, cancellationToken)));

    private async Task<ModelProviderProperties> ValidateAndNormalizeAsync(
        ResourceNamespace ownerNamespace,
        string ownerName,
        ModelProviderProperties definition,
        ResourceScopeRef ownerScopeRef,
        IReadOnlyDictionary<ScopedResourceAddress, JsonElement>? plannedParameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ContributionId);
        if (definition.ValueBindings is null)
            throw new ModelProviderValidationException("Value Bindings must be an array.");
        if (definition.SpecificationOverrides is null)
            throw new ModelProviderValidationException("Specification overrides must be an object.");
        var extensionAddress = definition.Extension.Resolve(ownerNamespace, ExtensionKinds.ExtensionRegistration);
        var extension = await references.ResolveAsync<ExtensionRegistrationResource>(definition.Extension, ownerNamespace,
            ExtensionKinds.ExtensionRegistration, ownerScopeRef, cancellationToken);
        if (extension is null)
            throw new ModelProviderValidationException($"Referenced extension registration '{extensionAddress}' does not exist or is not visible from '{ownerScopeRef}'.");
        if (FindDiscovery(AepModelProvider.AdapterType) is null)
            throw new ModelProviderValidationException("The AEP model-provider adapter is not registered in this host.");
        await ValidateValueBindingsAsync(ownerNamespace, ownerName, ownerScopeRef, definition, extension.Value,
            plannedParameters, cancellationToken);
        await ValidateSpecificationOverridesAsync(
            ownerNamespace, ownerName, ownerScopeRef, definition.SpecificationOverrides, cancellationToken);
        return definition with
        {
            DisplayName = definition.DisplayName.Trim(),
            ContributionId = definition.ContributionId.Trim(),
            ValueBindings = definition.ValueBindings.ToArray(),
            SpecificationOverrides = new Dictionary<string, ModelSpecificationOverride>(
                definition.SpecificationOverrides,
                StringComparer.Ordinal)
        };
    }

    private async Task ValidateSpecificationOverridesAsync(
        ResourceNamespace ownerNamespace,
        string ownerName,
        ResourceScopeRef ownerScopeRef,
        IReadOnlyDictionary<string, ModelSpecificationOverride> overrides,
        CancellationToken cancellationToken)
    {
        if (overrides.Count > ModelDiscoveryService.MaximumModels)
            throw new ModelProviderValidationException(
                $"A Model Provider cannot define more than {ModelDiscoveryService.MaximumModels} specification overrides.");

        var models = (await store.ListExactAsync<ModelResource>(
                ownerScopeRef,
                ModelResourceKinds.Model,
                0,
                ModelDiscoveryService.MaximumModels,
                cancellationToken))
            .Where(value => value.Value.Namespace == ownerNamespace
                && value.Value.Definition.Provider.Name == ownerName)
            .ToDictionary(value => value.Value.Definition.ExternalId, StringComparer.Ordinal);

        foreach (var (externalId, specificationOverride) in overrides)
        {
            if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 256)
                throw new ModelProviderValidationException(
                    "A specification override key must be an exact discovered model identifier containing between 1 and 256 characters.");
            if (specificationOverride is null)
                throw new ModelProviderValidationException($"Specification override for model '{externalId}' cannot be null.");
            if (!models.TryGetValue(externalId, out var model))
                throw new ModelProviderValidationException(
                    $"Specification override target '{externalId}' is not a discovered Model owned by this Model Provider.");
            if (IsEmpty(specificationOverride))
                throw new ModelProviderValidationException(
                    $"Specification override for model '{externalId}' must contain at least one explicit value.");
            try
            {
                _ = EffectiveModelSpecificationResolver.Resolve(model.Value.Definition.Specification, specificationOverride);
            }
            catch (ArgumentException exception)
            {
                throw new ModelProviderValidationException(
                    $"Specification override for model '{externalId}' is invalid: {exception.Message}");
            }
        }
    }

    private static bool IsEmpty(ModelSpecificationOverride value) =>
        value.Input is null
        && value.Output is null
        && value.Features.Streaming is null
        && value.Features.Tools is null
        && value.Features.StructuredOutput is null
        && value.Features.Reasoning is null
        && value.Limits.ContextTokens is null
        && value.Limits.MaxOutputTokens is null;

    private async Task ValidateValueBindingsAsync(
        ResourceNamespace ownerNamespace,
        string ownerName,
        ResourceScopeRef ownerScopeRef,
        ModelProviderProperties definition,
        ExtensionRegistrationResource extension,
        IReadOnlyDictionary<ScopedResourceAddress, JsonElement>? plannedParameters,
        CancellationToken cancellationToken)
    {
        // A required secured value may intentionally be supplied by the existing Model Profile Secret override.
        if (definition.ValueBindings.Count == 0) return;
        var inspector = inspectors.SingleOrDefault(value => value.CanInspectEndpoint(extension.Definition.Endpoint));
        if (inspector is null)
            throw new ModelProviderValidationException("No AEP inspector can validate the Model Provider Value Bindings.");
        var inspection = await inspector.InspectAsync(extension, cancellationToken);
        if (!string.Equals(inspection.Status, "available", StringComparison.Ordinal))
            throw new ModelProviderValidationException("The extension must be available to validate Model Provider Value Bindings.");
        if (!inspection.Contributions.Any(value =>
                string.Equals(value.Kind, AepContributionKinds.ModelProvider, StringComparison.Ordinal)
                && string.Equals(value.Id, definition.ContributionId, StringComparison.OrdinalIgnoreCase)))
            throw new ModelProviderValidationException($"The extension does not contribute Model Provider '{definition.ContributionId}'.");
        var issues = ModelProviderValueBindingValidator.Validate(
            definition.ValueBindings, inspection.ValueRequirements, definition.ContributionId, requireAll: true);
        if (issues.Count > 0) throw new ModelProviderValidationException(issues[0].Message);

        var consumer = new ParameterResolutionContext(ownerScopeRef,
            ResourceAddress.Create(ownerNamespace, ModelResourceKinds.ModelProvider, ownerName));
        var inlineValues = new List<AepBoundValue>();
        foreach (var binding in definition.ValueBindings)
        {
            if (binding.Kind == ModelProviderValueBindingKind.Parameter)
            {
                try
                {
                    var parameter = binding.Parameter!;
                    var resolved = await parameters.ResolveAsync(parameter, consumer, cancellationToken);
                    if (resolved is not null)
                    {
                        inlineValues.Add(AepBoundValue.Inline(binding.RequirementId, resolved.Value));
                        continue;
                    }

                    var address = ScopedResourceAddress.Create(parameter.ScopeRef, parameter.Address.Namespace,
                        ParameterResourceKinds.Parameter, parameter.Address.Name);
                    if (plannedParameters is null || !plannedParameters.TryGetValue(address, out var plannedValue))
                        throw new ModelProviderValidationException($"Parameter for Value requirement '{binding.RequirementId}' was not found.");
                    inlineValues.Add(AepBoundValue.Inline(binding.RequirementId, plannedValue));
                }
                catch (ParameterAccessDeniedException exception)
                {
                    throw new ModelProviderValidationException(exception.Message);
                }
            }
            else
            {
                try
                {
                    var status = await secrets.GetAuthorizedStatusAsync(binding.Secret!,
                        new SecretResolutionContext(ownerScopeRef, consumer.Consumer), cancellationToken);
                    if (status != SecretValueStatus.Configured)
                        throw new ModelProviderValidationException($"Secret for Value requirement '{binding.RequirementId}' is not configured.");
                }
                catch (SecretResolutionException exception)
                {
                    throw new ModelProviderValidationException(exception.Message);
                }
            }
        }
        var valueIssues = AepBoundValueValidator.Validate(inlineValues, inspection.ValueRequirements,
            AepContributionKinds.ModelProvider, definition.ContributionId, requireAll: false);
        if (valueIssues.Count > 0)
            throw new ModelProviderValidationException($"Value requirement '{valueIssues[0].RequirementId}' failed validation ({valueIssues[0].Code}).");
    }

    private async Task<ModelProviderConfiguration> ToConfigurationAsync(ModelProviderResource resource, CancellationToken cancellationToken)
    {
        var extensionAddress = resource.Definition.Extension.Resolve(resource.Namespace, ExtensionKinds.ExtensionRegistration);
        var extension = await references.ResolveAsync<ExtensionRegistrationResource>(
            resource.Definition.Extension,
            resource.Namespace,
            ExtensionKinds.ExtensionRegistration,
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
            Credential = extension.Value.Definition.Credential,
            ValueBindings = resource.Definition.ValueBindings,
            SpecificationOverrides = resource.Definition.SpecificationOverrides
        };
    }

    private static void ValidateIdentity(ModelProviderResource resource)
    {
        if (resource.Kind != ModelResourceKinds.ModelProvider) throw new ModelProviderValidationException($"Kind must be '{ModelResourceKinds.ModelProvider}'.");
        if (resource.ApiVersion != ResourceApiVersions.CoreV1) throw new ModelProviderValidationException($"ApiVersion must be '{ResourceApiVersions.CoreV1}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name);
    }

    private IModelProviderDiscovery? FindDiscovery(string providerType) => discoveries.SingleOrDefault(discovery => discovery.CanHandle(providerType));
}
