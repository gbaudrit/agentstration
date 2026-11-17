using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Api.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    private static readonly ResourceScopeRef StorageScope = ResourceScopeRef.Tenant(Guid.Parse("10460000-0000-0000-0000-000000000006"));
    private static readonly ScopedResourceAddress StorageAddress = new(StorageScope, ResourceNamespace.Parse("records"), StorageResourceKinds.Storage, "documents");

    [TestMethod]
    public async Task CollectionSchemaErrorsRetainFieldPathsForLocalizedConsoleDiagnostics()
    {
        using var handler = new StubHandler(_ => new(HttpStatusCode.UnprocessableEntity)
        {
            Content = JsonContent.Create(new
            {
                title = "storage_collection_schema_invalid",
                validationIssues = new[]
            { new { path = "/definition/collections/0/schema/properties/date/format", code = "unsupported_schema", keyword = "format" } }
            })
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        var error = await Assert.ThrowsAsync<AgentstrationApiException>(() => new StoragesApiClient(http).GetAsync(StorageAddress, default));
        Assert.AreEqual("storage_collection_schema_invalid", error.ProblemTitle);
        Assert.AreEqual("/definition/collections/0/schema/properties/date/format", error.ValidationIssues.Single().Path);
        Assert.AreEqual("format", error.ValidationIssues.Single().Keyword);
    }

    [TestMethod]
    public async Task StorageCatalogFollowsPaginationWithoutFollowingUntrustedNextLink()
    {
        var calls = 0;
        using var handler = new StubHandler(request =>
        {
            calls++;
            Assert.AreEqual("/api/storage-types", request.RequestUri!.AbsolutePath);
            StringAssert.Contains(request.RequestUri.Query, $"scopeRef={Uri.EscapeDataString(StorageScope.Value)}");
            StringAssert.Contains(request.RequestUri.Query, $"skip={(calls - 1) * 100}");
            var type = new StorageTypeResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = StorageResourceKinds.StorageType, Metadata = new() { Name = $"type-{calls}" }, ScopeRef = StorageScope, Definition = new() { DisplayName = "Test type", PreferredVersion = "1" } };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new PagedResponse<StorageTypeResource>([type], calls == 1 ? "https://untrusted.invalid/steal" : null)) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        var values = await new StoragesApiClient(http).GetTypesAsync(StorageScope, default);
        Assert.HasCount(2, values);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    [DataRow("get")]
    [DataRow("update")]
    [DataRow("reconcile")]
    [DataRow("deactivate")]
    public async Task StorageClientPreservesQualifiedIdentityAndEtags(string action)
    {
        using var handler = new StubHandler(request =>
        {
            var suffix = action is "get" or "update" ? "" : $"/{action}";
            Assert.AreEqual($"/api/storages/documents{suffix}", request.RequestUri!.AbsolutePath);
            StringAssert.Contains(request.RequestUri.Query, "namespace=records");
            StringAssert.Contains(request.RequestUri.Query, Uri.EscapeDataString(StorageScope.Value));
            if (action != "get") Assert.AreEqual("\"old\"", request.Headers.IfMatch.Single().ToString());
            if (action == "reconcile")
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("properties").ValueKind);
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(StorageClientResource()), Headers = { ETag = new EntityTagHeaderValue("\"new\"") } };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        var client = new StoragesApiClient(http);
        var result = action switch
        {
            "get" => await client.GetAsync(StorageAddress, default),
            "update" => await client.UpdateAsync(StorageAddress, new(StorageClientResource().Definition), "\"old\"", default),
            "reconcile" => await client.ReconcileAsync(StorageAddress, new(), "\"old\"", default),
            _ => await client.DeactivateAsync(StorageAddress, "\"old\"", default)
        };
        Assert.AreEqual("\"new\"", result.ETag);
        Assert.AreEqual(StorageAddress.Name, result.Value.Name);
    }

    [TestMethod]
    public async Task StorageClientSendsExplicitCompleteReplacementAndNoResolvedValues()
    {
        var properties = StorageClientResource().Definition;
        using var handler = new StubHandler(request =>
        {
            var body = request.Content!.ReadFromJsonAsync<ReconcileStorageRequest>().GetAwaiter().GetResult();
            Assert.IsNotNull(body?.Properties);
            Assert.AreEqual(properties.Type.TypeUid, body.Properties.Type.TypeUid);
            Assert.AreEqual(properties.Type.ContractDigest, body.Properties.Type.ContractDigest);
            Assert.AreEqual("{}", body.Properties.Envelope.GetRawText());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(StorageClientResource()), Headers = { ETag = new EntityTagHeaderValue("\"new\"") } };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        await new StoragesApiClient(http).ReconcileAsync(StorageAddress, new(properties), "\"old\"", default);
    }

    [TestMethod]
    public async Task StorageClientRejectsResourceResponsesWithoutConcurrencyToken()
    {
        using var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(StorageClientResource()) });
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        await Assert.ThrowsAsync<AgentstrationApiException>(() => new StoragesApiClient(http).GetAsync(StorageAddress, default));
    }

    [TestMethod]
    public async Task StorageClientRetainsProblemCodeForLocalizedPresentation()
    {
        using var handler = new StubHandler(_ => new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { title = "storage_service_stale", detail = "INTERNAL DIAGNOSTIC", code = "storage_service_stale" }) });
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        var error = await Assert.ThrowsAsync<AgentstrationApiException>(() => new StoragesApiClient(http).GetAsync(StorageAddress, default));
        Assert.AreEqual("storage_service_stale", error.ProblemTitle);
        Assert.AreEqual(HttpStatusCode.Conflict, error.StatusCode);
    }

    [TestMethod]
    public async Task StorageClientReadsWrappedDependenciesAndScopedList()
    {
        using var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/usages", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = JsonContent.Create(new ValueResponse<StorageUsage>([new("DataSource", "source", "Source", "/data-sources/source")])) }
            : new(HttpStatusCode.OK) { Content = JsonContent.Create(new PagedResponse<StorageResource>([StorageClientResource()], null)) });
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        var client = new StoragesApiClient(http);
        Assert.HasCount(1, await client.GetStoragesAsync(StorageScope, default));
        var usages = await client.GetUsagesAsync(StorageAddress, default);
        Assert.AreEqual("DataSource", usages.Single().Kind);
    }

    private static StorageResource StorageClientResource() => new()
    {
        Uid = Guid.Parse("10460000-0000-0000-0000-000000000007"),
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = StorageResourceKinds.Storage,
        Metadata = new() { Name = StorageAddress.Name, Namespace = StorageAddress.Namespace },
        ScopeRef = StorageScope,
        Definition = new()
        {
            DisplayName = "Documents",
            Type = new() { Type = new("local", ResourceScopeRef.Instance), TypeUid = Guid.Parse("10460000-0000-0000-0000-000000000008"), Version = "1", SchemaDigest = "schema", ContractDigest = "contract" },
            Configuration = JsonSerializer.SerializeToElement(new { }),
            Envelope = JsonSerializer.SerializeToElement(new { })
        }
    };
}
