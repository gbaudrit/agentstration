using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class RuntimeProfileValidationException(string message) : Exception(message);
public sealed class RuntimeProfileInUseException(string profileName, IReadOnlyList<RuntimeProfileUsage> usages)
    : Exception($"The runtime profile '{profileName}' is used by {usages.Count} deployment(s).")
{
    public IReadOnlyList<RuntimeProfileUsage> Usages { get; } = usages;
}
public sealed record RuntimeProfileUsage(string ResourceId, string Name, string Environment, string AgentResourceId);

public sealed class RuntimeProfileManagementService(IControlPlaneStore store)
{
    public async Task<StoredResource<RuntimeProfileResource>> CreateAsync(
        RuntimeProfileResource resource,
        CancellationToken cancellationToken)
    {
        Validate(resource);
        if (await store.GetAsync<RuntimeProfileResource>(resource.Id, cancellationToken) is not null)
            throw new ControlPlaneConcurrencyException($"Runtime profile '{resource.Name}' already exists.");
        return await store.PutAsync(resource with
        {
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, null, true, cancellationToken);
    }

    public Task<StoredResource<RuntimeProfileResource>?> GetAsync(string resourceGroup, string name, CancellationToken cancellationToken) =>
        store.GetAsync<RuntimeProfileResource>(ProfileId(resourceGroup, name), cancellationToken);

    public Task<IReadOnlyList<StoredResource<RuntimeProfileResource>>> ListAsync(string? resourceGroup, CancellationToken cancellationToken) =>
        store.ListAsync<RuntimeProfileResource>(AgentstrationResourceTypes.RuntimeProfiles, resourceGroup, 0, 1000, cancellationToken);

    public async Task<StoredResource<RuntimeProfileResource>> PutAsync(
        string resourceGroup,
        string name,
        RuntimeProfileProperties properties,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var id = ProfileId(resourceGroup, name);
        var existing = await store.GetAsync<RuntimeProfileResource>(id, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(id);
        var updated = existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Properties = properties,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        };
        Validate(updated);
        return await store.PutAsync(updated, ifMatch, false, cancellationToken);
    }

    public async Task<IReadOnlyList<RuntimeProfileUsage>> GetUsagesAsync(string profileResourceId, CancellationToken cancellationToken)
    {
        var deployments = await store.ListAsync<AgentDeployment>(AgentstrationResourceTypes.Deployments, null, 0, 1000, cancellationToken);
        return deployments
            .Where(value => string.Equals(value.Value.RuntimeProfileId, profileResourceId, StringComparison.Ordinal))
            .Select(value => new RuntimeProfileUsage(
                value.Value.Id,
                value.Value.Name,
                value.Value.Environment,
                value.Value.AgentResourceId ?? string.Empty))
            .ToArray();
    }

    public async Task DeleteAsync(string resourceGroup, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        var id = ProfileId(resourceGroup, name);
        var existing = await store.GetAsync<RuntimeProfileResource>(id, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(id);
        var usages = await GetUsagesAsync(id, cancellationToken);
        if (usages.Count > 0) throw new RuntimeProfileInUseException(existing.Value.Name, usages);
        await store.DeleteAsync(id, ifMatch, cancellationToken);
    }

    public static string ProfileId(string resourceGroup, string name) =>
        ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Runtime, "runtimeProfiles", name).Value;

    private static void Validate(RuntimeProfileResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!string.Equals(resource.Type, AgentstrationResourceTypes.RuntimeProfiles, StringComparison.Ordinal))
            throw new RuntimeProfileValidationException($"Type must be '{AgentstrationResourceTypes.RuntimeProfiles}'.");
        if (!string.Equals(resource.ApiVersion, ManagementApiVersions.V20260801, StringComparison.Ordinal))
            throw new RuntimeProfileValidationException($"ApiVersion must be '{ManagementApiVersions.V20260801}'.");
        if (string.IsNullOrWhiteSpace(resource.ResourceGroup)
            || !string.Equals(resource.Id, ProfileId(resource.ResourceGroup, resource.Name), StringComparison.Ordinal))
            throw new RuntimeProfileValidationException("The runtime profile resource identity is invalid.");
        if (string.IsNullOrWhiteSpace(resource.Properties.DisplayName))
            throw new RuntimeProfileValidationException("Runtime profile displayName is required.");
        if (string.IsNullOrWhiteSpace(resource.Properties.RuntimeType))
            throw new RuntimeProfileValidationException("Runtime profile runtimeType is required.");
        foreach (var option in resource.Properties.RuntimeOptions.Keys)
        {
            if (!string.Equals(option, resource.Properties.RuntimeType, StringComparison.OrdinalIgnoreCase)
                && !(string.Equals(resource.Properties.RuntimeType, "microsoft-agent-framework", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(option, "microsoftAgentFramework", StringComparison.OrdinalIgnoreCase)))
                throw new RuntimeProfileValidationException($"Runtime options for '{option}' cannot be used with runtime '{resource.Properties.RuntimeType}'.");
        }
    }
}
