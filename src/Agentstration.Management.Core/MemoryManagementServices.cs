using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Core;

public sealed record MemoryResourceUsage(string Kind, string Name, string DisplayName);

public sealed class MemoryManagementException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class MemoryProviderManagementService(IControlPlaneStore store)
{
    public async Task<StoredResource<MemoryProviderResource>> CreateAsync(MemoryProviderResource resource, CancellationToken cancellationToken)
    {
        ValidateIdentity(resource, ResourceKinds.MemoryProvider);
        if (await GetAsync(resource.Namespace, resource.Name, cancellationToken) is not null)
            throw new ControlPlaneConcurrencyException($"Memory provider '{resource.Address}' already exists.");
        var definition = await ValidateAsync(resource.Namespace, resource.Definition, null, cancellationToken);
        return await store.PutAsync(resource with
        {
            Generation = 1,
            Definition = definition,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, null, true, cancellationToken);
    }

    public Task<StoredResource<MemoryProviderResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        store.GetAsync<MemoryProviderResource>(new(ResourceKinds.MemoryProvider, name, @namespace), cancellationToken);

    public async Task<IReadOnlyList<StoredResource<MemoryProviderResource>>> ListAsync(CancellationToken cancellationToken) =>
        await store.ListAllAsync<MemoryProviderResource>(ResourceKinds.MemoryProvider, cancellationToken);

    public async Task<StoredResource<MemoryProviderResource>> PutAsync(ResourceNamespace @namespace, string name, MemoryProviderProperties definition, string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new MemoryManagementException("memory_provider_not_found", $"Memory provider '{@namespace}/{name}' was not found.");
        var normalized = await ValidateAsync(@namespace, definition, existing.Value, cancellationToken);
        if (normalized.IntegrationKind != existing.Value.Definition.IntegrationKind
            || normalized.Builtin != existing.Value.Definition.Builtin
            || normalized.Aep != existing.Value.Definition.Aep)
            throw new MemoryManagementException("memory_provider_binding_immutable", "A Memory provider integration binding cannot be changed after creation.");
        return await store.PutAsync(existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Definition = normalized
        }, etag, false, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryResourceUsage>> GetUsagesAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        (await store.ListAllAsync<MemoryProfileResource>(ResourceKinds.MemoryProfile, cancellationToken))
            .Where(profile => profile.Value.Definition.Provider.Resolve(profile.Value.Namespace, ResourceKinds.MemoryProvider) is var address
                && address.Namespace == @namespace && address.Name == name)
            .Select(profile => new MemoryResourceUsage(profile.Value.Kind, profile.Value.Name, profile.Value.Definition.DisplayName))
            .ToArray();

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, string? etag, CancellationToken cancellationToken)
    {
        _ = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new MemoryManagementException("memory_provider_not_found", $"Memory provider '{@namespace}/{name}' was not found.");
        if ((await GetUsagesAsync(@namespace, name, cancellationToken)).Count > 0)
            throw new MemoryManagementException("memory_provider_in_use", $"Memory provider '{@namespace}/{name}' is referenced by a Memory profile.");
        await store.DeleteAsync(new(ResourceKinds.MemoryProvider, name, @namespace), etag, cancellationToken);
    }

    private async Task<MemoryProviderProperties> ValidateAsync(ResourceNamespace @namespace, MemoryProviderProperties value, MemoryProviderResource? existing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.DisplayName);
        if (value.IntegrationKind == MemoryProviderIntegrationKind.Builtin)
        {
            if (value.Aep is not null || !string.Equals(value.Builtin?.Adapter, "sqlite", StringComparison.OrdinalIgnoreCase))
                throw new MemoryManagementException("memory_provider_invalid", "A builtin Memory provider must select the sqlite adapter and cannot define AEP settings.");
            var otherBuiltin = (await store.ListAllAsync<MemoryProviderResource>(ResourceKinds.MemoryProvider, cancellationToken))
                .Any(item => item.Value.Namespace == @namespace && item.Value.Definition.IntegrationKind == MemoryProviderIntegrationKind.Builtin
                    && item.Value.Address != existing?.Address);
            if (otherBuiltin) throw new MemoryManagementException("builtin_memory_provider_exists", "Only one builtin SQLite Memory provider is supported per namespace.");
            return value with { DisplayName = value.DisplayName.Trim(), Builtin = new() };
        }
        if (value.Builtin is not null || value.Aep is null || string.IsNullOrWhiteSpace(value.Aep.ExtensionId) || string.IsNullOrWhiteSpace(value.Aep.ProviderId))
            throw new MemoryManagementException("memory_provider_invalid", "An AEP Memory provider requires extensionId and providerId and cannot define builtin settings.");
        return value with
        {
            DisplayName = value.DisplayName.Trim(),
            Aep = value.Aep with { ExtensionId = value.Aep.ExtensionId.Trim(), ProviderId = value.Aep.ProviderId.Trim() }
        };
    }

    private static void ValidateIdentity(Resource resource, string kind)
    {
        if (resource.Kind != kind || resource.ApiVersion != ManagementApiVersions.CoreV1)
            throw new MemoryManagementException("memory_resource_invalid", $"Resource must use kind '{kind}' and apiVersion '{ManagementApiVersions.CoreV1}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Name);
    }
}

public sealed class MemoryProfileManagementService(IControlPlaneStore store, MemoryProviderManagementService providers)
{
    public async Task<StoredResource<MemoryProfileResource>> CreateAsync(MemoryProfileResource resource, CancellationToken cancellationToken)
    {
        if (resource.Kind != ResourceKinds.MemoryProfile || resource.ApiVersion != ManagementApiVersions.CoreV1)
            throw new MemoryManagementException("memory_profile_invalid", "Invalid Memory profile resource identity.");
        var definition = await ValidateAsync(resource.Namespace, resource.Definition, cancellationToken);
        return await store.PutAsync(resource with
        {
            Generation = 1,
            Definition = definition,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, null, true, cancellationToken);
    }

    public Task<StoredResource<MemoryProfileResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        store.GetAsync<MemoryProfileResource>(new(ResourceKinds.MemoryProfile, name, @namespace), cancellationToken);

    public async Task<IReadOnlyList<StoredResource<MemoryProfileResource>>> ListAsync(CancellationToken cancellationToken) =>
        await store.ListAllAsync<MemoryProfileResource>(ResourceKinds.MemoryProfile, cancellationToken);

    public async Task<StoredResource<MemoryProfileResource>> PutAsync(ResourceNamespace @namespace, string name, MemoryProfileProperties definition, string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new MemoryManagementException("memory_profile_not_found", $"Memory profile '{@namespace}/{name}' was not found.");
        return await store.PutAsync(existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Definition = await ValidateAsync(@namespace, definition, cancellationToken)
        }, etag, false, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryResourceUsage>> GetUsagesAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        (await store.ListAllAsync<AgentResource>(ResourceKinds.Agent, cancellationToken))
            .Where(agent =>
            {
                if (agent.Value.Definition.Memory is not { } memory) return false;
                var address = memory.Profile.Resolve(agent.Value.Namespace, ResourceKinds.MemoryProfile);
                return address.Namespace == @namespace && address.Name == name;
            })
            .Select(agent => new MemoryResourceUsage(agent.Value.Kind, agent.Value.Name, agent.Value.Definition.DisplayName))
            .ToArray();

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, string? etag, CancellationToken cancellationToken)
    {
        _ = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new MemoryManagementException("memory_profile_not_found", $"Memory profile '{@namespace}/{name}' was not found.");
        if ((await GetUsagesAsync(@namespace, name, cancellationToken)).Count > 0)
            throw new MemoryManagementException("memory_profile_in_use", $"Memory profile '{@namespace}/{name}' is referenced by an Agent.");
        await store.DeleteAsync(new(ResourceKinds.MemoryProfile, name, @namespace), etag, cancellationToken);
    }

    private async Task<MemoryProfileProperties> ValidateAsync(ResourceNamespace @namespace, MemoryProfileProperties value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.DisplayName);
        if (value.Provider.WorkspaceRef is not null)
            throw new MemoryManagementException("cross_workspace_memory_provider", "Cross-workspace Memory provider references are not supported.");
        var address = value.Provider.Resolve(@namespace, ResourceKinds.MemoryProvider);
        _ = await providers.GetAsync(address.Namespace, address.Name, cancellationToken)
            ?? throw new MemoryManagementException("memory_provider_not_found", $"Memory provider '{address}' was not found.");
        if (value.Retrieval.Strategy != MemoryRetrievalStrategy.Recent || value.Retrieval.MaximumRecords is < 1 or > 20)
            throw new MemoryManagementException("memory_retrieval_invalid", "V1 supports recent retrieval with maximumRecords between 1 and 20.");
        if (value.Retention.DefaultTimeToLive is { } ttl && ttl <= TimeSpan.Zero)
            throw new MemoryManagementException("memory_retention_invalid", "DefaultTimeToLive must be positive.");
        return value with { DisplayName = value.DisplayName.Trim(), Description = value.Description?.Trim() };
    }
}

public static class MemoryManagementServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationMemoryManagement(this IServiceCollection services)
    {
        services.AddSingleton<MemoryProviderManagementService>();
        services.AddSingleton<MemoryProfileManagementService>();
        return services;
    }
}
