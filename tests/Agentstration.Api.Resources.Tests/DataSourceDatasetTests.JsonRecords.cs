using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.Flows;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Storage;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Storage.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    [DataRow("data-source", false)]
    [DataRow("binding", false)]
    [DataRow("postAggregate", false)]
    [DataRow("data-source", true)]
    [DataRow("binding", true)]
    [DataRow("postAggregate", true)]
    public Task JsonExplodeKeepsOneArtifactWithIndependentRecords(string point, bool documentStorage) =>
        VerifyJsonRecordProjectionAsync(point, documentStorage, false);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task PostgreSqlJsonExplodePublishesIndependentRecords(bool knowledgeBinding) =>
        VerifyJsonRecordProjectionAsync(knowledgeBinding ? "binding" : "data-source", true, true);

    private async Task VerifyJsonRecordProjectionAsync(string point, bool documentStorage, bool postgres)
    {
        await using var factory = Factory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" })));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services, declareAcquisitionOutput: false, operationResult: JsonSerializer.SerializeToElement(new
        { results = new[] { new { status = "degraded", value = 1 }, new { status = "healthy", value = 2 } } }));
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        await using var structured = documentStorage ? await StructuredStorageFixture.StartAsync(factory.Services, client, Owner, postgres) : null;
        var storage = structured?.Reference ?? fixture.Storage;
        var explode = new FlowCompositionCall
        {
            Function = new("json-explode-builtin", "1.0.0", Owner),
            Parameters = JsonSerializer.SerializeToElement(new
            {
                path = "/results",
                itemSchema = new SchemaBinding
                { Inline = new() { Format = SchemaFormatIds.JsonSchema, Content = StructuredStorageFixture.Schema } }
            }, JsonSerializerOptions.Web)
        };
        var desired = Source("record-input", fixture, JsonSerializer.SerializeToElement(new { dataset = "alerts" }));
        desired = desired with
        {
            Definition = desired.Definition with
            {
                Dataset = desired.Definition.Dataset! with
                {
                    Storage = point == "data-source" ? storage : fixture.Storage,
                    DocumentCollectionUid = point == "data-source" ? structured?.Collection : null,
                    Transformations = point == "data-source" ? [explode] : []
                }
            }
        };
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(desired, default);
        var acquired = await AcquireAsync(client, source.Value.Name);
        Assert.HasCount(1, acquired.Manifest!.Artifacts);
        Assert.HasCount(point == "data-source" ? 2 : 1, acquired.Publication!.Records!.Members);
        Assert.AreEqual("object", acquired.Dataset!.OperationOutputSchema!.Value.GetProperty("type").GetString());
        var knowledge = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            ScopeRef = Owner,
            Metadata = new() { Name = "record-knowledge" },
            Definition = new()
            {
                DisplayName = "Record knowledge",
                Output = new(storage, new(Write: true)) { DocumentCollectionUid = structured?.Collection },
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                DataSources = [new() { Name = "input", DataSource = new(source.Value.Name, Owner), Transformations = point == "binding" ? [explode] : [] }],
                Transformations = point == "postAggregate" ? [explode] : []
            }
        }, default);
        var id = new KnowledgeSourceId(knowledge.Value.Name, ScopeRef: Owner);
        await factory.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(id, new(), default);
        var snapshot = (await factory.Services.GetRequiredService<KnowledgeSnapshotService>().GetActiveAsync(id, default))!.Snapshot;
        Assert.HasCount(1, snapshot.Artifacts);
        var artifact = snapshot.Artifacts.Single();
        Assert.HasCount(2, artifact.Content!.Records!.Members);
        Assert.AreEqual("array", artifact.DataContract!.Schema!.Value.GetProperty("type").GetString());
        Assert.IsNull(artifact.DataContract.SchemaEvidence, "A collection must not claim that the item Schema describes its whole content.");
        Assert.IsTrue(JsonElement.DeepEquals(StructuredStorageFixture.Schema, artifact.RecordContract!.Schema!.Value));
        var artifactId = FlowRunArtifactId.Parse(artifact.ArtifactId);
        var member = artifact.Content.Records.Members[1];
        using var read = await client.GetAsync($"/api/artifacts/flow-run-artifacts/{artifactId}/records/{member.Id}/content");
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
        var content = (await read.Content.ReadFromJsonAsync<ArtifactRecordContent>())!;
        Assert.IsTrue(content.EndOfRecord);
        using var json = JsonDocument.Parse(Convert.FromBase64String(content.ContentBase64));
        Assert.AreEqual(2, json.RootElement.GetProperty("value").GetInt32());
        using var forged = await client.GetAsync($"/api/artifacts/flow-run-artifacts/{artifactId}/records/foreign/content");
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, forged.StatusCode);
        if (structured is not null)
        {
            var access = factory.Services.GetRequiredService<KnowledgeAccessService>();
            var description = await access.DescribeDocumentsAsync(id, snapshot.Name, default);
            var page = await access.QueryDocumentsAsync(id, new()
            {
                SnapshotId = snapshot.Name,
                Profile = description.QueryProfiles.Single(),
                Query = StructuredStorageFixture.Json("""{"$filter":"value eq 2"}""")
            }, default);
            Assert.HasCount(1, page.DocumentPage!.Documents);
            Assert.AreEqual(artifact.ArtifactId, page.Records.Single().ArtifactId);
            Assert.AreEqual(1, page.Records.Single().MemberOrdinal);
        }
        else
        {
            var directory = Path.Combine(factory.Services.GetRequiredService<LocalStoragePaths>().DirectoryFor(storage),
                "publications", fixture.Context.WorkspaceId.ToString("N"));
            var files = artifact.Content.Records.Members.Select(record => Path.Combine(directory, record.Id + ".json")).ToArray();
            foreach (var file in files)
            {
                Assert.IsTrue(File.Exists(file));
                using var physical = JsonDocument.Parse(await File.ReadAllBytesAsync(file));
                Assert.AreEqual(JsonValueKind.Object, physical.RootElement.ValueKind);
                Assert.IsTrue(physical.RootElement.TryGetProperty("value", out _));
            }
            Assert.IsFalse(File.Exists(Path.Combine(directory, artifact.Content.Key.Split('/')[^1] + ".json")), "No aggregate physical file is created.");
            await Assert.ThrowsAsync<KnowledgeAccessException>(() => factory.Services.GetRequiredService<KnowledgeAccessService>()
                .DescribeDocumentsAsync(id, snapshot.Name, default));
        }
        var whole = await factory.Services.GetRequiredService<IStoragePublicationGateway>().ReadAsync(artifact.Content, 0, 65536, default);
        using var collection = JsonDocument.Parse(whole);
        Assert.AreEqual(2, collection.RootElement.GetArrayLength());
        if (point == "data-source" && structured is not null)
        {
            var access = factory.Services.GetRequiredService<KnowledgeAccessService>();
            var description = await access.DescribeDocumentsAsync(id, snapshot.Name, default);
            var firstPage = await access.QueryDocumentsAsync(id, new()
            {
                SnapshotId = snapshot.Name,
                Profile = description.QueryProfiles.Single(),
                Query = Empty,
                PageSize = 1
            }, default);
            Assert.IsNotNull(firstPage.NextCursor);
            var replacement = await PublishAsync(factory.Services, "record-replacement", KnowledgeFlowContracts.ArtifactTransformation,
                JsonSerializer.SerializeToElement(new
                {
                    artifacts = new[] { new
                {
                    artifactId = "replacement", kind = "inline", disposition = "publishable", mediaType = "application/json",
                    recordContract = artifact.RecordContract,
                    contentBase64 = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new[]
                    { new { status = "healthy", value = 9 }, new { status = "degraded", value = 8 } }))
                } }
                }));
            await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().PutAsync(id, knowledge.Value.Definition with
            {
                DataSources = [knowledge.Value.Definition.DataSources.Single() with
                { TransformationFlow = new() { Name = replacement.Flow.Name, ScopeRef = Owner, Version = replacement.Version, UseActiveVersion = false } }]
            }, knowledge.ETag, default);
            await factory.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(id, new(), default);
            var active = (await factory.Services.GetRequiredService<KnowledgeSnapshotService>().GetActiveAsync(id, default))!.Snapshot;
            Assert.AreNotEqual(snapshot.Name, active.Name);
            var continued = await access.QueryDocumentsAsync(id, new() { Cursor = firstPage.NextCursor }, default);
            Assert.AreEqual(snapshot.Name, continued.SnapshotId);
            Assert.AreEqual(2, continued.DocumentPage!.Documents.Single().Value.GetProperty("value").GetInt32());
            Assert.AreEqual(artifact.ArtifactId, continued.Records.Single().ArtifactId);
            Assert.AreEqual(1, continued.Records.Single().MemberOrdinal);
            var current = await access.QueryDocumentsAsync(id, new() { SnapshotId = active.Name, Profile = description.QueryProfiles.Single(), Query = Empty }, default);
            CollectionAssert.AreEquivalent(new[] { 8, 9 }, current.DocumentPage!.Documents.Select(row => row.Value.GetProperty("value").GetInt32()).ToArray());
        }
        using var operational = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context);
        var stored = await factory.Services.GetRequiredService<ArtifactManagementService>().GetFlowRunArtifactAsync(artifactId, default);
        Assert.AreEqual(2, stored!.Value.Receipt.Materialization!.Publication!.Records!.Members.Count);
        if (point == "data-source" && !documentStorage)
        {
            var records = factory.Services.GetRequiredService<ArtifactRecordService>();
            using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context with
            { Restriction = new(Guid.NewGuid(), fixture.Context.WorkspaceId, new HashSet<string> { AuthorizationPermissions.ArtifactsInspect }) }))
            {
                Assert.HasCount(2, (await records.GetAsync(artifactId, default)).Members);
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => records.ReadAsync(artifactId, member.Id, 0, 65536, default));
            }
            var otherContext = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            Assert.AreNotEqual(fixture.Context.WorkspaceId, otherContext.WorkspaceId);
            using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(otherContext))
            {
                await Assert.ThrowsAsync<ArtifactValidationException>(() => records.GetAsync(artifactId, default));
            }
        }
    }
}
