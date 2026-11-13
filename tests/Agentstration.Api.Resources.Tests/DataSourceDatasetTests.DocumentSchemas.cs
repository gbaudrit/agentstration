using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.DataSources;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.Storage.Contracts;
using Agentstration.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    public async Task CollectionManagementAcceptsLocalSchemaDefinitionsAndReportsPreciseUnsupportedFields()
    {
        await using var factory = Factory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" })));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var inputs = await PrepareAsync(factory.Services);
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(inputs.Context);
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        await using var storage = await StructuredStorageFixture.StartAsync(factory.Services, client, Owner, false);
        var schema = StructuredStorageFixture.Json(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nantes-alerts.schema.json")));
        var properties = storage.Storage.Definition with
        { Collections = [storage.Storage.Definition.Collections.First() with { Schema = schema, Uid = Guid.Empty }] };
        using var created = await client.PostAsJsonAsync("/api/storages?scopeRef=/instance", new CreateStorageRequest("alerts-schema", properties, Owner));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var saved = (await created.Content.ReadFromJsonAsync<StorageResource>())!;
        Assert.IsTrue(saved.Definition.Collections.Single().Schema.GetProperty("$defs").TryGetProperty("alerte", out _), "Keep the original schema rather than replacing it with an expanded query model.");
        properties = properties with
        { Collections = [properties.Collections.Single() with { Schema = StructuredStorageFixture.Json("""{"type":"object","properties":{"bad":{"type":"string","format":"unsupported"}}}""") }] };
        using var refused = await client.PostAsJsonAsync("/api/storages?scopeRef=/instance", new CreateStorageRequest("invalid-schema", properties, Owner));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.AreEqual("storage_collection_schema_invalid", problem.RootElement.GetProperty("title").GetString());
        var issue = problem.RootElement.GetProperty("validationIssues")[0];
        Assert.AreEqual("/definition/collections/0/schema/properties/bad/format", issue.GetProperty("path").GetString());
        Assert.AreEqual("format", issue.GetProperty("keyword").GetString());
        storage.Bindings.UseWrongDocumentSchema = true;
        var collection = saved.Definition.Collections.Single();
        var target = new StorageDocumentTarget(new(new(Owner, saved.Namespace, saved.Kind, saved.Name), saved.Uid,
            saved.Generation, StorageDigests.Binding(saved.Definition)), new(Owner, saved.Address), collection.Uid, collection.Policy);
        var response = StructuredStorageFixture.Json(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nantes-alerts-response.json")));
        var mismatch = await Assert.ThrowsAsync<StorageException>(() => factory.Services.GetRequiredService<IStorageDocumentGateway>().PutAsync(
            target, new("schema-mismatch", DateTimeOffset.UtcNow.AddMinutes(2), "alerts", response), default));
        Assert.AreEqual("storage_documents_invalid_result", mismatch.Code);
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AcquiredResponseEnvelopePublishesAndQueriesNestedAlertsWithoutAnInventedSplit(bool postgres)
    {
        await using var factory = Factory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" })));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var response = StructuredStorageFixture.Json(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nantes-alerts-response.json")));
        var schema = StructuredStorageFixture.Json(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nantes-alerts.schema.json")));
        var inputs = await PrepareAsync(factory.Services, operationResult: response);
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(inputs.Context);
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        await using var storage = await StructuredStorageFixture.StartAsync(factory.Services, client, Owner, postgres, schema);
        var source = Source("alerts-response", inputs, JsonSerializer.SerializeToElement(new { dataset = "alerts" }));
        source = source with
        {
            Definition = source.Definition with
            { Dataset = source.Definition.Dataset! with { Storage = storage.Reference, DocumentCollectionUid = storage.Collection } }
        };
        var saved = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(source, default);
        var acquisition = await AcquireAsync(client, saved.Value.Name);
        Assert.IsNotNull(acquisition.Publication?.Document);
        var bytes = await factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadAsync(acquisition, 0, 65536, default);
        Assert.IsTrue(JsonElement.DeepEquals(response, JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(bytes.ContentBase64))));
        var knowledge = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new()
        {
            ApiVersion = source.ApiVersion,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            ScopeRef = Owner,
            Metadata = new() { Name = "alerts-response-knowledge" },
            Definition = new()
            {
                DisplayName = "Alerts response",
                Output = new(storage.Reference, new(Write: true)) { DocumentCollectionUid = storage.Collection },
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                DataSources = [new() { Name = "alerts", DataSource = new(saved.Value.Name, Owner), RefreshIfNecessary = true, MaximumAge = TimeSpan.FromHours(1) }]
            }
        }, default);
        var id = new KnowledgeSourceId(knowledge.Value.Name, ScopeRef: Owner);
        var access = factory.Services.GetRequiredService<KnowledgeAccessService>();
        var description = await access.DescribeDocumentsAsync(id, null, default);
        var query = new KnowledgeDocumentQueryRequest
        {
            Profile = description.QueryProfiles.Single(),
            Query = StructuredStorageFixture.Json("""{"$filter":"results/any(a:a/status eq 'active')"}""")
        };
        var result = await AwaitNativeAsync(await access.QueryDocumentsAsync(id, query, default),
            pending => access.QueryDocumentsAsync(id, query with { PreparationId = pending.PreparationId }, default));
        Assert.AreEqual("ready", result.State);
        Assert.HasCount(1, result.DocumentPage!.Documents);
        Assert.IsTrue(JsonElement.DeepEquals(response, result.DocumentPage.Documents.Single().Value));
    }

}
