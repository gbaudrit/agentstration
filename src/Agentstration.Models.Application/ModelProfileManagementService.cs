using Agentstration.Aep.Abstractions;
using Agentstration.Agents;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.ModelProviders;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Models;

public sealed class ModelProfileManagementService(
    IResourceStore store,
    IResourceReferenceResolver references,
    ResourceScopeOperationService scopeOperations,
    ModelProviderManagementService providerConfigurations,
    IResourceAccessPolicyService accessPolicies,
    ModelDiscoveryService modelDiscovery,
    IEnumerable<IModelProviderDiscovery> discoveries,
    IEnumerable<IModelProviderCapabilitiesResolver> capabilityResolvers,
    IEnumerable<IExtensionInspector> inspectors,
    ICurrentRequestContext requestContext,
    IRequestContextScopeFactory requestScopes) : IModelProfileStore, IModelDeploymentStore, IModelProfileReferenceValidator
{
    public static string ProfileId(string name) => name;
    public async Task ValidateForCreateAsync(ModelProfileResource resource, CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ModelResourceKinds.ModelProfile);
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        await ValidateDefinitionAsync(resource.Namespace, resource.Definition, scopeRef, cancellationToken);
    }

    public async Task<StoredResource<ModelProfileResource>> CreateAsync(ModelProfileResource resource, CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ModelResourceKinds.ModelProfile);
        return await scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            await ValidateDefinitionAsync(resource.Namespace, resource.Definition, scopeRef, token);
            var address = ScopedResourceAddress.Create(scopeRef, resource.Namespace, ModelResourceKinds.ModelProfile, resource.Name);
            if (await store.GetExactAsync<ModelProfileResource>(address, token) is not null)
                throw new ResourceConcurrencyException($"Model profile '{resource.Address}' already exists in scope '{scopeRef}'.");
            return await store.PutExactAsync(scopeRef, resource with { ScopeRef = scopeRef, Generation = 1, Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded } }, null, true, token);
        }, cancellationToken);
    }

    public async Task<StoredResource<ModelProfileResource>> PutAsync(string name, ModelProfileProperties definition, string? ifMatch, CancellationToken cancellationToken)
        => await PutAsync(ResourceNamespace.Default, name, definition, ifMatch, cancellationToken);

    public async Task<StoredResource<ModelProfileResource>> PutAsync(ResourceNamespace @namespace, string name, ModelProfileProperties definition, string? ifMatch, CancellationToken cancellationToken)
        => await PutAsync(@namespace, name, null, definition, ifMatch, cancellationToken);

    public async Task<StoredResource<ModelProfileResource>> PutAsync(ResourceNamespace @namespace, string name, ResourceScopeRef? scopeRef, ModelProfileProperties definition, string? ifMatch, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, scopeRef, cancellationToken) ?? throw new ResourceNotFoundException(new(ModelResourceKinds.ModelProfile, name, @namespace));
        var resolvedScopeRef = existing.Value.ScopeRef ?? throw Invalid("scopeRef", "The model profile has no ownership scope.");
        return await scopeOperations.WriteAsync(existing.Value, resolvedScopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            await ValidateDefinitionAsync(existing.Value.Namespace, definition, resolvedScopeRef, token);
            return await store.PutExactAsync(resolvedScopeRef, existing.Value with
            {
                Generation = checked(existing.Value.Generation + 1),
                Definition = definition,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            }, ifMatch, false, token);
        }, cancellationToken);
    }

    public Task<StoredResource<ModelProfileResource>?> GetAsync(string name, CancellationToken cancellationToken) => store.GetAsync<ModelProfileResource>(new ResourceKey(ModelResourceKinds.ModelProfile, name), cancellationToken);
    public Task<IReadOnlyList<StoredResource<ModelProfileResource>>> ListAsync(CancellationToken cancellationToken) => store.ListAllAsync<ModelProfileResource>(ModelResourceKinds.ModelProfile, cancellationToken);
    public Task<StoredResource<ModelProfileResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => store.GetAsync<ModelProfileResource>(new ResourceKey(ModelResourceKinds.ModelProfile, name, @namespace), cancellationToken);
    public Task<StoredResource<ModelProfileResource>?> GetAsync(ResourceNamespace @namespace, string name, ResourceScopeRef? scopeRef, CancellationToken cancellationToken) =>
        scopeRef is { } exact ? GetExactAsync(exact, @namespace, name, cancellationToken) : GetAsync(@namespace, name, cancellationToken);
    public Task<StoredResource<ModelProfileResource>?> GetExactAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        store.GetExactAsync<ModelProfileResource>(ScopedResourceAddress.Create(scopeRef, @namespace, ModelResourceKinds.ModelProfile, name), cancellationToken);

    public Task DeleteAsync(string name, string? ifMatch, CancellationToken cancellationToken) =>
        DeleteAsync(ResourceNamespace.Default, name, ifMatch, cancellationToken);

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, string? ifMatch, CancellationToken cancellationToken)
        => await DeleteAsync(@namespace, name, null, ifMatch, cancellationToken);

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, ResourceScopeRef? scopeRef, string? ifMatch, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, scopeRef, cancellationToken) ?? throw new ResourceNotFoundException(new(ModelResourceKinds.ModelProfile, name, @namespace));
        var usages = await GetUsagesAsync(existing.Value, cancellationToken);
        if (usages.Count > 0) throw new ModelProfileInUseException(name, usages);
        var resolvedScopeRef = existing.Value.ScopeRef ?? throw Invalid("scopeRef", "The model profile has no ownership scope.");
        await scopeOperations.WriteAsync(existing.Value, resolvedScopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await store.DeleteExactAsync(ScopedResourceAddress.Create(resolvedScopeRef, @namespace, ModelResourceKinds.ModelProfile, name), ifMatch, token);
            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ModelProfileUsage>> GetUsagesAsync(string profileName, CancellationToken cancellationToken) =>
        await GetUsagesAsync(ResourceNamespace.Default, profileName, cancellationToken);

    public async Task<IReadOnlyList<ModelProfileUsage>> GetUsagesAsync(ResourceNamespace @namespace, string profileName, CancellationToken cancellationToken) =>
        await GetUsagesAsync(
            (await GetAsync(@namespace, profileName, cancellationToken))?.Value
                ?? throw new ResourceNotFoundException(new(ModelResourceKinds.ModelProfile, profileName, @namespace)),
            cancellationToken);

    public async Task<IReadOnlyList<ModelProfileUsage>> GetUsagesAsync(ModelProfileResource profile, CancellationToken cancellationToken)
    {
        var usages = new List<ModelProfileUsage>();
        foreach (var agent in await store.ListAllAsync<AgentResource>(AgentResourceKinds.Agent, cancellationToken))
        {
            if (agent.Value.ScopeRef is not { } agentScopeRef) continue;
            StoredResource<ModelProfileResource>? resolved;
            try
            {
                resolved = await references.ResolveAsync<ModelProfileResource>(
                    agent.Value.Definition.ModelProfile,
                    agent.Value.Namespace,
                    ModelResourceKinds.ModelProfile,
                    agentScopeRef,
                    cancellationToken);
            }
            catch (ResourceReferenceAmbiguousException)
            {
                continue;
            }
            if (resolved?.Value.Uid == profile.Uid)
                usages.Add(new(agent.Value.Kind, agent.Value.Metadata.Name, agent.Value.Definition.DisplayName));
        }
        return usages;
    }

    public async Task<ModelProfileResolution> ResolveAsync(
        ModelProfileResource profile,
        CancellationToken cancellationToken,
        bool includeCapabilityDiagnostics = false)
    {
        using var dependencyScope = EnterInstanceDependencyScope();
        ModelProviderConfiguration provider;
        try
        {
            provider = await providerConfigurations.GetAuthorizedConfigurationRequiredAsync(
            profile.Definition.Provider,
            profile.Namespace,
            profile.ScopeRef ?? throw Invalid("scopeRef", "The model profile has no ownership scope."),
            requireDiscover: false,
            cancellationToken);
        }
        catch (ModelProviderAccessDeniedException)
        {
            return new(profile, null, new("unavailable", "Provider dependency unavailable."), null,
                "unavailable", ["The referenced provider is not available to this Model Profile."]);
        }
        catch (ModelProviderResolutionException)
        {
            return new(profile, null, new("unavailable", "Provider not found."), null,
                "unavailable", ["The referenced provider does not exist."]);
        }
        var discovery = discoveries.SingleOrDefault(candidate => candidate.CanHandle(provider.AdapterType));
        if (discovery is null) return new(profile, provider, new("unknown", "No discovery adapter."), null, "unknown", ["No provider discovery adapter is registered."]);
        var health = await discovery.GetHealthAsync(provider, cancellationToken);
        if (!string.Equals(health.Status, "available", StringComparison.OrdinalIgnoreCase)) return new(profile, provider, health, null, "unavailable", [health.Details ?? "Provider unavailable."]);
        var modelResource = await modelDiscovery.FindAsync(provider.Uid, profile.Definition.Model.Name, cancellationToken);
        if (modelResource is null) return new(profile, provider, health, null, "unavailable", ["The configured model has not been discovered."]);
        var model = ModelDiscoveryService.ToDiscoveredModel(
            modelResource.Value,
            provider.SpecificationOverrides.GetValueOrDefault(profile.Definition.Model.Name));
        if (modelResource.Value.Definition.Observation.State != ModelObservationState.Available)
            return new(profile, provider, health, model, "unavailable",
                [modelResource.Value.Definition.Observation.State == ModelObservationState.Missing
                    ? "The configured model was not present during the latest successful discovery."
                    : "The latest model observation failed; the last valid observation was retained."]);
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
                Namespace = profile.Namespace,
                ScopeRef = profile.ScopeRef,
                ProviderName = provider.Name,
                ProviderNamespace = provider.Namespace,
                ProviderScopeRef = provider.ScopeRef ?? ResourceScopeRef.Instance,
                ModelName = profile.Definition.Model.Name,
                EffectiveSpecification = model.Specification,
                ProviderOptions = profile.Definition.ProviderOptions,
                SecretBindings = profile.Definition.SecretBindings
            };
            var levels = await capabilityResolver.ResolveCapabilitiesAsync(provider, deployment, cancellationToken);
            levels = levels with { Model = ModelSpecificationCapabilities.Map(model.Specification) };
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

    public async ValueTask<ModelProfileConfiguration> GetRequiredAsync(ResourceScopeRef scopeRef,
        ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default)
    {
        var profile = await GetExactAsync(scopeRef, @namespace, name, cancellationToken)
            ?? throw new ModelProfileNotFoundException($"{scopeRef}:{@namespace}/{name}");
        return ToConfiguration(profile.Value);
    }

    async ValueTask<ModelDeploymentConfiguration> IModelDeploymentStore.GetRequiredAsync(string name, CancellationToken cancellationToken)
        => await ((IModelDeploymentStore)this).GetRequiredAsync(ResourceNamespace.Default, name, cancellationToken);

    async ValueTask<ModelDeploymentConfiguration> IModelDeploymentStore.GetRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var profile = await GetAsync(@namespace, name, cancellationToken) ?? throw new ModelDeploymentNotFoundException($"{@namespace}/{name}");
        return await ToDeploymentAsync(profile.Value, cancellationToken);
    }

    async ValueTask<ModelDeploymentConfiguration> IModelDeploymentStore.GetRequiredAsync(ResourceScopeRef scopeRef,
        ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var profile = await GetExactAsync(scopeRef, @namespace, name, cancellationToken)
            ?? throw new ModelDeploymentNotFoundException($"{scopeRef}:{@namespace}/{name}");
        return await ToDeploymentAsync(profile.Value, cancellationToken);
    }

    private async Task<ModelDeploymentConfiguration> ToDeploymentAsync(ModelProfileResource profile,
        CancellationToken cancellationToken)
    {
        using var dependencyScope = EnterInstanceDependencyScope();
        var profileScopeRef = profile.ScopeRef
            ?? throw new ModelDeploymentNotFoundException($"{profile.Namespace}/{profile.Name}");
        var providerReference = profile.Definition.Provider.Resolve(profile.Namespace, ModelResourceKinds.ModelProvider);
        var provider = await providerConfigurations.GetAuthorizedConfigurationRequiredAsync(
            profile.Definition.Provider,
            profile.Namespace,
            profileScopeRef,
            requireDiscover: false,
            cancellationToken);
        var model = await modelDiscovery.FindAsync(provider.Uid, profile.Definition.Model.Name, cancellationToken);
        var effectiveSpecification = model is null
            ? null
            : EffectiveModelSpecificationResolver.Resolve(
                model.Value.Definition.Specification,
                provider.SpecificationOverrides.GetValueOrDefault(profile.Definition.Model.Name));
        return new()
        {
            Name = profile.Metadata.Name,
            Namespace = profile.Namespace,
            ScopeRef = profileScopeRef,
            ProviderName = providerReference.Name,
            ProviderNamespace = providerReference.Namespace,
            ProviderScopeRef = profile.Definition.Provider.ScopeRef ?? ResourceScopeRef.Instance,
            ModelName = profile.Definition.Model.Name,
            EffectiveSpecification = effectiveSpecification,
            ProviderOptions = profile.Definition.ProviderOptions,
            SecretBindings = profile.Definition.SecretBindings
        };
    }

    public async Task ValidateReferenceAsync(ResourceReference profileReference, ResourceNamespace ownerNamespace, ResourceScopeRef consumerScopeRef, CancellationToken cancellationToken)
        => _ = await ResolveReferenceAsync(profileReference, ownerNamespace, consumerScopeRef, cancellationToken);

    public async Task<ResolvedResourceDependency> ResolveReferenceAsync(ResourceReference profileReference, ResourceNamespace ownerNamespace, ResourceScopeRef consumerScopeRef, CancellationToken cancellationToken)
    {
        using var tenantResolutionScope = requestContext.AccessMode == ControlPlaneAccessMode.TenantAdministration
            ? requestScopes.PushTenant(requestContext.Current.PrincipalId, requestContext.Current.TenantId)
            : null;
        StoredResource<ModelProfileResource>? profile;
        try
        {
            profile = await references.ResolveAsync<ModelProfileResource>(
                profileReference, ownerNamespace, ModelResourceKinds.ModelProfile, consumerScopeRef, cancellationToken);
        }
        catch (ResourceReferenceAmbiguousException exception)
        {
            throw Invalid("definition.modelProfileRef.scopeRef", exception.Message);
        }
        catch (ResourceReferenceOutsideScopeException exception)
        {
            throw Invalid("definition.modelProfileRef.scopeRef", exception.Message);
        }
        if (profile is null)
            throw Invalid("definition.modelProfileRef.name", "The referenced model profile does not exist.");
        var profileScopeRef = profile.Value.ScopeRef ?? throw Invalid("scopeRef", "The model profile has no ownership scope.");
        if (profileScopeRef != consumerScopeRef)
        {
            var access = await accessPolicies.EvaluateAsync(new ResourceAccessIdentity(
                profile.Value.Uid, profileScopeRef, profile.Value.Namespace, ModelResourceKinds.ModelProfile,
                profile.Value.Generation), consumerScopeRef, ResourceAccessAction.Use, cancellationToken);
            if (!access.Allowed)
                throw Invalid("definition.modelProfileRef.scopeRef",
                    $"The model profile does not authorize use from '{consumerScopeRef}' ({access.ReasonCode}).");
        }
        await ValidateDefinitionAsync(
            profile.Value.Namespace,
            profile.Value.Definition,
            profileScopeRef,
            cancellationToken);
        return new(profile.Value.Uid, profileScopeRef, profile.Value.Namespace, ModelResourceKinds.ModelProfile,
            profile.Value.Name, profile.Value.Generation);
    }

    Task IModelProfileReferenceValidator.ValidateAsync(ResourceReference profileReference, ResourceNamespace ownerNamespace, ResourceScopeRef consumerScopeRef, CancellationToken cancellationToken) =>
        ValidateReferenceAsync(profileReference, ownerNamespace, consumerScopeRef, cancellationToken);

    async Task<ResolvedResourceDependency?> IModelProfileReferenceValidator.ResolveAsync(
        ResourceReference profileReference, ResourceNamespace ownerNamespace, ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken) =>
        await ResolveReferenceAsync(profileReference, ownerNamespace, consumerScopeRef, cancellationToken);

    private async Task ValidateDefinitionAsync(ResourceNamespace ownerNamespace, ModelProfileProperties definition, ResourceScopeRef ownerScopeRef, CancellationToken cancellationToken)
    {
        using var dependencyScope = EnterInstanceDependencyScope();
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DisplayName);
        if (definition.Provider.ScopeRef != ResourceScopeRef.Instance)
            throw Invalid("definition.provider.scopeRef", "A model profile requires an exact Instance Model Provider reference.");
        ModelProviderConfiguration provider;
        try
        {
            provider = await providerConfigurations.GetAuthorizedConfigurationRequiredAsync(
                definition.Provider, ownerNamespace, ownerScopeRef, requireDiscover: true, cancellationToken);
        }
        catch (ModelProviderAccessDeniedException)
        {
            throw Invalid("definition.provider.scopeRef",
                "The selected Model Provider is not available to the Model Profile owner scope.");
        }
        catch (ModelProviderResolutionException)
        {
            throw Invalid("definition.provider.name", "The referenced model provider does not exist.");
        }
        if (string.IsNullOrWhiteSpace(definition.Model.Name)) throw Invalid("definition.model.name", "A model name is required.");
        if (definition.SecretBindings is null) throw Invalid("definition.secretBindings", "Secret bindings must be an array.");
        if (definition.Generation.Temperature is < 0 or > 2) throw Invalid("definition.generation.temperature", "Temperature must be between 0 and 2.");
        foreach (var option in definition.ProviderOptions.Keys)
            if (!string.Equals(option, provider.ContributionId, StringComparison.OrdinalIgnoreCase)) throw Invalid($"definition.providerOptions.{option}", $"Provider options for '{option}' cannot be used with contribution '{provider.ContributionId}'.");
        if (definition.ProviderOptions.TryGetValue(provider.ContributionId, out var options))
            ValidateVersionedOptions(provider.ContributionId, options);
        if (definition.SecretBindings.Count > 0)
        {
            var inspector = inspectors.SingleOrDefault(candidate => candidate.CanHandle(provider.AdapterType))
                ?? throw Invalid("definition.secretBindings", "The model provider does not support AEP Secret bindings.");
            var inspection = await inspector.InspectAsync(provider, cancellationToken);
            if (!string.Equals(inspection.Status, "available", StringComparison.Ordinal))
                throw Invalid("definition.secretBindings", "The extension must be available to validate Secret bindings.");
            var issues = ExtensionSecretBindingValidator.Validate(
                definition.SecretBindings,
                inspection.ValueRequirements,
                AepContributionKinds.ModelProvider,
                provider.ContributionId,
                requireAll: false);
            if (issues.Count > 0)
                throw Invalid("definition.secretBindings", issues[0].Message);
        }
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

    private static ModelProfileConfiguration ToConfiguration(ModelProfileResource profile) => new()
    {
        Name = profile.Metadata.Name,
        DeploymentName = profile.Metadata.Name,
        Generation = profile.Definition.Generation,
        Reasoning = profile.Definition.Reasoning,
        Output = profile.Definition.Output,
        ProviderOptions = profile.Definition.ProviderOptions
    };

    private static ModelProfileValidationException Invalid(string field, string message) => new("model_profile_invalid", message, new Dictionary<string, string[]> { [field] = [message] });

    private IDisposable? EnterInstanceDependencyScope() => requestContext.AccessMode switch
    {
        ControlPlaneAccessMode.System or ControlPlaneAccessMode.Instance => null,
        _ when requestContext.IsInitialized => requestScopes.PushInstance(requestContext.Current),
        _ => requestScopes.PushSystem()
    };

    private static void ValidateIdentity(ModelProfileResource resource)
    {
        if (resource.Kind != ModelResourceKinds.ModelProfile) throw Invalid("kind", $"Kind must be '{ModelResourceKinds.ModelProfile}'.");
        if (resource.ApiVersion != ResourceApiVersions.CoreV1) throw Invalid("apiVersion", $"ApiVersion must be '{ResourceApiVersions.CoreV1}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name);
    }
}

