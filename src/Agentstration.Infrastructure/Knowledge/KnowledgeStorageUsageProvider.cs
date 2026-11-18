using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Knowledge;

public sealed class KnowledgeStorageUsageProvider(IResourceStore resources, IRequestContextScopeFactory scopes) : IStorageUsageProvider
{
    public async Task<IReadOnlyList<StorageUsage>> GetUsagesAsync(ScopedResourceAddress storage, CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        var sources = await resources.ListAllAsync<KnowledgeSourceResource>(KnowledgeResourceKinds.KnowledgeSource, cancellationToken);
        var snapshots = await resources.ListAllAsync<KnowledgeSnapshotResource>(KnowledgeResourceKinds.KnowledgeSnapshot, cancellationToken);
        return sources.Where(item => item.Value.Definition.Output?.Storage.Storage == storage)
            .Select(item => new StorageUsage(item.Value.Kind, item.Value.Name, item.Value.Definition.DisplayName,
                Link(item.Value.Name, item.Value.Namespace, item.Value.ScopeRef!.Value, item.Value.ScopeRef!.Value)))
            .Concat(snapshots.Where(item => item.Value.Artifacts.Any(value => value.Content?.Target.Storage.Storage == storage))
                .Select(item => new StorageUsage(item.Value.Kind, item.Value.Name, item.Value.Name,
                    Link(item.Value.KnowledgeSourceName, item.Value.KnowledgeSourceNamespace,
                        item.Value.KnowledgeSourceScopeRef ?? item.Value.ScopeRef!.Value, item.Value.ScopeRef!.Value) + "&view=snapshots")))
            .Take(100).ToArray();
    }

    private static string Link(string name, ResourceNamespace ns, ResourceScopeRef owner, ResourceScopeRef context) =>
        $"/knowledge-sources/{Uri.EscapeDataString(name)}?namespace={Uri.EscapeDataString(ns.Value)}&scopeRef={Uri.EscapeDataString(owner.Value)}&context={Uri.EscapeDataString(context.Value)}";
}
