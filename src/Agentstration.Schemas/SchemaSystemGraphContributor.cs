using System.Runtime.CompilerServices;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Schemas;

public sealed class SchemaSystemGraphContributor(IResourceStore resources) : ISystemGraphContributor
{
    public string Family => "schemas";
    public SystemGraphContributorCoverage Coverage => new(Family, ["schema-publications-and-digests"], ["inline-schema-has-no-independent-identity"]);
    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var stored in ResourceSystemGraph.ReadAsync<SchemaResource>(resources, scope, SchemaResourceKinds.Schema, ct))
            yield return new([ResourceSystemGraph.Node(stored.Value)], []);
        await foreach (var stored in ResourceSystemGraph.ReadAsync<SchemaVersionResource>(resources, scope, SchemaResourceKinds.SchemaVersion, ct))
        {
            var value = stored.Value;
            var node = ResourceSystemGraph.Publication(value, value.SchemaUid, SchemaResourceKinds.Schema, value.SchemaName, value.Version, value.DefinitionHash, generation: value.SchemaGeneration, publishedAt: value.PublishedAt);
            yield return new([node], [new(SystemGraphIds.Definition(value.SchemaUid), node.Id, "publication")]);
        }
    }
}
