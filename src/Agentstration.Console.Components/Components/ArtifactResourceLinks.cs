using Agentstration.Artifacts.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Components;

internal static class ArtifactResourceLinks
{
    public static string Storage(ArtifactStorageMaterialization materialization)
    {
        var storage = materialization.Target.Storage.Storage;
        return $"/storages/{Uri.EscapeDataString(storage.Name)}?scopeRef={Uri.EscapeDataString(storage.ScopeRef.Value)}&namespace={Uri.EscapeDataString(storage.Namespace.Value)}&context={Uri.EscapeDataString(materialization.Target.Consumer.ConsumerScopeRef.Value)}";
    }

    public static string? Producer(ScopedResourceAddress resource) => resource.Kind switch
    {
        "DataSource" => Link("data-sources", resource, "acquisitions"),
        "KnowledgeSource" => Link("knowledge-sources", resource, "projections"),
        _ => null
    };

    private static string Link(string route, ScopedResourceAddress resource, string view) =>
        $"/{route}/{Uri.EscapeDataString(resource.Name)}?scopeRef={Uri.EscapeDataString(resource.ScopeRef.Value)}&namespace={Uri.EscapeDataString(resource.Namespace.Value)}&view={view}";
}
