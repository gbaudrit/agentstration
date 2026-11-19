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
    IConfiguration configuration, TimeProvider timeProvider)
{
    public async Task WaitAsync(BootstrapResourceDocument document, BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var profile = WorkspaceBootstrapResource.Parse<ModelProfileResource>(document);
        var owner = operation.ProfileScope switch
        {
            BootstrapProfileScope.Instance => ResourceScopeRef.Instance,
            BootstrapProfileScope.Tenant => ResourceScopeRef.Tenant(operation.Target!.TenantId!.Value),
            _ => ResourceScopeRef.Workspace(operation.Target!.WorkspaceId!.Value)
        };
        // Create-only Bootstrap must check the retained definition when a profile already exists.
        profile = (await profiles.GetExactAsync(owner, profile.Namespace, profile.Name, cancellationToken))?.Value ?? profile;
        var reference = profile.Definition.Provider;
        var config = await providers.GetAuthorizedConfigurationRequiredAsync(reference, profile.Namespace, owner,
            requireDiscover: true, cancellationToken);
        var timeout = configuration.GetValue("Agentstration:Bootstrap:ModelDependencyTimeout", TimeSpan.FromMinutes(2));
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(30))
            throw new DeclarativeBootstrapException("Bootstrap ModelDependencyTimeout must be between one second and thirty minutes.");
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var lastState = "never";
        var initialAttempt = true;
        try
        {
            while (true)
            {
                wait.Token.ThrowIfCancellationRequested();
                using (scopes.PushSystem())
                {
                    var provider = await providers.GetExactAsync(ResourceScopeRef.Instance, config.Namespace, config.Name, wait.Token)
                        ?? throw new DeclarativeBootstrapException("The required model provider was removed during Bootstrap.");
                    if (provider.Value.Uid != config.Uid)
                        throw new DeclarativeBootstrapException("The required model provider was replaced during Bootstrap.");
                    var observation = await observations.ReadAsync(provider.Value, wait.Token);
                    var model = await models.FindAsync(config.Uid, profile.Definition.Model.Name, wait.Token);
                    if (observation.Health == "available" && observation.State == "succeeded"
                        && model?.Value.Definition.Observation.State == ModelObservationState.Available) return;
                    lastState = observation.ErrorCode ?? observation.State;
                    // Only required providers are expedited; optional providers never consume this deadline.
                    if (initialAttempt || observation.State is "never" or "succeeded")
                        await observations.RequestAsync(provider.Value, wait.Token);
                    initialAttempt = false;
                    await discovery.ProcessAsync(provider.Value, wait.Token);
                    observation = await observations.ReadAsync(provider.Value, wait.Token);
                    model = await models.FindAsync(config.Uid, profile.Definition.Model.Name, wait.Token);
                    lastState = observation.ErrorCode ?? observation.State;
                    if (observation.Health == "available" && observation.State == "succeeded"
                        && model?.Value.Definition.Observation.State == ModelObservationState.Available) return;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, wait.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DeclarativeBootstrapException($"[model_dependency_timeout] {operation.ProfileName}: ModelProfile/{profile.Name} requires model '{profile.Definition.Model.Name}' from provider '{config.Namespace}/{config.Name}'. Last discovery state: {lastState}. Verify the provider configuration and install the exact model upstream, then retry Bootstrap.");
        }
    }
}
