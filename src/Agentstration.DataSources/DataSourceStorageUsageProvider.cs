using Agentstration.DataSources.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.DataSources;

public sealed class DataSourceStorageUsageProvider(IResourceStore resources) : IStorageUsageProvider
{
    public async Task<IReadOnlyList<StorageUsage>> GetUsagesAsync(ScopedResourceAddress storage, CancellationToken cancellationToken = default) =>
        (await resources.ListAllAsync<DataSourceResource>(DataSourceResourceKinds.DataSource, cancellationToken))
            .Where(item => item.Value.Definition.Dataset?.Storage?.Storage == storage)
            .Select(item => new StorageUsage(item.Value.Kind, item.Value.Name, item.Value.Definition.DisplayName,
                $"/data-sources/{Uri.EscapeDataString(item.Value.Name)}?scopeRef={Uri.EscapeDataString(item.Value.ScopeRef!.Value.Value)}&namespace={Uri.EscapeDataString(item.Value.Namespace.Value)}"))
            .ToArray();
}
