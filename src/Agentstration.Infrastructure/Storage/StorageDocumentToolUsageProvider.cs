using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Storage;

/// <summary>Published document relays retain the same dependency protection as other exact Storage consumers.</summary>
public sealed class StorageDocumentToolUsageProvider(IResourceStore resources, IRequestContextScopeFactory scopes) : IStorageUsageProvider
{
    public async Task<IReadOnlyList<StorageUsage>> GetUsagesAsync(ScopedResourceAddress storage, CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        var definitions = await resources.ListAllAsync<ToolDefinitionResource>(ToolResourceKinds.ToolDefinition, cancellationToken);
        var versions = await resources.ListAllAsync<ToolDefinitionVersionResource>(ToolResourceKinds.ToolDefinitionVersion, cancellationToken);
        return definitions.Where(d => Uses(d.Value.Definition, storage)).Select(d => Usage(d.Value, d.Value.Name))
            .Concat(versions.Where(v => Uses(v.Value.Definition, storage)).Select(v => Usage(v.Value, v.Value.DefinitionName)))
            .Take(100).ToArray();
    }

    private static bool Uses(ToolDefinitionProperties definition, ScopedResourceAddress storage)
    {
        if (definition is not RelayToolDefinitionProperties relay
            || !new[] { "get", "put", "delete", "query" }.Any(operation =>
                relay.Target.Name == AgentstrationToolProvider.ToolResourceName("storage.documents." + operation))) return false;
        var binding = relay.Bindings.OfType<LiteralToolArgumentBinding>().SingleOrDefault(b => b.Path == "/target");
        if (binding is null || binding.Value.ValueKind != JsonValueKind.Object
            || !binding.Value.TryGetProperty("storage", out var reference)) return false;
        try { return reference.Deserialize<StorageRevisionReference>(StorageDocumentMcpTool.Json)?.Storage == storage; }
        catch (JsonException) { return false; }
    }
    private static StorageUsage Usage(Resource resource, string definitionName) => new(resource.Kind, resource.Name, definitionName,
        $"/tools/definitions/{Uri.EscapeDataString(definitionName)}?scopeRef={Uri.EscapeDataString(resource.ScopeRef!.Value.Value)}&namespace={Uri.EscapeDataString(resource.Namespace.Value)}");
}
