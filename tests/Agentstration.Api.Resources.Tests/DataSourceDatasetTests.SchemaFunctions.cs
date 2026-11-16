using System.Text.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.Flows;
using Agentstration.Flows.Application;
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
    [DataRow(false, "none")]
    [DataRow(false, "inline")]
    [DataRow(true, "inline")]
    [DataRow(false, "shared")]
    [DataRow(true, "shared")]
    [DataRow(false, "postAggregate")]
    [DataRow(false, "flow")]
    public async Task SharedResponseSchemaAndSelectedArrayKeepDistinctEvidenceThroughNativeExplosion(bool adoptFinalSchema, string itemMode)
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services, operationResult: JsonSerializer.SerializeToElement(new
        { results = new[] { new { id = "a" }, new { id = "b" } } }), declareAcquisitionOutput: false);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var schemas = factory.Services.GetRequiredService<SchemaManagementService>();
        var parent = await schemas.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SchemaResourceKinds.Schema,
            ScopeRef = Owner,
            Metadata = new() { Name = "alerts-envelope" },
            Definition = new()
            {
                DisplayName = "Alerts envelope",
                Format = SchemaFormatIds.JsonSchema,
                Content = JsonSerializer.Deserialize<JsonElement>("""
                    {"type":"object","properties":{"results":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}}},"required":["results"]}
                    """)
            }
        }, default);
        var published = await schemas.PublishAsync(Owner, ResourceNamespace.Default, parent.Value.Name, "1.0.0", default);
        var rawBinding = new SchemaBinding { Reference = new() { Name = parent.Value.Name, Version = "1.0.0", ScopeRef = Owner } };
        var arrayBinding = rawBinding with { Reference = rawBinding.Reference! with { SchemaPath = "/properties/results" } };
        var desired = Source("shared-selected-array", fixture, JsonSerializer.SerializeToElement(new { dataset = "alerts" }));
        desired = desired with
        {
            Definition = desired.Definition with
            {
                Dataset = desired.Definition.Dataset! with
                {
                    Acquisition = desired.Definition.Dataset.Acquisition with { OutputSchemaBinding = rawBinding },
                    OutputSchemaBinding = adoptFinalSchema ? arrayBinding : null,
                    Compositions = new Dictionary<string, IReadOnlyList<FlowCompositionCall>>
                    {
                        ["transformations"] = [new()
            { Function = new("json-explode-builtin", "1.0.0", Owner), Parameters = JsonSerializer.SerializeToElement(new
                { path = "/results", itemSchema = new SchemaBinding { Reference = rawBinding.Reference! with { SchemaPath = "/properties/results/items" } } }, JsonSerializerOptions.Web) }]
                    }
                }
            }
        };
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
                Assert.IsNull(receipt.DataContract.SchemaEvidence);
                var record = receipt.Materialization!.RecordContract!;
                Assert.AreEqual("/properties/results/items", record.SchemaEvidence!.SchemaPath);
                Assert.AreEqual(published.Value.DefinitionHash, record.SchemaEvidence.ParentDefinitionHash);
                Assert.IsTrue(JsonElement.DeepEquals(parent.Value.Definition.Content.GetProperty("properties").GetProperty("results").GetProperty("items"), record.Schema!.Value));
            }
            else Assert.IsNull(receipt.DataContract.SchemaEvidence, "An unconstrained transformation output must not retain the envelope's shared Schema evidence.");
        }
        var itemSchema = parent.Value.Definition.Content.GetProperty("properties").GetProperty("results").GetProperty("items");
        var itemBinding = itemMode is "shared" or "postAggregate" or "flow"
            ? new SchemaBinding { Reference = rawBinding.Reference! with { SchemaPath = "/properties/results/items" } }
            : new SchemaBinding { Inline = new() { Format = SchemaFormatIds.JsonSchema, Content = itemSchema } };
        var explodeParameters = new Dictionary<string, object> { ["path"] = "" };
        if (itemMode != "none") explodeParameters["itemSchema"] = itemBinding;
        var explode = new FlowCompositionCall
        {
            Function = new("artifact-explode-builtin", "1.0.0", Owner),
            Parameters = JsonSerializer.SerializeToElement(explodeParameters, JsonSerializerOptions.Web)
        };
        if (itemMode == "flow")
        {
            var flows = factory.Services.GetRequiredService<FlowService>();
            var envelope = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"value":{"type":"object"},"parameters":{"type":"object"}},"required":["value","parameters"]}""");
            var manifest = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"artifacts":{"type":"array"}},"required":["artifacts"],"additionalProperties":false}""");
            await flows.CreateExactAsync(Owner, new("explode-flow", null, "1.0.0", true,
                new WorkflowFlowDefinition("input", [new FlowNode("input", FlowNodeKind.Input)], []), Graph: new()
                {
                    EntryStep = "input",
                    InputSchema = envelope,
                    OutputSchema = manifest,
                    Steps = [new InputFlowStepDefinition { Name = "input", Schema = envelope },
                        new TransformFlowStepDefinition { Name = "explode", Function = explode.Function,
                            Mapping = JsonSerializer.SerializeToElement(new { value = "${input.value}", path = "" }),
                            FunctionSchemas = new Dictionary<string, SchemaBinding> { ["itemSchema"] = itemBinding } },
                        new OutputFlowStepDefinition { Name = "output", Schema = manifest, OutputMapping = JsonSerializer.SerializeToElement("${transition.output}") }],
                    Transitions = [new("start", "input", "completed", "explode"), new("done", "explode", "completed", "output")]
                }), ResourceNamespace.Default, default);
            var pinnedFlow = await flows.PublishVersionExactAsync(Owner, new("explode-flow"), "1.0.0", true, default);
            Assert.AreEqual(parent.Value.Uid, pinnedFlow.Value.Graph!.Steps.OfType<TransformFlowStepDefinition>().Single().FunctionSchemaSnapshots!["itemSchema"].SchemaUid);
            explode = new() { Flow = new("explode-flow", FlowCallVersionStrategy.Exact, "1.0.0", ScopeRef: Owner) };
        }
        var knowledge = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            ScopeRef = Owner,
            Metadata = new() { Name = "shared-items" },
            Definition = new()
            {
                DisplayName = "Shared items",
                Output = new(fixture.Storage, new(Write: true)),
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                DataSources = [new() { Name = "alerts", DataSource = new(source.Value.Name, Owner), RefreshIfNecessary = true,
                    Transformations = itemMode == "postAggregate" ? [] : [explode] }],
                Transformations = itemMode == "postAggregate" ? [explode] : []
            }
        }, default);
        if (itemMode is "shared" or "postAggregate" or "flow")
        {
            if (itemMode != "flow")
            {
                var plan = itemMode == "postAggregate" ? knowledge.Value.Composition!.ProjectionComposition!
                    : knowledge.Value.Composition!.InputCompositions["alerts"];
                var frozen = plan.Receipt!.Bindings.Values.SelectMany(value => value).Single(call => call.Function is not null).FunctionSchemaSnapshots!["itemSchema"];
                Assert.AreEqual(parent.Value.Uid, frozen.SchemaUid);
                Assert.AreEqual(published.Value.DefinitionHash, frozen.ParentDefinitionHash);
            }
            // An incompatible newer publication and even loss of the catalogue entry must not
            // trigger resolution of a different schema during an already accepted projection.
            parent = await schemas.PutAsync(Owner, parent.Value.Namespace, parent.Value.Name,
                parent.Value.Definition with { Content = JsonSerializer.Deserialize<JsonElement>("""{"type":"integer"}""") }, parent.ETag, default);
            await schemas.PublishAsync(Owner, ResourceNamespace.Default, parent.Value.Name, "2.0.0", default);
            await factory.Services.GetRequiredService<IResourceStore>().DeleteExactAsync(new(Owner, parent.Value.Namespace,
                SchemaResourceKinds.Schema, parent.Value.Name), parent.ETag, default);
        }
        var id = new KnowledgeSourceId(knowledge.Value.Name, ScopeRef: Owner);
        var projection = await factory.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(id, new(), default);
        Assert.AreEqual(KnowledgeProjectionState.Succeeded, projection.Value.State);
        var snapshot = (await factory.Services.GetRequiredService<KnowledgeSnapshotService>().GetActiveAsync(id, default))!.Snapshot;
        Assert.HasCount(2, snapshot.Artifacts);
        foreach (var artifact in snapshot.Artifacts)
        {
            Assert.IsNull(artifact.DataContract?.SchemaEvidence);
            if (itemMode == "none") { Assert.IsNull(artifact.RecordContract?.SchemaEvidence); continue; }
            Assert.IsTrue(artifact.DataContract!.Validated);
            Assert.IsTrue(JsonElement.DeepEquals(itemSchema, artifact.RecordContract!.Schema!.Value));
            if (itemMode is "shared" or "postAggregate" or "flow")
            {
                var evidence = artifact.RecordContract.SchemaEvidence!;
                Assert.AreEqual("1.0.0", evidence.Version);
                Assert.AreEqual("/properties/results/items", evidence.SchemaPath);
                Assert.AreEqual(published.Value.DefinitionHash, evidence.ParentDefinitionHash);
                Assert.AreEqual(parent.Value.Uid, evidence.SchemaUid);
            }
            else Assert.IsNull(artifact.RecordContract.SchemaEvidence, "An inline item contract must not claim a published Schema identity.");
        }
        if (itemMode == "inline")
        {
            var sources = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
            var invalid = explode with
            {
                Parameters = JsonSerializer.SerializeToElement(new
                {
                    path = "",
                    itemSchema = new SchemaBinding
                    { Inline = new() { Format = SchemaFormatIds.JsonSchema, Content = JsonSerializer.Deserialize<JsonElement>("""{"type":"integer"}""") } }
                }, JsonSerializerOptions.Web)
            };
            await sources.PutAsync(id, knowledge.Value.Definition with
            {
                DataSources = knowledge.Value.Definition.DataSources
                .Select(binding => binding with { Transformations = [invalid] }).ToArray()
            }, knowledge.ETag, default);
            await Assert.ThrowsAsync<KnowledgeProjectionException>(() => factory.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(id, new(), default));
            Assert.AreEqual(snapshot.Name, (await factory.Services.GetRequiredService<KnowledgeSnapshotService>().GetActiveAsync(id, default))!.Snapshot.Name);
        }
    }
}
