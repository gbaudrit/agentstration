using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Storage.Contracts;
using Agentstration.Tools.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AcquiredJsonProjectsToImmutableStructuredKnowledgeWithGovernedToolsAndHistoricalQueries(bool postgres)
    {
        await using var factory = Factory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" })));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var inputs = await PrepareAsync(factory.Services);
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(inputs.Context);
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        await using var storage = await StructuredStorageFixture.StartAsync(factory.Services, client, Owner, postgres);
        var inputDraft = Source("structured-json-input", inputs, JsonSerializer.SerializeToElement(new { dataset = "structured" }));
        inputDraft = inputDraft with
        {
            Definition = inputDraft.Definition with
            { Dataset = inputDraft.Definition.Dataset! with { Storage = storage.Reference, DocumentCollectionUid = storage.Collection } }
        };
        var invalidCollection = inputDraft with
        {
            Definition = inputDraft.Definition with
            { Dataset = inputDraft.Definition.Dataset! with { DocumentCollectionUid = Guid.NewGuid() } }
        };
        var invalid = await Assert.ThrowsAsync<Agentstration.DataSources.DataSourceValidationException>(() =>
            factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(invalidCollection, default));
        Assert.AreEqual("data_source_document_collection_unavailable", invalid.Code);
        var privateCollection = storage.Storage.Definition.Collections.Single(c => c.Name == "private").Uid;
        invalidCollection = inputDraft with
        {
            Definition = inputDraft.Definition with
            { Dataset = inputDraft.Definition.Dataset! with { DocumentCollectionUid = privateCollection } }
        };
        invalid = await Assert.ThrowsAsync<Agentstration.DataSources.DataSourceValidationException>(() =>
            factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(invalidCollection, default));
        Assert.AreEqual("data_source_document_publication_required", invalid.Code);
        var input = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(inputDraft, default);
        var contentContract = DataContracts.Declared(StructuredStorageFixture.Json("""
            {"type":"object","required":["status","value"],"properties":{"status":{"type":"string"},"value":{"type":"number"}}}
            """), "structured-projection", "application/json")!;
        async Task<Agentstration.ConnectedServices.Contracts.ConnectedServiceDiscoveryFlowReference> Normalize(string name, int value) =>
            await PublishAsync(factory.Services, name, KnowledgeFlowContracts.ArtifactTransformation, JsonSerializer.SerializeToElement(new
            {
                artifacts = new[] { "a", "b" }.Select(id => new
                {
                    artifactId = id,
                    kind = "inline",
                    disposition = "publishable",
                    mediaType = "application/json",
                    dataContract = contentContract,
                    contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(new { status = "degraded", value })))
                }).ToArray()
            }));
        var firstFlow = await Normalize("normalize-structured-a", 42);
        var sources = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
        var source = await sources.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            ScopeRef = Owner,
            Metadata = new() { Name = "structured-knowledge" },
            Definition = new()
            {
                DisplayName = "Structured knowledge",
                Output = new(storage.Reference, new(Write: true)) { DocumentCollectionUid = storage.Collection },
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                DataSources = [new() { Name = "json", DataSource = new(input.Value.Name, Owner), RefreshIfNecessary = true, MaximumAge = TimeSpan.FromHours(1),
                    TransformationFlow = new() { Name = firstFlow.Flow.Name, ScopeRef = Owner, Version = firstFlow.Version, UseActiveVersion = false } }]
            }
        }, default);
        var id = new KnowledgeSourceId(source.Value.Name, ScopeRef: Owner);
        var access = factory.Services.GetRequiredService<KnowledgeAccessService>();
        var description = await access.DescribeDocumentsAsync(id, null, default);
        var profile = description.QueryProfiles.Single();
        var query = new KnowledgeDocumentQueryRequest { Profile = profile, Query = StructuredStorageFixture.Json("""{"$filter":"status eq 'degraded'"}"""), PageSize = 1 };
        var first = await AwaitNativeAsync(await access.QueryDocumentsAsync(id, query, default),
            pending => access.QueryDocumentsAsync(id, query with { PreparationId = pending.PreparationId }, default));
        Assert.AreEqual("ready", first.State);
        Assert.AreEqual(42, first.DocumentPage!.Documents.Single().Value.GetProperty("value").GetInt32());
        Assert.IsNotNull(first.NextCursor);
        var a = first.SnapshotId!;
        var read = await access.ReadAsync(id, new() { SnapshotId = a }, default);
        Assert.IsTrue(read.Chunks.All(c => c.Content.Contains("\"value\":42", StringComparison.Ordinal)));
        var exposure = await factory.Services.GetRequiredService<KnowledgeSourceToolExposureService>().PublishAsync(id, "1.0.0", false, default);
        Assert.HasCount(4, exposure.Value.Operations);
        var queryTool = exposure.Value.Operations.Single(o => o.Operation == KnowledgeSourceOperation.StructuredQuery).Tool;
        var pinnedTool = await factory.Services.GetRequiredService<IFlowToolContractResolver>().ResolveAsync(
            queryTool.ScopeRef ?? Owner, queryTool.Namespace ?? ResourceNamespace.Default,
            new(queryTool.Name, queryTool.Namespace, queryTool.ScopeRef), default);
        Assert.AreEqual("object", pinnedTool.OutputSchema!.Value.GetProperty("type").GetString());
        using var toolCall = await client.PostAsJsonAsync($"/api/tools/{queryTool.Name}/run?scopeRef=/instance",
            new RunToolRequest(JsonSerializer.SerializeToElement(new { request = new { snapshotId = a, query = new { } } }), ToolRunMode.Execute));
        Assert.IsTrue(toolCall.IsSuccessStatusCode, await toolCall.Content.ReadAsStringAsync());
        var toolResult = (await toolCall.Content.ReadFromJsonAsync<RunToolResponse>())!;
        Assert.AreEqual("completed", toolResult.Status, JsonSerializer.Serialize(toolResult));
        Assert.AreEqual(a, toolResult.Output!.Value.GetProperty("snapshotId").GetString());
        var secondFlow = await Normalize("normalize-structured-b", 99);
        var binding = source.Value.Definition.DataSources.Single();
        await sources.PutAsync(id, source.Value.Definition with
        {
            DataSources = [binding with
            { TransformationFlow = new() { Name = secondFlow.Flow.Name, ScopeRef = Owner, Version = secondFlow.Version, UseActiveVersion = false } }]
        }, source.ETag, default);
        var second = await AwaitNativeAsync(await access.QueryDocumentsAsync(id, query, default),
            pending => access.QueryDocumentsAsync(id, query with { PreparationId = pending.PreparationId }, default));
        Assert.AreNotEqual(a, second.SnapshotId);
        Assert.AreEqual(99, second.DocumentPage!.Documents.Single().Value.GetProperty("value").GetInt32());
        var continued = await access.QueryDocumentsAsync(id, new() { Cursor = first.NextCursor }, default);
        Assert.AreEqual(a, continued.SnapshotId);
        Assert.AreEqual(42, continued.DocumentPage!.Documents.Single().Value.GetProperty("value").GetInt32());
        var exact = await access.QueryDocumentsAsync(id, query with { SnapshotId = a }, default);
        Assert.AreEqual(42, exact.DocumentPage!.Documents.Single().Value.GetProperty("value").GetInt32());
        var grouped = await access.QueryDocumentsAsync(id, query with
        {
            SnapshotId = a,
            PageSize = 10,
            Query = StructuredStorageFixture.Json("""{"$apply":"aggregate($count as Count,value with sum as Total)"}""")
        }, default);
        Assert.AreEqual(2, grouped.DocumentPage!.Groups.Single().GetProperty("Count").GetInt32());
        Assert.AreEqual(84, grouped.DocumentPage.Groups.Single().GetProperty("Total").GetInt32());
        var snapshots = await factory.Services.GetRequiredService<KnowledgeSnapshotService>().ListAsync(id, default);
        Assert.HasCount(2, snapshots);
        Assert.IsTrue(snapshots.All(s => s.Snapshot.Artifacts.All(artifact =>
            artifact.DataContract is { Validated: true, MediaType: "application/json" } contract && contract.Digest == contentContract.Digest)));

        Assert.IsTrue(snapshots.All(s => s.Snapshot.Artifacts.All(artifact => artifact.Content?.Document is not null)));
        Assert.HasCount(1, await factory.Services.GetRequiredService<DataSourceAcquisitionService>().ListAsync(input.Value.Namespace, input.Value.Name, Owner, default));
        var acquisition = (await factory.Services.GetRequiredService<DataSourceAcquisitionService>().ListAsync(input.Value.Namespace, input.Value.Name, Owner, default)).Single();
        Assert.IsNotNull(acquisition.Publication?.Document, "JSON acquisition must itself be retained in the selected document Storage.");
        var acquired = await factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadAsync(acquisition, 0, 65536, default);
        Assert.AreEqual(acquisition.Content!.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(acquired.ContentBase64))));
    }
}
