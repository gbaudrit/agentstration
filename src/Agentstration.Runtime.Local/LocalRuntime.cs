using System.Collections.Concurrent;
using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.AI;

namespace Agentstration.Runtime.Local;

public sealed class RuntimeRegistry : IRuntimeRegistry
{
    private readonly ConcurrentDictionary<string, IAgentRuntime> _runtimes = new(StringComparer.Ordinal);
    public void Set(string deploymentId, IAgentRuntime runtime) => _runtimes[deploymentId] = runtime;
    public bool TryGet(string deploymentId, out IAgentRuntime? runtime) => _runtimes.TryGetValue(deploymentId, out runtime);
    public bool Remove(string deploymentId) => _runtimes.TryRemove(deploymentId, out _);

    public async Task<AgentExecutionResult> ExecuteAsync(string deploymentId, AgentExecutionRequest request, CancellationToken cancellationToken)
    {
        if (!_runtimes.TryGetValue(deploymentId, out var runtime)) throw new InvalidOperationException($"Deployment '{deploymentId}' has no active runtime instance.");
        return await runtime.ExecuteAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<AgentExecutionEvent> ExecuteEventsAsync(
        string deploymentId,
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_runtimes.TryGetValue(deploymentId, out var runtime))
            throw new InvalidOperationException($"Deployment '{deploymentId}' has no active runtime instance.");
        return runtime.ExecuteEventsAsync(request, cancellationToken);
    }
}

public sealed class SingleChatClientResolver(IChatClient chatClient) : IChatClientResolver
{
    public ValueTask<IChatClient> ResolveAsync(string modelProfileResourceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelProfileResourceId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(chatClient);
    }
}

public sealed class EmptyToolCatalog : IToolCatalog
{
    public ValueTask<IReadOnlyCollection<IAgentTool>> ResolveAsync(IEnumerable<string> toolIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = toolIds.Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length > 0) throw new InvalidOperationException($"No tools are registered in the standalone catalog. Requested: {string.Join(", ", requested)}.");
        return ValueTask.FromResult<IReadOnlyCollection<IAgentTool>>([]);
    }
}

public abstract class LocalAgentProvisioner(
    IEnumerable<IAgentRuntimeFactory> factories,
    AgentRuntimeContext context,
    IRuntimeRegistry registry) : IAgentDeploymentProvisioner
{
    public abstract AgentHostingMode HostingMode { get; }

    public async Task<ProvisioningResult> ProvisionAsync(AgentRevision revision, AgentDeployment deployment, CancellationToken cancellationToken)
    {
        var factory = factories.SingleOrDefault(value => string.Equals(value.Handler, revision.Definition.Handler, StringComparison.Ordinal));
        if (factory is null) return new ProvisioningResult(false, null, $"No runtime factory is registered for handler '{revision.Definition.Handler}'.");
        try
        {
            var runtime = await factory.CreateAsync(revision.Definition, revision.Id, context, cancellationToken);
            registry.Set(deployment.Id, runtime);
            return new ProvisioningResult(true, $"inprocess://{deployment.Name}", null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ProvisioningResult(false, null, exception.Message);
        }
    }

    public Task<ProvisioningResult> DeprovisionAsync(AgentDeployment deployment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        registry.Remove(deployment.Id);
        return Task.FromResult(new ProvisioningResult(true, null, null));
    }

    public Task<RuntimeObservation> ObserveAsync(AgentDeployment deployment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(registry.TryGet(deployment.Id, out var runtime)
            ? new RuntimeObservation(OperationalState.Ready, runtime!.RevisionId, null)
            : new RuntimeObservation(OperationalState.Unavailable, null, "Runtime instance is missing."));
    }
}

public sealed class InProcessAgentProvisioner(IEnumerable<IAgentRuntimeFactory> factories, AgentRuntimeContext context, IRuntimeRegistry registry)
    : LocalAgentProvisioner(factories, context, registry)
{
    public override AgentHostingMode HostingMode => AgentHostingMode.InProcess;
}

public sealed class SharedHostAgentProvisioner(IEnumerable<IAgentRuntimeFactory> factories, AgentRuntimeContext context, IRuntimeRegistry registry)
    : LocalAgentProvisioner(factories, context, registry)
{
    public override AgentHostingMode HostingMode => AgentHostingMode.SharedHost;
}

public sealed class LocalAgentDeploymentReconciler(
    IEnumerable<IAgentDeploymentProvisioner> provisioners,
    IControlPlaneStore store,
    IRuntimeRegistry registry) : IAgentDeploymentReconciler
{
    public async Task<ReconciliationResult> ReconcileAsync(AgentDeployment deployment, CancellationToken cancellationToken)
    {
        var provisioner = provisioners.SingleOrDefault(value => value.HostingMode == deployment.HostingMode);
        if (provisioner is null)
        {
            return Failed(deployment, $"Hosting mode '{deployment.HostingMode}' is not available in this host.");
        }

        if (deployment.DesiredState == DesiredAgentState.Stopped)
        {
            await provisioner.DeprovisionAsync(deployment, cancellationToken);
            var stopped = deployment with
            {
                ProvisioningState = ProvisioningState.Succeeded,
                OperationalState = OperationalState.Stopped,
                ObservedRevisionId = null,
                LastError = null
            };
            return new ReconciliationResult(stopped, stopped != deployment, "Deployment is stopped.");
        }

        if (registry.TryGet(deployment.Id, out var current) && string.Equals(current!.RevisionId, deployment.RevisionId, StringComparison.Ordinal))
        {
            var observation = await provisioner.ObserveAsync(deployment, cancellationToken);
            var observed = deployment with
            {
                ProvisioningState = ProvisioningState.Succeeded,
                OperationalState = observation.State,
                ObservedRevisionId = observation.RevisionId,
                LastError = observation.Error
            };
            return new ReconciliationResult(observed, observed != deployment, "Existing runtime observed.");
        }

        if (current is not null) await provisioner.DeprovisionAsync(deployment, cancellationToken);
        var revision = await store.GetAsync<AgentRevision>(deployment.RevisionId, cancellationToken);
        if (revision is null) return Failed(deployment, $"Revision '{deployment.RevisionId}' does not exist.");
        var result = await provisioner.ProvisionAsync(revision.Value, deployment, cancellationToken);
        if (!result.Succeeded) return Failed(deployment, result.Error ?? "Provisioning failed.");
        var ready = deployment with
        {
            ProvisioningState = ProvisioningState.Succeeded,
            OperationalState = OperationalState.Ready,
            ObservedRevisionId = deployment.RevisionId,
            LastError = null
        };
        return new ReconciliationResult(ready, true, "Runtime provisioned.");
    }

    private static ReconciliationResult Failed(AgentDeployment deployment, string error) => new(
        deployment with { ProvisioningState = ProvisioningState.Failed, OperationalState = OperationalState.Degraded, LastError = error },
        true,
        error);
}

public abstract class UnsupportedAgentProvisioner : IAgentDeploymentProvisioner
{
    public abstract AgentHostingMode HostingMode { get; }
    public Task<ProvisioningResult> ProvisionAsync(AgentRevision revision, AgentDeployment deployment, CancellationToken cancellationToken) => Task.FromResult(new ProvisioningResult(false, null, $"Hosting mode '{HostingMode}' is not implemented."));
    public Task<ProvisioningResult> DeprovisionAsync(AgentDeployment deployment, CancellationToken cancellationToken) => Task.FromResult(new ProvisioningResult(true, null, null));
    public Task<RuntimeObservation> ObserveAsync(AgentDeployment deployment, CancellationToken cancellationToken) => Task.FromResult(new RuntimeObservation(OperationalState.Unavailable, null, $"Hosting mode '{HostingMode}' is not implemented."));
}

public sealed class DedicatedProcessAgentProvisioner : UnsupportedAgentProvisioner { public override AgentHostingMode HostingMode => AgentHostingMode.DedicatedProcess; }
public sealed class DedicatedContainerAgentProvisioner : UnsupportedAgentProvisioner { public override AgentHostingMode HostingMode => AgentHostingMode.DedicatedContainer; }
public sealed class RemoteEndpointAgentProvisioner : UnsupportedAgentProvisioner { public override AgentHostingMode HostingMode => AgentHostingMode.RemoteEndpoint; }
public sealed class FoundryAgentProvisioner : UnsupportedAgentProvisioner { public override AgentHostingMode HostingMode => AgentHostingMode.FoundryHosted; }
