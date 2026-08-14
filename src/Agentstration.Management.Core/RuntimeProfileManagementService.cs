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

public sealed class RuntimeProfileManagementService(IControlPlaneStore store)
{
    public static string ProfileId(string name) => name;
    public async Task<StoredResource<RuntimeProfileResource>> CreateAsync(RuntimeProfileResource resource, CancellationToken cancellationToken)
    {
        Validate(resource);
        if (await GetAsync(resource.Namespace, resource.Metadata.Name, cancellationToken) is not null)
            throw new ControlPlaneConcurrencyException($"Runtime profile '{resource.Metadata.Name}' already exists.");
        return await store.PutAsync(resource with { Generation = 1, Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded } }, null, true, cancellationToken);
    }

    public Task<StoredResource<RuntimeProfileResource>?> GetAsync(string name, CancellationToken cancellationToken) =>
        store.GetAsync<RuntimeProfileResource>(new ResourceKey(ResourceKinds.RuntimeProfile, name), cancellationToken);
    public Task<StoredResource<RuntimeProfileResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        store.GetAsync<RuntimeProfileResource>(new ResourceKey(ResourceKinds.RuntimeProfile, name, @namespace), cancellationToken);

    public Task<IReadOnlyList<StoredResource<RuntimeProfileResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<RuntimeProfileResource>(ResourceKinds.RuntimeProfile, cancellationToken);

    public async Task<StoredResource<RuntimeProfileResource>> PutAsync(string name, RuntimeProfileProperties definition, string? ifMatch, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(name, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.RuntimeProfile, name));
        var updated = existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Definition = definition,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        };
        Validate(updated);
        return await store.PutAsync(updated, ifMatch, false, cancellationToken);
    }

    public async Task<IReadOnlyList<RuntimeProfileUsage>> GetUsagesAsync(string name, CancellationToken cancellationToken) =>
        (await store.ListAllAsync<AgentDeployment>(ResourceKinds.AgentDeployment, cancellationToken))
            .Where(value => value.Value.RuntimeProfileName == name)
            .Select(value => new RuntimeProfileUsage(value.Value.Uid, value.Value.Metadata.Name, value.Value.Environment, value.Value.AgentName ?? string.Empty))
            .ToArray();

    public async Task DeleteAsync(string name, string? ifMatch, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(name, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.RuntimeProfile, name));
        var usages = await GetUsagesAsync(name, cancellationToken);
        if (usages.Count > 0) throw new RuntimeProfileInUseException(existing.Value.Metadata.Name, usages);
        await store.DeleteAsync(new(ResourceKinds.RuntimeProfile, name), ifMatch, cancellationToken);
    }

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        _ = await GetAsync(@namespace, name, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.RuntimeProfile, name, @namespace));
        await store.DeleteAsync(new(ResourceKinds.RuntimeProfile, name, @namespace), ifMatch, cancellationToken);
    }

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
