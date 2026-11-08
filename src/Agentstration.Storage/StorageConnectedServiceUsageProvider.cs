using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Storage;

public sealed class StorageConnectedServiceUsageProvider(IResourceStore store, IRequestContextScopeFactory requestScopes) : IConnectedServiceUsageProvider
{
    public async Task<IReadOnlyList<ConnectedServiceUsage>> GetUsagesAsync(
        ScopedResourceAddress connectedService, CancellationToken cancellationToken = default)
    {
        using var system = requestScopes.PushSystem();
        var resources = await store.ListAllAsync<StorageResource>(StorageResourceKinds.Storage, cancellationToken);
        return resources.Where(item => item.Value.Definition.ConnectedService?.Service is { } reference
            && reference.ScopeRef == connectedService.ScopeRef
            && reference.Address == connectedService.Address)
            .Select(item => new ConnectedServiceUsage(StorageResourceKinds.Storage, item.Value.Name,
                item.Value.Definition.DisplayName, $"/api/storages/{Uri.EscapeDataString(item.Value.Name)}?scopeRef={Uri.EscapeDataString(item.Value.ScopeRef!.Value.Value)}&namespace={Uri.EscapeDataString(item.Value.Namespace.Value)}"))
            .ToArray();
    }
}
