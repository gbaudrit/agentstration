using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.Configuration;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed class BootstrapModelDependencies(
    ModelProviderManagementService providers, ModelProfileManagementService profiles,
    ModelDiscoveryService models, ModelProviderDiscoveryLifecycle discovery,
    ModelProviderDiscoveryState observations, IRequestContextScopeFactory scopes,
    IConfiguration configuration, TimeProvider timeProvider, BootstrapStartupState? startup = null, BootstrapStartupModelOverrideStore? corrections = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<BootstrapResourceDocument> WaitAsync(BootstrapResourceDocument document, BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var profile = WorkspaceBootstrapResource.Parse<ModelProfileResource>(document);
        var owner = operation.ProfileScope switch
        {
            BootstrapProfileScope.Instance => ResourceScopeRef.Instance,
            BootstrapProfileScope.Tenant => ResourceScopeRef.Tenant(operation.Target!.TenantId!.Value),
            _ => ResourceScopeRef.Workspace(operation.Target!.WorkspaceId!.Value)
        };
        // Retained profiles keep their saved definition. Only an unapplied initial
        // profile may accept an explicit, versioned startup substitution.
        var existing = await profiles.GetExactAsync(owner, profile.Namespace, profile.Name, cancellationToken);
        profile = existing?.Value ?? profile;
        var canChange = existing is null && operation.StartupResourceId is not null && corrections is not null;
        var attempt = startup?.Read().AttemptId ?? Guid.Empty;
        BootstrapPendingModel? pending = canChange
            ? await corrections!.RegisterAsync(profile, operation, owner, cancellationToken) : null;
        var timeout = configuration.GetValue("Agentstration:Bootstrap:ModelDependencyTimeout", TimeSpan.FromMinutes(2));
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(30))
            throw new DeclarativeBootstrapException("Bootstrap ModelDependencyTimeout must be between one second and thirty minutes.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pending is not null) pending = await corrections!.GetAsync(attempt, pending.ResourceId, cancellationToken);
            var effective = pending is null ? profile : profile with
            {
                Definition = profile.Definition with { Provider = pending.Provider, Model = new() { Name = pending.Model } }
            };
            var config = await providers.GetAuthorizedConfigurationRequiredAsync(effective.Definition.Provider,
                effective.Namespace, owner, requireDiscover: true, cancellationToken);
            if (pending?.ProviderUid is { } expectedUid && config.Uid != expectedUid)
                throw new DeclarativeBootstrapException("bootstrap_substitution_provider_changed");
            using var deadline = new CancellationTokenSource(timeout, timeProvider);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var lastState = "never";
            var initialAttempt = true;
            try
            {
                while (true)
                {
                    wait.Token.ThrowIfCancellationRequested();
                    if (pending is not null && (await corrections!.GetAsync(attempt, pending.ResourceId, cancellationToken)).Version != pending.Version)
                        break;
                    using (scopes.PushSystem())
                    {
                        var provider = await providers.GetExactAsync(ResourceScopeRef.Instance, config.Namespace, config.Name, wait.Token)
                            ?? throw new DeclarativeBootstrapException("The required model provider was removed during Bootstrap.");
                        if (provider.Value.Uid != config.Uid)
                            throw new DeclarativeBootstrapException("The required model provider was replaced during Bootstrap.");
                        var observation = await observations.ReadAsync(provider.Value, wait.Token);
                        var model = await models.FindAsync(config.Uid, effective.Definition.Model.Name, wait.Token);
                        var available = observation.Health == "available" && observation.State == "succeeded"
                            && model?.Value.Definition.Observation.State == ModelObservationState.Available;
                        if (!available)
                        {
                            lastState = observation.ErrorCode ?? observation.State;
                            startup?.ObserveModelWait(effective.Name, config.Name, effective.Definition.Model.Name,
                                observation.Health == "available" ? "required_model_missing" : "provider_unavailable",
                                config.Namespace.Value, (config.ScopeRef ?? ResourceScopeRef.Instance).Value, canChange);
                            if (initialAttempt || observation.State is "never" or "succeeded")
                                await observations.RequestAsync(provider.Value, wait.Token);
                            initialAttempt = false;
                            await discovery.ProcessAsync(provider.Value, wait.Token);
                            observation = await observations.ReadAsync(provider.Value, wait.Token);
                            model = await models.FindAsync(config.Uid, effective.Definition.Model.Name, wait.Token);
                            lastState = observation.ErrorCode ?? observation.State;
                            available = observation.Health == "available" && observation.State == "succeeded"
                                && model?.Value.Definition.Observation.State == ModelObservationState.Available;
                        }
                        if (available)
                        {
                            if (pending is not null && !await corrections!.TryCloseAsync(attempt, pending.ResourceId, pending.Version, cancellationToken)) break;
                            operation.ObserveOperation?.Invoke("model_discovery", "succeeded");
                            if (pending is null || pending.Version == 0) return document;
                            var definition = JsonNode.Parse(document.Definition.GetRawText())!.AsObject();
                            definition["provider"] = JsonSerializer.SerializeToNode(effective.Definition.Provider, JsonOptions);
                            definition["model"] = JsonSerializer.SerializeToNode(effective.Definition.Model, JsonOptions);
                            return document with { Definition = JsonSerializer.SerializeToElement(definition) };
                        }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, wait.Token);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                if (pending is not null && (await corrections!.GetAsync(attempt, pending.ResourceId, cancellationToken)).Version != pending.Version)
                    continue;
                throw new DeclarativeBootstrapException($"[model_dependency_timeout] {operation.ProfileName}: ModelProfile/{effective.Name} requires model '{effective.Definition.Model.Name}' from provider '{config.Namespace}/{config.Name}'. Last discovery state: {lastState}.");
            }
        }
    }
}
