using System.Runtime.CompilerServices;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Knowledge;

public sealed class KnowledgeSystemGraphContributor(IResourceStore resources, IResourceIdentityCatalog identities) : ISystemGraphContributor
{
    public string Family => "knowledge";
    public SystemGraphContributorCoverage Coverage => new(Family, ["source-composition-input-pins", "snapshot-input-output-storage-generations", "snapshot-storage-type-connected-service-pins"], ["historical-flow-bindings-without-uids", "proposal-refresh-unavailable"]);
    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var stored in ResourceSystemGraph.ReadAsync<KnowledgeSourceResource>(resources, scope, KnowledgeResourceKinds.KnowledgeSource, ct))
        {
            var value = stored.Value;
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Node(value), identities);
            if (value.Definition.Output is { } output) await builder.PinAsync(output.Storage.StorageUid, "definition.output.storage", ct, output.Storage.Generation);
            if (value.Composition is { } composition)
                foreach (var input in composition.DataSources)
                {
                    await builder.PinAsync(input.DataSourceUid, $"composition.dataSources[{input.BindingName}]", ct, input.DataSourceGeneration);
                    if (input.ProfileUid is { } profileUid)
                        await builder.PinAsync(profileUid, $"composition.dataSources[{input.BindingName}].profile", ct,
                            version: input.ProfileVersion, generation: input.ProfileGeneration);
                }
            if (value.Definition.Orchestration is { VersionStrategy: Agentstration.Flows.FlowCallVersionStrategy.Exact, ResourceUid: { } uid, Version: { } version }) await builder.PinAsync(uid, "definition.orchestration", ct, version: version);
            foreach (var call in value.Definition.Transformations.Concat(value.Definition.Compositions.Values.SelectMany(value => value)))
            {
                if (call.Flow is { VersionStrategy: Agentstration.Flows.FlowCallVersionStrategy.Exact, ResourceUid: { } flowUid, Version: { } callVersion }) await builder.PinAsync(flowUid, "definition.compositions.flow", ct, version: callVersion);
                if (call.Function is { Uid: { } functionUid } function) await builder.PinAsync(functionUid, "definition.compositions.function", ct, version: function.Version);
            }
            yield return builder.Build();
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<KnowledgeSnapshotResource>(resources, scope, KnowledgeResourceKinds.KnowledgeSnapshot, ct))
        {
            var value = stored.Value;
            var node = ResourceSystemGraph.Publication(value, value.KnowledgeSourceUid, KnowledgeResourceKinds.KnowledgeSource,
                value.KnowledgeSourceName, value.Name, value.RequestHash, ownerScope: value.KnowledgeSourceScopeRef, generation: value.KnowledgeSourceGeneration, publishedAt: value.PublishedAt)
                with
            {
                Identity = new(value.KnowledgeSourceUid, value.KnowledgeSourceScopeRef ?? scope, value.KnowledgeSourceNamespace,
                    KnowledgeResourceKinds.KnowledgeSource, value.KnowledgeSourceName)
            };
            var builder = new SystemGraphContributionBuilder(node, identities);
            builder.PublicationOf(value.KnowledgeSourceUid);
            foreach (var input in value.ProjectionInputs)
            {
                await builder.PinAsync(input.DataSourceUid, $"projectionInputs[{input.BindingName}].source", ct, input.DataSourceGeneration);
                if (input.StorageInput is { } storage) await builder.PinAsync(storage.Storage.StorageUid, $"projectionInputs[{input.BindingName}].storage", ct, storage.Storage.Generation,
                    expectedHash: storage.Sha256, bindingHash: storage.Storage.BindingDigest, selectedPublication: storage.Version);
                // Acquisition/run evidence is operational provenance, not an executable adoption edge.
            }
            foreach (var artifact in value.Artifacts)
            {
                if (artifact.Content is { } content)
                {
                    await builder.PinAsync(content.Target.Storage.StorageUid, "artifacts.storage", ct, content.Target.Storage.Generation,
                        targetField: content.Document is { } document ? $"collections[{document.Publication.Target.CollectionUid:D}]" : null,
                        expectedHash: content.Sha256, bindingHash: content.Target.Storage.BindingDigest,
                        capabilityHash: content.Binding?.CapabilityDigest, selectedPublication: content.Version);
                    if (content.Binding is { } binding)
                    {
                        await builder.PinAsync(binding.Type.TypeUid, "artifacts.binding.storageType", ct, version: binding.Type.Version, expectedHash: binding.Type.ContractDigest,
                            bindingHash: binding.ManualMappingDigest);
                        if (binding.ConnectedService is { } service) await builder.PinAsync(service.ServiceUid, "artifacts.binding.connectedService", ct, service.Generation);
                        if (binding.ConnectedServiceType is { } type) await builder.PinAsync(type.TypeUid, "artifacts.binding.connectedServiceType", ct, version: type.Version);
                    }
                }
            }
            yield return builder.Build();
        }
    }
}
