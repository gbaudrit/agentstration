using System.Text.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.Flows;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SharedResponseSchemaAndSelectedArrayKeepDistinctEvidenceThroughNativeExplosion(bool adoptFinalSchema)
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services, operationResult: JsonSerializer.SerializeToElement(new
        { results = new[] { new { id = "a" }, new { id = "b" } } }), declareAcquisitionOutput: false);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var schemas = factory.Services.GetRequiredService<SchemaManagementService>();
        var parent = await schemas.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = SchemaResourceKinds.Schema, ScopeRef = Owner,
            Metadata = new() { Name = "alerts-envelope" }, Definition = new()
            {
                DisplayName = "Alerts envelope", Format = SchemaFormatIds.JsonSchema,
                Content = JsonSerializer.Deserialize<JsonElement>("""
                    {"type":"object","properties":{"results":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}}},"required":["results"]}
                    """)
            }
        }, default);
        var published = await schemas.PublishAsync(Owner, ResourceNamespace.Default, parent.Value.Name, "1.0.0", default);
        var rawBinding = new SchemaBinding { Reference = new() { Name = parent.Value.Name, Version = "1.0.0", ScopeRef = Owner } };
        var arrayBinding = rawBinding with { Reference = rawBinding.Reference! with { SchemaPath = "/properties/results" } };
        var desired = Source("shared-selected-array", fixture, JsonSerializer.SerializeToElement(new { dataset = "alerts" }));
        desired = desired with { Definition = desired.Definition with { Dataset = desired.Definition.Dataset! with
        {
            Acquisition = desired.Definition.Dataset.Acquisition with { OutputSchemaBinding = rawBinding },
            OutputSchemaBinding = adoptFinalSchema ? arrayBinding : null,
            Compositions = new Dictionary<string, IReadOnlyList<FlowCompositionCall>> { ["transformations"] = [new()
            { Function = new("json-select-builtin", "1.0.0", Owner), Parameters = JsonSerializer.SerializeToElement(new { path = "/results" }) }] }
        } } };
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(desired, default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        var acquired = await AcquireAsync(client, source.Value.Name);
        Assert.AreEqual("object", acquired.Dataset!.OperationOutputSchema!.Value.GetProperty("type").GetString());
        var acquisitionCall = acquired.Dataset.Composition!.Receipt!.Bindings["acquisition"].Single();
        Assert.AreEqual("object", acquisitionCall.Flow!.OutputSchema!.Value.GetProperty("type").GetString());
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context))
        {
            var receipt = (await factory.Services.GetRequiredService<IResourceStore>().GetExactAsync<FlowRunArtifactResource>(new(
                ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), ResourceNamespace.Default, ArtifactResourceKinds.FlowRunArtifact,
                acquired.Manifest!.Artifacts.Single().ArtifactId), default))!.Value.Receipt;
            Assert.IsTrue(receipt.DataContract!.Validated);
            if (adoptFinalSchema)
            {
                Assert.AreEqual("array", receipt.DataContract.Schema!.Value.GetProperty("type").GetString());
                Assert.AreEqual("/properties/results", receipt.DataContract.SchemaEvidence!.SchemaPath);
                Assert.AreEqual(published.Value.DefinitionHash, receipt.DataContract.SchemaEvidence.ParentDefinitionHash);
                Assert.AreEqual(source.Value.Definition.Dataset!.OutputSchemaSnapshot!.DefinitionHash, receipt.DataContract.SchemaEvidence.DefinitionHash);
            }
            else Assert.IsNull(receipt.DataContract.SchemaEvidence, "An unconstrained transformation output must not retain the envelope's shared Schema evidence.");
        }
        var itemSchema = parent.Value.Definition.Content.GetProperty("properties").GetProperty("results").GetProperty("items");
        var knowledge = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = KnowledgeResourceKinds.KnowledgeSource, ScopeRef = Owner,
            Metadata = new() { Name = "shared-items" }, Definition = new()
            {
                DisplayName = "Shared items", Output = new(fixture.Storage, new(Write: true)),
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                DataSources = [new() { Name = "alerts", DataSource = new(source.Value.Name, Owner), RefreshIfNecessary = true,
                    Transformations = [new() { Function = new("artifact-explode-builtin", "1.0.0", Owner),
                        Parameters = JsonSerializer.SerializeToElement(new { path = "", itemSchema }) }] }]
            }
        }, default);
        var id = new KnowledgeSourceId(knowledge.Value.Name, ScopeRef: Owner);
        var projection = await factory.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(id, new(), default);
        Assert.AreEqual(KnowledgeProjectionState.Succeeded, projection.Value.State);
        var snapshot = (await factory.Services.GetRequiredService<KnowledgeSnapshotService>().GetActiveAsync(id, default))!.Snapshot;
        Assert.HasCount(2, snapshot.Artifacts);
        foreach (var artifact in snapshot.Artifacts)
        {
            Assert.IsTrue(artifact.DataContract!.Validated);
            Assert.IsTrue(JsonElement.DeepEquals(itemSchema, artifact.DataContract.Schema!.Value));
            Assert.IsNull(artifact.DataContract.SchemaEvidence, "An inline item contract must not claim the parent envelope or array's published Schema identity.");
        }
    }
}
