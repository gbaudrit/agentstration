using System.Runtime.CompilerServices;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Storage;

public sealed class StorageSystemGraphContributor(IResourceStore resources, IResourceIdentityCatalog identities) : ISystemGraphContributor
{
    public string Family => "storage";
    public SystemGraphContributorCoverage Coverage => new(Family, ["type-contract-digests", "provider-contribution-generations", "connected-service-pins", "collection-schema-pins"], ["physical-content-never-inspected"]);
    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var stored in ResourceSystemGraph.ReadAsync<StorageTypeResource>(resources, scope, StorageResourceKinds.StorageType, ct))
        {
            var value = stored.Value;
            yield return new([ResourceSystemGraph.Node(value)], []);
            foreach (var version in value.Definition.Versions)
            {
                var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Publication(value, value.Uid, value.Kind, value.Name,
                    version.Version, contractHash: version.ContractDigest, hasNativeUid: false), identities);
                builder.PublicationOf(value.Uid);
                if (version.Contribution is { } contribution) await builder.PinAsync(contribution.ProviderUid, "versions.contribution.provider", ct, contribution.ProviderGeneration);
                yield return builder.Build();
            }
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<StorageResource>(resources, scope, StorageResourceKinds.Storage, ct))
        {
            var value = stored.Value;
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Node(value), identities);
            await builder.PinAsync(value.Definition.Type.TypeUid, "definition.type", ct, version: value.Definition.Type.Version);
            if (value.Definition.ConnectedService is { } service) await builder.PinAsync(service.ServiceUid, "definition.connectedService", ct, service.Generation);
            if (value.Definition.ConnectedServiceType is { } type) await builder.PinAsync(type.TypeUid, "definition.connectedServiceType", ct, version: type.Version);
            foreach (var collection in value.Definition.Collections)
                if (collection.SchemaSnapshot is { SchemaUid: { } uid, Source: { } schema }) await builder.PinAsync(uid, $"definition.collections[{collection.Uid:D}].schema", ct, version: schema.Version,
                        targetField: schema.SchemaPath, expectedHash: collection.SchemaSnapshot.DefinitionHash, parentHash: collection.SchemaSnapshot.ParentDefinitionHash);
            yield return builder.Build();
        }
    }
}
