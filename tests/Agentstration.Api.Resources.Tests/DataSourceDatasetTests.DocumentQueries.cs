using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Storage;
using Agentstration.Tools.Contracts;
using Agentstration.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    public async Task AcquisitionQueriesAndToolsDistinguishLatestExactAndMutableCollectionTargets()
    {
        await using var factory = Factory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" }))
            .ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<IStorageDocumentGateway>(sp =>
                new PagedAcquisitionGateway(ActivatorUtilities.CreateInstance<StorageDocumentGateway>(sp))))));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        await using var storage = await StructuredStorageFixture.StartAsync(factory.Services, client, Owner, false,
            StructuredStorageFixture.Json("""{"type":"object","properties":{"dataset":{"type":"string"}}}"""));
        var desired = Source("queryable-dataset", fixture, JsonSerializer.SerializeToElement(new { dataset = "alpha" }));
        desired = desired with { Definition = desired.Definition with { Dataset = desired.Definition.Dataset! with
        { Storage = storage.Reference, DocumentCollectionUid = storage.Collection,
            Lifecycle = new() { KeepHistory = true, HistoryRetention = TimeSpan.FromDays(1) } } } };
        var management = factory.Services.GetRequiredService<DataSourceManagementService>();
        var source = await management.CreateAsync(desired, default);
        var a = await AcquireAsync(client, source.Value.Name);
        var prefix = $"/api/datasourceacquisitions/{a.Name}/documents";
        var description = (await client.GetFromJsonAsync<DataSourceDocumentDescription>(prefix + "/capabilities"))!;
        Assert.AreEqual(a.Uid, description.AcquisitionUid);
        Assert.AreEqual(a.Publication!.Document!.Publication.Publication.Id, description.PublicationId);
        var profile = description.Selection.Contract.Profile;
        using var queried = await client.PostAsJsonAsync(prefix + "/query", new DataSourceDocumentQueryRequest { Profile = profile, Query = Empty });
        Assert.AreEqual(HttpStatusCode.OK, queried.StatusCode, await queried.Content.ReadAsStringAsync());
        var result = (await queried.Content.ReadFromJsonAsync<DataSourceDocumentQueryResult>())!;
        Assert.AreEqual("alpha", result.Page.Documents.Single().Value.GetProperty("dataset").GetString());
        Assert.AreEqual(a.Name, result.AcquisitionId);
        var gateway = factory.Services.GetRequiredService<IStorageDocumentGateway>();
        var current = await gateway.QueryAsync(storage.Target(Owner), new("current", profile, Empty), default);
        Assert.HasCount(0, current.Documents, "The mutable collection must remain distinct from acquisition publications.");

        async Task<ResourceReference> Publish(string mode)
        {
            using var response = await client.PostAsJsonAsync(prefix + "/tools", new PublishDataSourceDocumentToolRequest("1.0.0", profile, mode));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ResourceReference>())!;
        }
        var dynamicTool = await Publish(DataSourceDocumentSelectionModes.Latest);
        var fixedTool = await Publish(DataSourceDocumentSelectionModes.Exact);
        // The production fixture stores one envelope document. Simulate a paginated peer to exercise
        // acquisition selection independently from the provider's one-document pagination behavior.
        var paged = (PagedAcquisitionGateway)gateway;
        paged.PaginateNext = true;
        var access = factory.Services.GetRequiredService<DataSourceDocumentAccessService>();
        var dynamicSelection = description.Selection with { Mode = DataSourceDocumentSelectionModes.Latest, AcquisitionId = null, AcquisitionUid = null };
        var firstPage = await access.QueryAsync(dynamicSelection, new() { Profile = profile, Query = Empty, PageSize = 1 }, default);
        Assert.IsNotNull(firstPage.NextCursor);
        Assert.IsNull(firstPage.Page.Cursor, "The peer cursor must not escape the application envelope.");
        var leased = (await factory.Services.GetRequiredService<DataSourceAcquisitionService>().GetAsync(a.Namespace, a.Name, default)).Value;
        Assert.IsTrue(leased.ProjectionLeases.Values.Any(expiry => expiry == firstPage.CursorExpiresAt));
        var invalidCursor = await Assert.ThrowsAsync<DataSourceValidationException>(() => access.QueryAsync(dynamicSelection,
            new() { Profile = profile, Cursor = "tampered" }, default));
        Assert.AreEqual("data_source_document_cursor_invalid", invalidCursor.Code);
        var changedQuery = await Assert.ThrowsAsync<DataSourceValidationException>(() => access.QueryAsync(dynamicSelection,
            new() { Profile = profile, Cursor = firstPage.NextCursor, Query = Empty }, default));
        Assert.AreEqual("data_source_document_cursor_parameters", changedQuery.Code);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context with
        { Restriction = new(Guid.NewGuid(), fixture.Context.WorkspaceId, new HashSet<string> { AuthorizationPermissions.RunsRead }) }))
        {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => access.QueryAsync(dynamicSelection,
                new() { Profile = profile, Cursor = firstPage.NextCursor }, default));
        }
        var changed = await management.PutAsync(source.Value.Namespace, source.Value.Name, Owner,
            source.Value.Definition with { Dataset = source.Value.Definition.Dataset! with
            { Acquisition = source.Value.Definition.Dataset.Acquisition with { Parameters = JsonSerializer.SerializeToElement(new { dataset = "beta" }) } } }, source.ETag, default);
        var b = await AcquireAsync(client, source.Value.Name);
        Assert.AreNotEqual(a.Uid, b.Uid);
        var secondPage = await access.QueryAsync(dynamicSelection, new() { Profile = profile, Cursor = firstPage.NextCursor }, default);
        Assert.AreEqual(a.Uid, secondPage.AcquisitionUid, "Continuation must remain on A after B becomes latest.");
        Assert.AreEqual("alpha", secondPage.Page.Documents.Single().Value.GetProperty("dataset").GetString());
        Assert.IsNull(secondPage.NextCursor);
        var released = (await factory.Services.GetRequiredService<DataSourceAcquisitionService>().GetAsync(a.Namespace, a.Name, default)).Value;
        Assert.HasCount(0, released.ProjectionLeases);
        var replayed = await Assert.ThrowsAsync<DataSourceValidationException>(() => access.QueryAsync(dynamicSelection,
            new() { Profile = profile, Cursor = firstPage.NextCursor }, default));
        Assert.AreEqual("data_source_document_cursor_expired", replayed.Code);
        async Task<DataSourceDocumentQueryResult> Run(ResourceReference tool, object request)
        {
            using var response = await client.PostAsJsonAsync($"/api/tools/{tool.Name}/run?scopeRef=/instance",
                new RunToolRequest(JsonSerializer.SerializeToElement(new { request }), ToolRunMode.Execute));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var run = (await response.Content.ReadFromJsonAsync<RunToolResponse>())!;
            Assert.AreEqual("completed", run.Status, JsonSerializer.Serialize(run));
            return run.Output!.Value.Deserialize<DataSourceDocumentQueryResult>(JsonSerializerOptions.Web)!;
        }
        var latest = await Run(dynamicTool, new { query = Empty });
        Assert.AreEqual(b.Uid, latest.AcquisitionUid);
        Assert.AreEqual("beta", latest.Page.Documents.Single().Value.GetProperty("dataset").GetString());
        var exact = await Run(fixedTool, new { query = Empty });
        Assert.AreEqual(a.Uid, exact.AcquisitionUid);
        Assert.AreEqual("alpha", exact.Page.Documents.Single().Value.GetProperty("dataset").GetString());
        var specified = await Run(dynamicTool, new { query = Empty, acquisitionId = a.Name });
        Assert.AreEqual(a.Uid, specified.AcquisitionUid);
        using var overrideFixed = await client.PostAsJsonAsync($"/api/tools/{fixedTool.Name}/run?scopeRef=/instance",
            new RunToolRequest(JsonSerializer.SerializeToElement(new { request = new { query = Empty, acquisitionId = b.Name } }), ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, overrideFixed.StatusCode);
        using var physicalOverride = await client.PostAsJsonAsync(prefix + "/query", new
        { profile, query = Empty, target = storage.Target(Owner) });
        Assert.AreEqual(HttpStatusCode.BadRequest, physicalOverride.StatusCode);
        using var wrongProfile = await client.PostAsJsonAsync(prefix + "/query", new DataSourceDocumentQueryRequest
        { Profile = profile with { Version = "unavailable" }, Query = Empty });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, wrongProfile.StatusCode);
        SelectConsoleContext(client, ResourceScopeRef.Workspace((await GetBootstrapContextAsync(factory)).WorkspaceId));
        using var foreign = await client.GetAsync(prefix + "/capabilities");
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.IsNotNull(changed);
    }
    [TestMethod]
    public async Task DynamicAcquisitionToolsRejectChangedParentVersionsEvenWhenDefinitionsAreIdentical()
    {
        await using var factory = Factory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" })));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        var schema = StructuredStorageFixture.Json("""{"type":"object","properties":{"dataset":{"type":"string"}}}""");
        var schemas = factory.Services.GetRequiredService<SchemaManagementService>();
        var parent = await schemas.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = SchemaResourceKinds.Schema, ScopeRef = Owner,
            Metadata = new() { Name = "query-contract" },
            Definition = new() { DisplayName = "Query contract", Format = SchemaFormatIds.JsonSchema, Content = schema }
        }, default);
        var versionA = await schemas.PublishAsync(Owner, ResourceNamespace.Default, parent.Value.Name, "A", default);
        var versionB = await schemas.PublishAsync(Owner, ResourceNamespace.Default, parent.Value.Name, "B", default);
        Assert.AreEqual(versionA.Value.DefinitionHash, versionB.Value.DefinitionHash);
        SchemaBinding Binding(string version) => new() { Reference = new() { Name = parent.Value.Name, ScopeRef = Owner, Version = version } };
        await using var storageA = await StructuredStorageFixture.StartAsync(factory.Services, client, Owner, false, schema, Binding("A"));
        var storageB = (await factory.Services.GetRequiredService<StorageManagementService>().CreateAsync(storageA.Storage with
        {
            Metadata = new() { Name = "version-b-documents" },
            Definition = storageA.Storage.Definition with { Collections = storageA.Storage.Definition.Collections.Select(collection =>
                collection.Name == "shared" ? collection with { SchemaBinding = Binding("B"), SchemaSnapshot = null } : collection).ToArray() }
        }, default)).Value;
        var referenceB = new StorageRevisionReference(new(Owner, storageB.Namespace, storageB.Kind, storageB.Name),
            storageB.Uid, storageB.Generation, StorageDigests.Binding(storageB.Definition));
        var management = factory.Services.GetRequiredService<DataSourceManagementService>();
        var desired = Source("versioned-query", fixture, JsonSerializer.SerializeToElement(new { dataset = "original" }));
        desired = desired with { Definition = desired.Definition with { Dataset = desired.Definition.Dataset! with
        { Storage = storageA.Reference, DocumentCollectionUid = storageA.Collection,
            Lifecycle = new() { KeepHistory = true, HistoryRetention = TimeSpan.FromDays(1) } } } };
        var source = await management.CreateAsync(desired, default);
        var a = await AcquireAsync(client, source.Value.Name);
        var access = factory.Services.GetRequiredService<DataSourceDocumentAccessService>();
        var exact = (await access.DescribeAsync(ResourceNamespace.Default, a.Name, default)).Selection;
        var latest = exact with { Mode = DataSourceDocumentSelectionModes.Latest, AcquisitionId = null, AcquisitionUid = null };
        Assert.AreEqual("A", exact.Contract.SchemaReference!.Version);
        var changed = await management.PutAsync(source.Value.Namespace, source.Value.Name, Owner, source.Value.Definition with
        { Dataset = source.Value.Definition.Dataset! with { Storage = referenceB, DocumentCollectionUid = storageB.Definition.Collections.Single(c => c.Name == "shared").Uid } }, source.ETag, default);
        var b = await AcquireAsync(client, source.Value.Name);
        var bDescription = await access.DescribeAsync(ResourceNamespace.Default, b.Name, default);
        Assert.AreEqual("B", bDescription.Selection.Contract.SchemaReference!.Version);
        Assert.AreEqual(exact.Contract.Profile, bDescription.Selection.Contract.Profile);
        var rejected = await Assert.ThrowsAsync<DataSourceValidationException>(() => access.QueryAsync(latest,
            new() { Profile = exact.Contract.Profile, Query = Empty }, default));
        Assert.AreEqual("data_source_document_contract_mismatch", rejected.Code, "Latest must fail instead of falling back to compatible acquisition A.");
        var retained = await access.QueryAsync(exact, new() { Profile = exact.Contract.Profile, Query = Empty }, default);
        Assert.AreEqual(a.Uid, retained.AcquisitionUid);
        var selected = await access.QueryAsync(latest, new() { Profile = exact.Contract.Profile, Query = Empty, AcquisitionId = a.Name }, default);
        Assert.AreEqual(a.Uid, selected.AcquisitionUid);
        var replaced = await Assert.ThrowsAsync<DataSourceValidationException>(() => access.QueryAsync(exact with { SourceUid = Guid.NewGuid() },
            new() { Profile = exact.Contract.Profile, Query = Empty }, default));
        Assert.AreEqual("data_source_document_source_unavailable", replaced.Code);
        _ = await management.PutAsync(changed.Value.Namespace, changed.Value.Name, Owner, changed.Value.Definition with { Enabled = false }, changed.ETag, default);
        var disabled = await Assert.ThrowsAsync<DataSourceValidationException>(() => access.QueryAsync(exact,
            new() { Profile = exact.Contract.Profile, Query = Empty }, default));
        Assert.AreEqual("data_source_document_source_unavailable", disabled.Code);
    }

    private sealed class PagedAcquisitionGateway(IStorageDocumentGateway inner) : IStorageDocumentGateway
    {
        public bool PaginateNext { get; set; }
        public async Task<StorageDocumentPage> QueryAsync(StorageDocumentTarget target, StorageDocumentQuery request, CancellationToken ct)
        {
            var page = await inner.QueryAsync(target, request with { Cursor = null }, ct);
            if (!PaginateNext) return page;
            PaginateNext = false;
            return page with { Cursor = "fixture-peer-page-2" };
        }
        public Task<StorageDocumentDescription> DescribeAsync(StorageDocumentTarget target, CancellationToken ct) => inner.DescribeAsync(target, ct);
        public Task<StorageDocumentValue> GetAsync(StorageDocumentTarget target, string id, string? revision, CancellationToken ct) => inner.GetAsync(target, id, revision, ct);
        public Task<StorageDocumentValue> PutAsync(StorageDocumentTarget target, StorageDocumentWrite request, CancellationToken ct) => inner.PutAsync(target, request, ct);
        public Task<StorageDocumentValue> DeleteAsync(StorageDocumentTarget target, StorageDocumentDelete request, CancellationToken ct) => inner.DeleteAsync(target, request, ct);
        public Task<StorageDocumentPublicationReference> PrepareAsync(StorageDocumentTarget target, string id, IReadOnlyList<StorageDocumentValue> documents, string requestId, DateTimeOffset notAfter, CancellationToken ct) => inner.PrepareAsync(target, id, documents, requestId, notAfter, ct);
        public Task SealAsync(StorageDocumentPublicationReference reference, CancellationToken ct) => inner.SealAsync(reference, ct);
        public Task ValidatePublicationAsync(StorageDocumentPublicationReference reference, CancellationToken ct) => inner.ValidatePublicationAsync(reference, ct);
        public Task RetireAsync(StorageDocumentPublicationReference reference, CancellationToken ct) => inner.RetireAsync(reference, ct);
    }

}
