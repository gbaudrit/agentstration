using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Functions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Storage;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas.Contracts;
using Agentstration.Storage.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(2, 0)]
    [DataRow(1, 80000)]
    public async Task RssMappingRunsThroughSourcesFlowsAndSingleArtifactRecordPublication(int itemCount, int titleLength)
    {
        await using var factory = Factory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" })));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var feed = RssTransformationTestData.Feed(itemCount);
        // Grow one mapped scalar rather than changing the declared cardinality contract.
        if (titleLength > 0)
        {
            var document = System.Xml.Linq.XDocument.Parse(feed);
            document.Root!.Element("channel")!.Element("item")!.Element("title")!.Value = new string('a', titleLength);
            feed = document.ToString();
        }
        var fixture = await PrepareAsync(factory.Services, declareAcquisitionOutput: false,
            operationResult: JsonSerializer.SerializeToElement(new { mediaType = "application/rss+xml", content = feed }),
            operationOutputSchema: JsonSerializer.Deserialize<JsonElement>("""
                {"type":"object","properties":{"mediaType":{"type":"string"},"content":{"type":"string"}},"required":["mediaType","content"],"additionalProperties":false}
                """));
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        await RssTransformationTestData.CreateAsync(factory.Services, Owner);
        if (itemCount == 0) await VerifyAcquisitionContentAdmissionAsync(factory.Services);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        (string Point, bool DocumentStorage, bool Explode)[] cases = itemCount == 0 || titleLength > 0
            ? [("data-source", false, false)]
            : [("data-source", false, false), ("data-source", false, true), ("data-source", true, true),
                ("binding", false, true), ("postAggregate", false, true)];
        foreach (var (point, documentStorage, explode) in cases)
            await VerifyRssPublicationAsync(factory.Services, client, fixture, point, documentStorage, explode);
    }

    private static async Task VerifyRssPublicationAsync(IServiceProvider services, HttpClient client, Fixture fixture,
        string point, bool documentStorage, bool explode)
    {
        var suffix = point.ToLowerInvariant() + "-" + (documentStorage ? "document" : explode ? "records" : "feed");
        var itemSchema = RssTransformationTestData.Declarations[1].Definition.GetProperty("content").GetProperty("properties").GetProperty("items").GetProperty("items");
        await using var structured = documentStorage ? await StructuredStorageFixture.StartAsync(services, client, Owner, false, itemSchema) : null;
        var storage = structured?.Reference ?? fixture.Storage;
        var convert = new FlowCompositionCall { Function = new("rss-2-0-xml-to-json", "1.0.0", Owner) };
        var split = new FlowCompositionCall
        {
            Function = new("json-explode-builtin", "1.0.0", Owner),
            Parameters = JsonSerializer.SerializeToElement(new
            {
                path = "/items",
                itemSchema = new SchemaBinding
                { Reference = new() { Name = "rss-2-0-json", Version = "1.0.0", ScopeRef = Owner, SchemaPath = "/properties/items/items" } }
            }, JsonSerializerOptions.Web)
        };
        var sourceDefinition = Source("rss-input-" + suffix, fixture, JsonSerializer.SerializeToElement(new { dataset = "rss" }));
        var source = await services.GetRequiredService<DataSourceManagementService>().CreateAsync(sourceDefinition with
        {
            Definition = sourceDefinition.Definition with
            {
                Dataset = sourceDefinition.Definition.Dataset! with
                {
                    Storage = point == "data-source" ? storage : fixture.Storage,
                    DocumentCollectionUid = point == "data-source" ? structured?.Collection : null,
                    Transformations = point != "data-source" ? [] : explode
                        ? [convert, split]
                        : [convert]
                }
            }
        }, default);
        if (point == "data-source" && !explode)
        {
            var management = services.GetRequiredService<DataSourceManagementService>();
            var rejected = await Assert.ThrowsExactlyAsync<DataSourceValidationException>(() => management.PutAsync(
                source.Value.Namespace, source.Value.Name, Owner, source.Value.Definition with
                { Dataset = source.Value.Definition.Dataset! with { Transformations = [convert with { FunctionInputSource = FlowCompositionFunctionInputSource.PreviousResult }] } }, source.ETag, default));
            Assert.AreEqual("data_source_composition_invalid", rejected.Code);
            Assert.AreEqual("flow_composition_function_input_incompatible", rejected.CompositionErrorCode);
            Assert.AreEqual(source.ETag, (await management.GetAsync(source.Value.Namespace, source.Value.Name, Owner, default))!.ETag,
                "An incompatible full-response call must preserve the saved content conversion sequence.");
        }
        var acquired = await AcquireAsync(client, source.Value.Name);
        Assert.HasCount(1, acquired.Manifest!.Artifacts);
        Assert.HasCount(point == "data-source" && explode ? 2 : 1, acquired.Publication!.Records!.Members);
        if (point == "data-source")
        {
            var receipt = acquired.Dataset!.Composition!.Receipt!.Bindings["transformations"].Single(c => c.Function?.Name == "rss-2-0-xml-to-json");
            Assert.AreEqual(FlowCompositionFunctionInputSource.AcquisitionContent, receipt.FunctionInputSource);
            Assert.HasCount(explode ? 2 : 1, acquired.Dataset.Composition.Receipt.Bindings["transformations"]);
            Assert.IsNotNull(receipt.Function!.Uid);
            Assert.IsNotNull(receipt.Function.Digest);
        }
        FlowCompositionCall? process = null;
        if (point != "data-source")
        {
            // Knowledge receives Artifacts, so reuse its explicit content-processing region.
            // The same named function is executed in an ordinary Flow transform inside that region.
            var flows = services.GetRequiredService<FlowService>();
            var manifest = (await flows.GetVersionExactAsync(Owner, new("knowledge-projection-builtin"), "1.0.0", default))!.Value.Graph!.OutputSchema!.Value;
            var input = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = manifest, parameters = new { type = "object" } }, required = new[] { "value", "parameters" }, additionalProperties = false });
            var feedSchema = RssTransformationTestData.Declarations[1].Definition.GetProperty("content");
            await flows.CreateExactAsync(Owner, new("rss-content-" + suffix, null, "1.0.0", true,
                new WorkflowFlowDefinition("input", [new("input", FlowNodeKind.Input)], []), Graph: new()
                {
                    EntryStep = "input",
                    InputSchema = input,
                    OutputSchema = manifest,
                    Steps = [new InputFlowStepDefinition { Name = "input", Schema = input },
                        new ForeachArtifactsFlowStepDefinition { Name = "process", OutputContract = new() { Schema = feedSchema } },
                        new TransformFlowStepDefinition { Name = "extract", Function = new("json-select-builtin", "1.0.0", Owner),
                            Mapping = JsonSerializer.SerializeToElement(new { value = "${input.content}", path = "/0/content" }) },
                        new TransformFlowStepDefinition { Name = "convert", Function = convert.Function,
                            Mapping = JsonSerializer.SerializeToElement(new { value = "${transition.output}" }) },
                        new OutputFlowStepDefinition { Name = "output", Schema = manifest, OutputMapping = JsonSerializer.SerializeToElement("${transition.output}") },
                        new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }],
                    Transitions = [new("start", "input", "completed", "process"), new("body", "process", "process", "extract"),
                        new("convert", "extract", "completed", "convert"), new("return", "convert", "completed", "process"),
                        new("done", "process", "completed", "output"), new("fail", "process", "error", "error")]
                }), ResourceNamespace.Default, default);
            var published = await flows.PublishVersionExactAsync(Owner, new("rss-content-" + suffix), "1.0.0", true, default);
            Assert.IsNotNull(published.Value.Graph!.Steps.OfType<TransformFlowStepDefinition>().Single(s => s.Name == "convert").Function!.Digest);
            process = new() { Flow = new("rss-content-" + suffix, FlowCallVersionStrategy.Exact, "1.0.0", ScopeRef: Owner) };
        }
        var knowledge = await services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            ScopeRef = Owner,
            Metadata = new() { Name = "rss-knowledge-" + suffix },
            Definition = new()
            {
                DisplayName = "RSS knowledge",
                Output = new(storage, new(Write: true)) { DocumentCollectionUid = structured?.Collection },
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                DataSources = [new() { Name = "rss", DataSource = new(source.Value.Name, Owner), Transformations = point == "binding" ? [process!, split] : [] }],
                Transformations = point == "postAggregate" ? [process!, split] : []
            }
        }, default);
        var id = new KnowledgeSourceId(knowledge.Value.Name, ScopeRef: Owner);
        var projection = await services.GetRequiredService<KnowledgeProjectionService>().StartAsync(id, new(), default);
        Assert.AreEqual(KnowledgeProjectionState.Succeeded, projection.Value.State, projection.Value.ErrorMessage);
        var snapshot = (await services.GetRequiredService<KnowledgeSnapshotService>().GetActiveAsync(id, default))!.Snapshot;
        var artifact = snapshot.Artifacts.Single();
        Assert.HasCount(explode ? 2 : 1, artifact.Content!.Records!.Members);
        var member = artifact.Content.Records.Members[0];
        using var recordBytes = new MemoryStream();
        ArtifactRecordContent content;
        do
        {
            using var read = await client.GetAsync($"/api/artifacts/flow-run-artifacts/{artifact.ArtifactId}/records/{member.Id}/content?offset={recordBytes.Length}&length=65536");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            content = (await read.Content.ReadFromJsonAsync<ArtifactRecordContent>())!;
            Assert.AreEqual(recordBytes.Length, content.Offset);
            var bytes = Convert.FromBase64String(content.ContentBase64);
            Assert.IsTrue(content.EndOfRecord || bytes.Length > 0, "A bounded record read must make progress.");
            recordBytes.Write(bytes);
            Assert.IsTrue(recordBytes.Length <= member.Length);
        } while (!content.EndOfRecord);
        Assert.AreEqual(member.Length, recordBytes.Length);
        using var json = JsonDocument.Parse(recordBytes.ToArray());
        Assert.AreEqual(explode ? "opaque:0:not-a-uuid" : "Synthetic feed", json.RootElement.GetProperty(explode ? "guid" : "title").GetString());
        if (explode)
        {
            Assert.AreEqual("/properties/items/items", artifact.RecordContract!.SchemaEvidence!.SchemaPath);
            if (structured is not null)
            {
                var access = services.GetRequiredService<KnowledgeAccessService>();
                var description = await access.DescribeDocumentsAsync(id, snapshot.Name, default);
                var page = await access.QueryDocumentsAsync(id, new()
                {
                    SnapshotId = snapshot.Name,
                    Profile = description.QueryProfiles.Single(),
                    Query = RssTransformationTestData.Json("""{"$filter":"creator eq 'Author 1'"}"""),
                    PageSize = 1
                }, default);
                Assert.HasCount(1, page.DocumentPage!.Documents);
                Assert.AreEqual(artifact.ArtifactId, page.Records.Single().ArtifactId);
                Assert.AreEqual(1, page.Records.Single().MemberOrdinal);
            }
            else
            {
                var directory = Path.Combine(services.GetRequiredService<LocalStoragePaths>().DirectoryFor(storage), "publications", fixture.Context.WorkspaceId.ToString("N"));
                foreach (var record in artifact.Content.Records.Members)
                    Assert.IsTrue(File.Exists(Path.Combine(directory, record.Id + ".json")));
            }
        }
    }
}
