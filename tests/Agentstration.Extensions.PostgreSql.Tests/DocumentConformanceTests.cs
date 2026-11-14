using System.Net.Http.Headers;
using System.Text.Json;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Agentstration.Extensions.PostgreSql;
using Asp.Samples.StructuredDocuments;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;

namespace Agentstration.Extensions.PostgreSql.Tests;

[TestClass]
public sealed partial class DocumentConformanceTests
{
    internal static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    internal static JsonElement Schema => Json("""
        {"type":"object","properties":{"status":{"type":"string"},"service":{"type":"string"},
        "timestamp":{"type":"string"},"metrics":{"type":"object","properties":{"errorRate":{"type":"number"}}},
        "measurements":{"type":"array","items":{"type":"object","properties":{"value":{"type":"number"}}}}}}
        """);
    private static async Task Error(string code, Func<Task> run) => Assert.AreEqual(code, (await Assert.ThrowsAsync<AspException>(run)).Code);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealMcpCreateReplaceDeleteReplayAndTypedSchema(bool postgres)
    {
        await using var host = await Host.StartAsync(postgres);
        var write = host.Put("doc", """{"status":"degraded","metrics":{"errorRate":0.2}}""");
        var first = await host.Client.PutAsync(write); var replay = await host.Client.PutAsync(write);
        Assert.AreEqual(first.Revision, replay.Revision);
        await Error("idempotency_conflict", () => host.Client.PutAsync(write with { Value = Json("{}") }));
        await Error("conflict", () => host.Client.PutAsync(write with { RequestId = "other-create" }));
        var second = await host.Client.PutAsync(host.Put("doc", """{"status":"healthy"}""") with { ExpectedRevision = first.Revision });
        Assert.AreNotEqual(first.Revision, second.Revision);
        await Error("version_unavailable", () => host.Client.GetAsync(new("old", "doc", first.Revision)));
        await Error("schema_validation", () => host.Client.PutAsync(host.Put("wrong", """{"metrics":{"errorRate":"0.2"}}""")));
        var delete = new AspDocumentDelete("delete", DateTimeOffset.UtcNow.AddMinutes(2), "doc", second.Revision);
        Assert.AreEqual(second.Revision, (await host.Client.DeleteAsync(delete)).Revision);
        Assert.AreEqual(second.Revision, (await host.Client.DeleteAsync(delete)).Revision);
        await Error("not_found", () => host.Client.GetAsync(new("missing", "doc")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StandardFiltersSortTiesPaginationNullAndParameterizedStrings(bool postgres)
    {
        await using var host = await Host.StartAsync(postgres); await Seed(host);
        var request = host.Query("""{"$filter":"status eq 'degraded' and metrics/errorRate gt 0.1","$orderby":"timestamp desc"}""", 1);
        var first = await host.Client.QueryAsync(request); Assert.AreEqual("a", first.Documents.Single().Id); Assert.IsNotNull(first.Cursor);
        var second = await host.Client.QueryAsync(request with { RequestId = "second", Cursor = first.Cursor });
        Assert.AreEqual("b", second.Documents.Single().Id); Assert.IsNull(second.Cursor);
        var nulls = await host.Client.QueryAsync(host.Query("""{"$filter":"optional eq null"}""")); Assert.HasCount(3, nulls.Documents);
        var injected = await host.Client.QueryAsync(host.Query("""{"$filter":"status eq 'degraded'' OR true--'"}""")); Assert.IsEmpty(injected.Documents);
        var selected = await host.Client.QueryAsync(host.Query("""{"$select":"status,metrics/errorRate","$filter":"status eq 'healthy'"}"""));
        Assert.IsFalse(selected.Documents.Single().Value.TryGetProperty("timestamp", out _));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BoundedArraysAndGroupingUseTheSameStandardSemantics(bool postgres)
    {
        await using var host = await Host.StartAsync(postgres); await Seed(host);
        var any = await host.Client.QueryAsync(host.Query("""{"$filter":"measurements/any(m:m/value gt 10)"}"""));
        Assert.AreEqual("a", any.Documents.Single().Id);
        Assert.HasCount(2, (await host.Client.QueryAsync(host.Query("""{"$filter":"measurements/any()"}"""))).Documents);
        Assert.HasCount(3, (await host.Client.QueryAsync(host.Query("""{"$filter":"measurements/all(m:m/value gt 1)"}"""))).Documents);
        Assert.HasCount(2, (await host.Client.QueryAsync(host.Query("""{"$filter":"metrics/errorRate in (0.2,0.3)"}"""))).Documents);
        Assert.HasCount(2, (await host.Client.QueryAsync(host.Query("""{"$filter":"contains(status,'degr') and startswith(service,'a') and endswith(service,'i')"}"""))).Documents);
        await Error("unsupported_query", () => host.Client.QueryAsync(host.Query("""{"$filter":"measurements/any(m:m/value gt $it/metrics/errorRate)"}""")));
        var aggregate = await host.Client.QueryAsync(host.Query("""{"$apply":"groupby((service),aggregate($count as Count,metrics/errorRate with sum as Total,metrics/errorRate with min as Minimum,metrics/errorRate with max as Maximum))"}"""));
        Assert.HasCount(2, aggregate.Groups);
        var api = aggregate.Groups.Single(g => g.GetProperty("service").GetString() == "api");
        Assert.AreEqual(2, api.GetProperty("Count").GetInt32()); Assert.AreEqual(0.5m, api.GetProperty("Total").GetDecimal());
        Assert.AreEqual(0.2m, api.GetProperty("Minimum").GetDecimal()); Assert.AreEqual(0.3m, api.GetProperty("Maximum").GetDecimal());
        await host.Client.PutAsync(host.Put("decimal-a", """{"metrics":{"errorRate":1.0}}"""));
        await host.Client.PutAsync(host.Put("decimal-b", """{"metrics":{"errorRate":1.00}}"""));
        var equivalentNumbers = await host.Client.QueryAsync(host.Query("""{"$filter":"metrics/errorRate eq 1","$apply":"groupby((metrics/errorRate),aggregate($count as Count))"}"""));
        Assert.HasCount(1, equivalentNumbers.Groups);
        Assert.AreEqual(2, equivalentNumbers.Groups.Single().GetProperty("Count").GetInt32());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CursorBindsProfileQueryTargetAndCurrentAuthorization(bool postgres)
    {
        await using var host = await Host.StartAsync(postgres); await Seed(host);
        var query = host.Query("{}", 1); var first = await host.Client.QueryAsync(query);
        await Error("invalid_reference", () => host.Client.QueryAsync(query with { Query = Json("""{"$filter":"status eq 'healthy'"}"""), Cursor = first.Cursor }));
        await Error("unsupported_capability", () => host.Client.QueryAsync(query with { Profile = query.Profile with { Encoding = "other" }, Cursor = first.Cursor }));
        host.Access.Revision = "revoked-prior-grant";
        await Error("invalid_reference", () => host.Client.QueryAsync(query with { Cursor = first.Cursor }));
        host.Access.Enabled = false;
        await Error("forbidden", () => host.Client.QueryAsync(query));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SealedHistoricalPublicationSurvivesNewPublicationAndRefusesAllMutation(bool postgres)
    {
        await using var host = await Host.StartAsync(postgres);
        var prepare = new AspDocumentPrepare("prepare-a", DateTimeOffset.UtcNow.AddMinutes(2), "publication-a",
            [new("first", Json("""{"status":"degraded"}"""))]);
        var a = await host.Client.PrepareAsync(prepare); var seal = new AspDocumentPublicationRequest("seal-a", a.Id, a.Digest);
        var historical = await AspDocumentClient.ConnectAsync(host.Mcp, "aspdoc", StructuredSampleAccess.Store, new("sample", a.Id));
        await historical.SealAsync(seal); await historical.SealAsync(seal);
        await Error("forbidden", () => host.Client.SealAsync(seal));
        var b = await host.Client.PrepareAsync(prepare with { RequestId = "prepare-b", PublicationId = "publication-b", Documents = [new("second", Json("{}"))] });
        var latest = await AspDocumentClient.ConnectAsync(host.Mcp, "aspdoc", StructuredSampleAccess.Store, new("sample", b.Id));
        await latest.SealAsync(new("seal-b", b.Id, b.Digest));
        var result = await historical.QueryAsync(host.Query("{}")); Assert.AreEqual("first", result.Documents.Single().Id);
        await Error("forbidden", () => historical.PutAsync(host.Put("first", "{}")));
        await Error("forbidden", () => historical.DeleteAsync(new("remove", DateTimeOffset.UtcNow.AddMinutes(2), "first", a.Digest)));
        await Error("conflict", () => host.Client.PrepareAsync(prepare with { RequestId = "overwrite", Documents = [new("changed", Json("{}"))] }));
        Assert.AreEqual(a.Digest, (await historical.InspectAsync(seal)).Digest);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnsupportedSyntaxBoundedNumbersAndForeignCollectionsAreRefused(bool postgres)
    {
        await using var host = await Host.StartAsync(postgres);
        await Error("unsupported_query", () => host.Client.QueryAsync(host.Query("""{"$expand":"anything"}""")));
        await Error("unsupported_value", () => host.Client.PutAsync(host.Put("tiny", """{"number":1e-29}""")));
        await Error("invalid_request", () => host.Client.PutAsync(host.Put("duplicate", """{"status":"a","status":"b"}""")));
        await Error("forbidden", () => AspDocumentClient.ConnectAsync(host.Mcp, "aspdoc", StructuredSampleAccess.Store, new("other")));
        await Error("forbidden", () => AspDocumentClient.ConnectAsync(host.Mcp, "aspdoc", StructuredSampleAccess.Store with { StoreId = "foreign" }, new("sample")));
    }

    private static async Task Seed(Host host)
    {
        await host.Client.PutAsync(host.Put("a", """{"status":"degraded","service":"api","timestamp":"2026-11-12T12:00:00Z","metrics":{"errorRate":0.2},"measurements":[{"value":20}]}"""));
        await host.Client.PutAsync(host.Put("b", """{"status":"degraded","service":"api","timestamp":"2026-11-12T12:00:00Z","metrics":{"errorRate":0.3},"measurements":[{"value":5}]}"""));
        await host.Client.PutAsync(host.Put("c", """{"status":"healthy","service":"web","timestamp":"2026-11-12T11:00:00Z","metrics":{"errorRate":0.01},"measurements":[]}"""));
    }

    internal sealed class Host : IAsyncDisposable
    {
        public WebApplicationFactory<StructuredSampleAccess> Factory { get; private set; } = null!;
        public McpClient Mcp { get; private set; } = null!;
        public AspDocumentClient Client { get; private set; } = null!;
        public StructuredSampleAccess Access => Factory.Services.GetRequiredService<StructuredSampleAccess>();
        public static async Task<Host> StartAsync(bool postgres, IAspDocumentProvider? fixture = null, string? profileId = null, JsonElement? schema = null)
        {
            IAspDocumentProvider provider = fixture ?? new MemoryDocumentProvider(schema ?? Schema, TimeProvider.System);
            if (postgres)
            {
                var connection = Environment.GetEnvironmentVariable("AGENTSTRATION_TEST_ASP_POSTGRES");
                if (string.IsNullOrWhiteSpace(connection)) Assert.Inconclusive("Set AGENTSTRATION_TEST_ASP_POSTGRES for the dedicated real PostgreSQL lane.");
                var postgresProvider = new PostgreSqlDocumentProvider(connection, schema ?? Schema); await postgresProvider.InitializeAsync(CancellationToken.None);
                // Each case gets a unique physical StoreId via a provider-only partition wrapper.
                provider = new PartitionProvider(postgresProvider, Guid.NewGuid().ToString("N"));
            }
            var host = new Host();
            host.Factory = new WebApplicationFactory<StructuredSampleAccess>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            { services.RemoveAll<IAspDocumentProvider>(); services.AddSingleton(provider); }));
            var http = host.Factory.CreateClient();
            if (profileId is not null) host.Access.Policy = host.Access.Policy with { QueryProfiles = [profileId] };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.Access.Token);
            host.Mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new("http://localhost/mcp") }, http));
            host.Client = await AspDocumentClient.ConnectAsync(host.Mcp, "aspdoc", StructuredSampleAccess.Store, new("sample")); return host;
        }
        public AspDocumentPut Put(string id, string value) => new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(2), id, Json(value));
        public AspDocumentQuery Query(string query, int size = 50) => new(Guid.NewGuid().ToString("N"), Client.Description.QueryProfiles.Single().Reference, Json(query), size);
        public async ValueTask DisposeAsync() { if (Mcp is not null) await Mcp.DisposeAsync(); if (Factory is not null) await Factory.DisposeAsync(); }
    }

    private sealed class PartitionProvider(PostgreSqlDocumentProvider inner, string partition) : IAspDocumentProvider, IAspDocumentPublisher
    {
        private AspDocumentContext Context(AspDocumentContext c) => c with { Store = c.Store with { StoreId = partition + c.Store.StoreId } };
        public string ProviderId => inner.ProviderId; public string ProviderVersion => inner.ProviderVersion;
        public JsonElement ConfigurationSchema => inner.ConfigurationSchema; public JsonElement DocumentSchema => inner.DocumentSchema;
        public IReadOnlyList<AspQueryProfileDescriptor> QueryProfiles => inner.QueryProfiles;
        public ValueTask<AspDocument> GetAsync(AspDocumentContext c, AspDocumentGet r, CancellationToken ct) => inner.GetAsync(Context(c), r, ct);
        public ValueTask<AspDocument> PutAsync(AspDocumentContext c, AspDocumentPut r, CancellationToken ct) => inner.PutAsync(Context(c), r, ct);
        public ValueTask<AspDocumentDeleted> DeleteAsync(AspDocumentContext c, AspDocumentDelete r, CancellationToken ct) => inner.DeleteAsync(Context(c), r, ct);
        public ValueTask<AspDocumentProviderPage> QueryAsync(AspDocumentContext c, AspDocumentQuery r, string? p, CancellationToken ct) => inner.QueryAsync(Context(c), r, p, ct);
        public ValueTask<AspDocumentPublication> PrepareAsync(AspDocumentContext c, AspDocumentPrepare r, CancellationToken ct) => inner.PrepareAsync(Context(c), r, ct);
        public ValueTask<AspDocumentPublication> SealAsync(AspDocumentContext c, AspDocumentPublicationRequest r, CancellationToken ct) => inner.SealAsync(Context(c), r, ct);
        public ValueTask<AspDocumentPublication> InspectAsync(AspDocumentContext c, AspDocumentPublicationRequest r, CancellationToken ct) => inner.InspectAsync(Context(c), r, ct);
        public ValueTask RetireAsync(AspDocumentContext c, AspDocumentPublicationRequest r, CancellationToken ct) => inner.RetireAsync(Context(c), r, ct);
    }
}
