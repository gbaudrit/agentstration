using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Tools;

namespace Agentstration.DataSources;

public sealed class DataSourceSystemGraphContributor(IResourceStore resources, IResourceIdentityCatalog identities,
    DataSourceQueryService queries) : ISystemGraphContributor
{
    public string Family => "data-sources-and-queries";
    public SystemGraphContributorCoverage Coverage => new(Family, ["profile-tool-provider-generations", "dataset-operation-storage-pins", "query-publications-contract-digests", "query-latest-published-at-selection", "composition-flow-function-uids"], ["historical-profile-flows-without-uids", "operational-acquisitions-not-publication-closure"]);

    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var stored in ResourceSystemGraph.ReadAsync<DataSourceProfileResource>(resources, scope, DataSourceResourceKinds.DataSourceProfile, ct))
            yield return new([ResourceSystemGraph.Node(stored.Value)], []);
        await foreach (var stored in ResourceSystemGraph.ReadAsync<DataSourceProfileRevisionResource>(resources, scope, DataSourceResourceKinds.DataSourceProfileRevision, ct))
        {
            var value = stored.Value;
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Publication(value, value.ProfileUid,
                DataSourceResourceKinds.DataSourceProfile, value.ProfileName, value.Version, value.DefinitionHash, value.DependencyHash, generation: value.ProfileGeneration, publishedAt: value.PublishedAt), identities);
            builder.PublicationOf(value.ProfileUid);
            foreach (var tool in value.ToolBindings.Concat(value.DiscoveryToolBindings))
            {
                await builder.PinAsync(tool.ToolUid, $"toolBindings[{tool.BindingName}].tool", ct, tool.ToolGeneration);
                await builder.PinAsync(tool.ProviderUid, $"toolBindings[{tool.BindingName}].provider", ct, tool.ProviderGeneration);
            }
            yield return builder.Build();
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<DataSourceResource>(resources, scope, DataSourceResourceKinds.DataSource, ct))
        {
            var value = stored.Value;
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Node(value), identities);
            if (value.ProfileUid is { } profile) await builder.PinAsync(profile, "definition.profile", ct, kind: "dynamic-dependency", resolution: "family-selected-profile");
            if (value.Definition.Dataset is { } dataset)
            {
                await builder.PinAsync(dataset.Acquisition.Operation.OperationUid, "definition.dataset.acquisition.operation", ct, dataset.Acquisition.Operation.Generation);
                if (dataset.Storage is { } storage) await builder.PinAsync(storage.StorageUid, "definition.dataset.storage", ct, storage.Generation);
                if (dataset.OutputSchemaSnapshot is { SchemaUid: { } schemaUid, Source: { } schema })
                    await builder.PinAsync(schemaUid, "definition.dataset.outputSchema", ct, version: schema.Version, targetField: schema.SchemaPath,
                        expectedHash: dataset.OutputSchemaSnapshot.DefinitionHash, parentHash: dataset.OutputSchemaSnapshot.ParentDefinitionHash);
                if (dataset.Orchestration is { VersionStrategy: Agentstration.Flows.FlowCallVersionStrategy.Exact, ResourceUid: { } flowUid, Version: { } version })
                    await builder.PinAsync(flowUid, "definition.dataset.orchestration", ct, version: version);
                foreach (var call in dataset.Transformations.Concat(dataset.Compositions.Values.SelectMany(value => value)))
                {
                    if (call.Flow is { VersionStrategy: Agentstration.Flows.FlowCallVersionStrategy.Exact, ResourceUid: { } uid, Version: { } callVersion }) await builder.PinAsync(uid, "definition.dataset.compositions.flow", ct, version: callVersion);
                    if (call.Function is { Uid: { } functionUid } function) await builder.PinAsync(functionUid, "definition.dataset.compositions.function", ct, version: function.Version);
                }
            }
            yield return builder.Build();
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<DataSourceQueryResource>(resources, scope, DataSourceResourceKinds.DataSourceQuery, ct))
        {
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Node(stored.Value), identities);
            await builder.PinAsync(stored.Value.Definition.Selection.SourceUid, "definition.selection.source", ct, kind: "dynamic-dependency", resolution: "source-identity");
            yield return builder.Build();
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<DataSourceQueryVersionResource>(resources, scope, DataSourceResourceKinds.DataSourceQueryVersion, ct))
        {
            var value = stored.Value;
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Publication(value, value.QueryUid,
                DataSourceResourceKinds.DataSourceQuery, value.QueryName, value.Version, value.DefinitionDigest, value.ContractDigest, generation: value.QueryGeneration, publishedAt: value.PublishedAt), identities);
            builder.PublicationOf(value.QueryUid);
            await builder.PinAsync(value.Definition.Selection.SourceUid, "definition.selection.source", ct, kind: "dynamic-dependency", resolution: value.Definition.Selection.Mode);
            if (value.Definition.Selection.Contract is { SchemaUid: { } uid, SchemaReference: { } reference })
                await builder.PinAsync(uid, "definition.selection.contract.schema", ct, version: reference.Version, targetField: reference.SchemaPath,
                    expectedHash: value.Definition.Selection.Contract.DefinitionHash, parentHash: value.Definition.Selection.Contract.ParentDefinitionHash);
            yield return builder.Build();
        }
        // Query Tool bindings are a Data Sources-owned typed encoding, shared with its existing reader.
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ToolDefinitionVersionResource>(resources, scope, ToolResourceKinds.ToolDefinitionVersion, ct))
        {
            var publication = stored.Value;
            if (!publication.Metadata.Annotations.TryGetValue("agentstration.io/query-uid", out var marker)
                || DataSourceQueryToolService.ReadQueryReference(publication) is not { } reference
                || marker != reference.QueryUid.ToString("D") || reference.Query.ScopeRef is not { } ownerScope
                || reference.Query.Namespace is not { } ns) continue;
            var owner = await resources.GetExactAsync<DataSourceQueryResource>(new(ownerScope, ns, DataSourceResourceKinds.DataSourceQuery, reference.Query.Name), ct);
            if (owner is null || owner.Value.Uid != reference.QueryUid || !owner.Value.Definition.Enabled) continue;
            DataSourceQueryVersionResource selected;
            try { selected = await queries.SelectPublicationAsync(owner.Value, reference.Version, ct); }
            catch (DataSourceValidationException) { continue; }
            var expected = publication.Definition.Bindings.OfType<LiteralToolArgumentBinding>().FirstOrDefault(binding => binding.Path == "/contract")?.Value;
            var compatible = expected is { ValueKind: JsonValueKind.String } contract && contract.GetString() == selected.ContractDigest;
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Publication(publication, publication.DefinitionUid,
                ToolResourceKinds.ToolDefinition, publication.DefinitionName, publication.Definition.Version, publishedAt: publication.PublishedAt), identities);
            await builder.PinAsync(owner.Value.Uid, "definition.bindings/query", ct, version: selected.Version,
                kind: reference.Version is null ? "dynamic-dependency" : "exact-dependency",
                resolution: reference.Version is null ? "latest-published-at" : "exact-publication",
                assessment: compatible ? reference.Version is null ? "compatible-dynamic-no-republication" : "fixed-publication" : "contract-mismatch");
            yield return builder.Build();
        }
    }
}
