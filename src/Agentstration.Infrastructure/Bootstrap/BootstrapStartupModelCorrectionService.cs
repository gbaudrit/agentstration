using Agentstration.Bootstrap.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ModelProviders;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Bootstrap;

internal sealed class BootstrapStartupModelCorrectionService(
    BootstrapStartupModelOverrideStore changes, IResourceStore store, ModelDiscoveryService discovery,
    ModelProfileManagementService profiles, ModelProviderManagementService providers,
    IResourceAccessPolicyService access, IPlatformAuthorizationService authorization,
    IRequestContextScopeFactory scopes) : IBootstrapStartupModelCorrections
{
    public Task<BootstrapStartupModelOptions> GetOptionsAsync(Guid attemptId, string resourceId, Guid actorPrincipalId,
        string? providerName, string? providerNamespace, CancellationToken cancellationToken) =>
        GuardAsync(() => GetOptionsCoreAsync(attemptId, resourceId, actorPrincipalId, providerName, providerNamespace, cancellationToken));

    public async Task ApplyAsync(string resourceId, BootstrapStartupModelChange request, Guid actorPrincipalId, CancellationToken cancellationToken) =>
        _ = await GuardAsync(async () => { await ApplyCoreAsync(resourceId, request, actorPrincipalId, cancellationToken); return true; });

    private static async Task<T> GuardAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (ResourceConcurrencyException) { throw new BootstrapStartupModelChangeException("bootstrap_substitution_stale", 409); }
        catch (UnauthorizedAccessException) { throw new BootstrapStartupModelChangeException("permission_denied", 403); }
        catch (ModelProviderAccessDeniedException) { throw new BootstrapStartupModelChangeException("permission_denied", 403); }
        catch (Exception exception) when (exception is ArgumentException or ModelProviderResolutionException or ModelProfileValidationException or ModelProviderValidationException)
        { throw new BootstrapStartupModelChangeException("bootstrap_model_selection_invalid", 400); }
    }

    private async Task<BootstrapStartupModelOptions> GetOptionsCoreAsync(Guid attemptId, string resourceId, Guid actorPrincipalId,
        string? providerName, string? providerNamespace, CancellationToken cancellationToken)
    {
        await EnsureAdministratorAsync(actorPrincipalId, cancellationToken);
        var entry = await changes.GetAsync(attemptId, resourceId, cancellationToken);
        using var system = scopes.PushSystem();
        var candidates = await store.ListExactAsync<ModelProviderResource>(ResourceScopeRef.Instance, ModelResourceKinds.ModelProvider, 0, 1000, cancellationToken);
        var choices = new List<BootstrapStartupModelProviderChoice>();
        foreach (var candidate in candidates)
        {
            var value = candidate.Value;
            var identity = new ResourceAccessIdentity(value.Uid, ResourceScopeRef.Instance, value.Namespace, value.Kind, value.Generation);
            if (!(await access.EvaluateAsync(identity, entry.OwnerScope, ResourceAccessAction.Discover, cancellationToken)).Allowed
                || !(await access.EvaluateAsync(identity, entry.OwnerScope, ResourceAccessAction.Use, cancellationToken)).Allowed) continue;
            choices.Add(new(value.Uid, new(value.Name, ResourceScopeRef.Instance, value.Namespace), value.Definition.DisplayName));
        }
        var selected = string.IsNullOrWhiteSpace(providerName) ? entry.Provider
            : new ResourceReference(providerName, ResourceScopeRef.Instance, ResourceNamespace.Parse(providerNamespace));
        selected = selected with { ScopeRef = ResourceScopeRef.Instance, Namespace = selected.Namespace ?? entry.Original.Namespace };
        var authorized = choices.FirstOrDefault(choice => choice.Reference.Name == selected.Name
            && choice.Reference.Namespace == (selected.Namespace ?? entry.Original.Namespace));
        var models = authorized is null ? [] : (await discovery.ListProviderModelsAsync(
            authorized.Reference.Namespace!.Value, authorized.Reference.Name, cancellationToken))
            .Where(model => model.Value.Definition.Observation.State == ModelObservationState.Available)
            .Select(model => model.Value.Definition.ExternalId).ToArray();
        return new(attemptId, resourceId, entry.Version, selected, entry.Model, choices, models);
    }

    private async Task ApplyCoreAsync(string resourceId, BootstrapStartupModelChange request, Guid actorPrincipalId, CancellationToken cancellationToken)
    {
        await EnsureAdministratorAsync(actorPrincipalId, cancellationToken);
        ArgumentNullException.ThrowIfNull(request.Provider);
        if (string.IsNullOrWhiteSpace(request.Model) || request.Model.Length > 512 || request.Provider.ScopeRef != ResourceScopeRef.Instance)
            throw new ArgumentException("bootstrap_model_selection_invalid", nameof(request));
        var entry = await changes.GetAsync(request.AttemptId, resourceId, cancellationToken);
        if (entry.Version != request.Version) throw new ResourceConcurrencyException("bootstrap_substitution_stale");
        using var system = scopes.PushSystem();
        if (await profiles.GetExactAsync(entry.OwnerScope, entry.Original.Namespace, entry.Original.Name, cancellationToken) is not null)
            throw new ResourceConcurrencyException("bootstrap_profile_already_applied");
        var config = await providers.GetAuthorizedConfigurationRequiredAsync(request.Provider, entry.Original.Namespace,
            entry.OwnerScope, requireDiscover: true, cancellationToken);
        if (config.Uid != request.ProviderUid) throw new ResourceConcurrencyException("bootstrap_provider_changed");
        var selectedModel = await discovery.FindAsync(config.Uid, request.Model, cancellationToken);
        if (selectedModel is null || selectedModel.Value.Definition.Observation.State != ModelObservationState.Available)
            throw new ArgumentException("bootstrap_model_unavailable", nameof(request));
        var definition = entry.Original.Definition with { Provider = request.Provider, Model = new() { Name = request.Model } };
        var candidate = entry.Original with { ScopeRef = entry.OwnerScope, Definition = definition };
        await profiles.ValidateForCreateAsync(candidate, cancellationToken);
        var specification = EffectiveModelSpecificationResolver.Resolve(selectedModel.Value.Definition.Specification,
            config.SpecificationOverrides.TryGetValue(request.Model, out var modelOverride) ? modelOverride : null);
        if (definition.Reasoning.Mode == ReasoningMode.Enabled
            && specification.Features.Reasoning?.Support is not (ModelFeatureSupport.Native or ModelFeatureSupport.Emulated or ModelFeatureSupport.Partial))
            throw new ArgumentException("bootstrap_model_incompatible", nameof(request));
        if (definition.Output.Format != ModelOutputFormat.Text)
        {
            var format = definition.Output.Format == ModelOutputFormat.JsonSchema ? ModelStructuredOutputFormat.JsonSchema : ModelStructuredOutputFormat.JsonObject;
            var output = specification.Features.StructuredOutput;
            if (output?.Support is not (ModelFeatureSupport.Native or ModelFeatureSupport.Emulated or ModelFeatureSupport.Partial)
                || !output.Formats.TryGetValue(format, out var options) || definition.Output.Strict && options.SupportsStrict != true)
                throw new ArgumentException("bootstrap_model_incompatible", nameof(request));
        }
        var exactReference = new ResourceReference(config.Name, ResourceScopeRef.Instance, config.Namespace);
        await changes.SaveAsync(entry, exactReference, config.Uid, request.Model, actorPrincipalId, cancellationToken);
    }

    private async Task EnsureAdministratorAsync(Guid actor, CancellationToken cancellationToken)
    {
        if (!await authorization.IsPlatformAdministratorAsync(actor, cancellationToken))
            throw new UnauthorizedAccessException("A Platform administrator is required.");
    }
}
