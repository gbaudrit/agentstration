using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class RuntimeProfileValidationException(string message) : Exception(message);
public sealed class RuntimeProfileInUseException(string profileName, IReadOnlyList<RuntimeProfileUsage> usages)
    : Exception($"The runtime profile '{profileName}' is used by {usages.Count} deployment(s).")
{
    public IReadOnlyList<RuntimeProfileUsage> Usages { get; } = usages;
}
public sealed record RuntimeProfileUsage(Guid DeploymentUid, string Name, string Environment, string AgentName);

public sealed class RuntimeProfileManagementService(
    IControlPlaneStore store,
    ResourceScopeOperationService scopeOperations,
    IAgentDeploymentReconciler reconciler)
{
    public static string ProfileId(string name) => name;
    public void ValidateForCreate(RuntimeProfileResource resource) => Validate(resource);

    public async Task<StoredResource<RuntimeProfileResource>> CreateAsync(RuntimeProfileResource resource, CancellationToken cancellationToken)
    {
        Validate(resource);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ResourceKinds.RuntimeProfile);
        return await scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var address = ScopedResourceAddress.Create(scopeRef, resource.Namespace, ResourceKinds.RuntimeProfile, resource.Name);
            if (await store.GetExactAsync<RuntimeProfileResource>(address, token) is not null)
                throw new ControlPlaneConcurrencyException($"Runtime profile '{resource.Metadata.Name}' already exists in scope '{scopeRef}'.");
            return await store.PutExactAsync(scopeRef, resource with { ScopeRef = scopeRef, Generation = 1, Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded } }, null, true, token);
        }, cancellationToken);
    }

    public Task<StoredResource<RuntimeProfileResource>?> GetAsync(string name, CancellationToken cancellationToken) =>
        store.GetAsync<RuntimeProfileResource>(new ResourceKey(ResourceKinds.RuntimeProfile, name), cancellationToken);
    public Task<StoredResource<RuntimeProfileResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        store.GetAsync<RuntimeProfileResource>(new ResourceKey(ResourceKinds.RuntimeProfile, name, @namespace), cancellationToken);
    public Task<StoredResource<RuntimeProfileResource>?> GetExactAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        store.GetExactAsync<RuntimeProfileResource>(ScopedResourceAddress.Create(scopeRef, @namespace, ResourceKinds.RuntimeProfile, name), cancellationToken);

    public Task<IReadOnlyList<StoredResource<RuntimeProfileResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<RuntimeProfileResource>(ResourceKinds.RuntimeProfile, cancellationToken);

    public async Task<StoredResource<RuntimeProfileResource>> PutAsync(string name, RuntimeProfileProperties definition, string? ifMatch, CancellationToken cancellationToken)
        => await PutAsync(ResourceNamespace.Default, name, definition, ifMatch, cancellationToken);

    public async Task<StoredResource<RuntimeProfileResource>> PutAsync(ResourceNamespace @namespace, string name, RuntimeProfileProperties definition, string? ifMatch, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.RuntimeProfile, name, @namespace));
        var updated = existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Definition = definition,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        };
        Validate(updated);
        var scopeRef = existing.Value.ScopeRef ?? throw new RuntimeProfileValidationException("The runtime profile has no ownership scope.");
        return await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, updated, ifMatch, false, token), cancellationToken);
    }

    public async Task<IReadOnlyList<RuntimeProfileUsage>> GetUsagesAsync(string name, CancellationToken cancellationToken) =>
        await GetUsagesAsync(ResourceNamespace.Default, name, cancellationToken);

    public async Task<IReadOnlyList<RuntimeProfileUsage>> GetUsagesAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var usages = new List<RuntimeProfileUsage>();
        foreach (var deployment in await GetDeploymentsAsync(@namespace, name, cancellationToken))
        {
            if (!await HasOwningAgentAsync(deployment.Value, cancellationToken)) continue;
            usages.Add(new(deployment.Value.Uid, deployment.Value.Metadata.Name, deployment.Value.Environment, deployment.Value.AgentName ?? string.Empty));
        }
        return usages;
    }

    public Task DeleteAsync(string name, string? ifMatch, CancellationToken cancellationToken) =>
        DeleteAsync(ResourceNamespace.Default, name, ifMatch, cancellationToken);

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.RuntimeProfile, name, @namespace));
        await DeleteOrphanedDeploymentsAsync(@namespace, name, cancellationToken);
        var usages = await GetUsagesAsync(@namespace, name, cancellationToken);
        if (usages.Count > 0) throw new RuntimeProfileInUseException(name, usages);
        var scopeRef = existing.Value.ScopeRef ?? throw new RuntimeProfileValidationException("The runtime profile has no ownership scope.");
        await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await store.DeleteExactAsync(ScopedResourceAddress.Create(scopeRef, @namespace, ResourceKinds.RuntimeProfile, name), ifMatch, token);
            return true;
        }, cancellationToken);
    }

    private async Task DeleteOrphanedDeploymentsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        foreach (var deployment in await GetDeploymentsAsync(@namespace, name, cancellationToken))
        {
            if (await HasOwningAgentAsync(deployment.Value, cancellationToken)) continue;
            var stopped = deployment.Value with { DesiredState = DesiredAgentState.Stopped };
            var result = await reconciler.ReconcileAsync(stopped, cancellationToken);
            var stored = result.Changed
                ? await store.PutAsync(result.Deployment, deployment.ETag, false, cancellationToken)
                : deployment;
            await store.DeleteAsync(
                new ResourceKey(ResourceKinds.AgentDeployment, stored.Value.Metadata.Name, stored.Value.Namespace),
                stored.ETag,
                cancellationToken);
        }
    }

    private async Task<bool> HasOwningAgentAsync(AgentDeployment deployment, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deployment.AgentName)) return false;
        var agent = await store.GetAsync<AgentResource>(
            new ResourceKey(ResourceKinds.Agent, deployment.AgentName, deployment.AgentNamespace), cancellationToken);
        if (agent is null) return false;
        var revision = await store.GetAsync<AgentRevision>(
            new ResourceKey(ResourceKinds.AgentRevision, deployment.RevisionName, deployment.AgentNamespace), cancellationToken);
        return revision?.Value.AgentUid == agent.Value.Uid;
    }

    private async Task<IReadOnlyList<StoredResource<AgentDeployment>>> GetDeploymentsAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        (await store.ListAllAsync<AgentDeployment>(ResourceKinds.AgentDeployment, cancellationToken))
            .Where(value => value.Value.RuntimeProfileName == name && value.Value.RuntimeProfileNamespace == @namespace)
            .ToArray();

    private static void Validate(RuntimeProfileResource resource)
    {
        if (resource.Kind != ResourceKinds.RuntimeProfile) throw new RuntimeProfileValidationException($"Kind must be '{ResourceKinds.RuntimeProfile}'.");
        if (resource.ApiVersion != ManagementApiVersions.CoreV1) throw new RuntimeProfileValidationException($"ApiVersion must be '{ManagementApiVersions.CoreV1}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.RuntimeType);
        foreach (var option in resource.Definition.RuntimeOptions.Keys)
        {
            if (!string.Equals(option, resource.Definition.RuntimeType, StringComparison.OrdinalIgnoreCase)
                && !(string.Equals(resource.Definition.RuntimeType, "microsoft-agent-framework", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(option, "microsoftAgentFramework", StringComparison.OrdinalIgnoreCase)))
                throw new RuntimeProfileValidationException($"Runtime options for '{option}' cannot be used with runtime '{resource.Definition.RuntimeType}'.");
        }
    }
}
