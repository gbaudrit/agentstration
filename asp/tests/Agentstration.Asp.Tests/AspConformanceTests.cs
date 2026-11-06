using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Asp.Samples.LocalStorage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Agentstration.Asp.Tests;

[TestClass]
public sealed class AspConformanceTests
{
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient http = null!;
    private McpClient mcp = null!;
    private SampleAccess access = null!;
    private AspClient client = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        factory = new();
        http = factory.CreateClient();
        access = factory.Services.GetRequiredService<SampleAccess>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new("http://localhost/mcp") }, http));
        client = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore);
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        if (mcp is not null) await mcp.DisposeAsync();
        http?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
    }

    [TestMethod]
    public async Task RealMcpRoundtripPreservesOpaqueJsonAndReadsRanges()
    {
        const string json = "{\"MiXeD_KEY\":{\"$opaque\":[null,42,\"été\"]}}";
        var written = await client.WriteAsync(Write("notes/a", json));
        var page = await client.ListAsync(new("list-1", "notes/", 10));
        Assert.HasCount(1, page.Objects);
        Assert.IsNull(page.Cursor);
        var stat = await client.StatAsync(new("stat-1", page.Objects[0].Reference));
        Assert.AreEqual(written.Item.Sha256, stat.Sha256);
        var read = await client.ReadAsync(new("read-1", stat.Reference, 0, 4096));
        Assert.AreEqual(json, Encoding.UTF8.GetString(Convert.FromBase64String(read.ContentBase64)));
        Assert.IsTrue(read.EndOfObject);
        var chunk = await client.ReadAsync(new("read-2", stat.Reference, 1, 3));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(json)[1..4], Convert.FromBase64String(chunk.ContentBase64));
        Assert.IsFalse(chunk.EndOfObject);
        var eof = await client.ReadAsync(new("read-3", stat.Reference, stat.Length, 10));
        Assert.AreEqual("", eof.ContentBase64);
        Assert.IsTrue(eof.EndOfObject);
    }

    [TestMethod]
    public async Task PaginationIsBoundedOrderedAndBoundToTheExactQuery()
    {
        foreach (var key in new[] { "notes/c", "notes/a", "notes/b" }) await client.WriteAsync(Write(key, key));
        var first = await client.ListAsync(new("page-1", "notes/", 1));
        Assert.AreEqual("notes/a", first.Objects.Single().Key);
        Assert.IsNotNull(first.Cursor);
        var second = await client.ListAsync(new("page-2", "notes/", 1, first.Cursor));
        Assert.AreEqual("notes/b", second.Objects.Single().Key);
        var third = await client.ListAsync(new("page-3", "notes/", 1, second.Cursor));
        Assert.AreEqual("notes/c", third.Objects.Single().Key);
        Assert.IsNull(third.Cursor);
        await ErrorAsync("invalid_reference", () => client.ListAsync(new("replay", "notes/", 2, first.Cursor)));
        await ErrorAsync("invalid_reference", () => client.ListAsync(new("replay", "notes/a", 1, first.Cursor)));
        await ErrorAsync("limit_exceeded", () => client.ListAsync(new("large", "notes/", 11)));
    }

    [TestMethod]
    public async Task SharedPhysicalProviderDoesNotShareLogicalStoreAuthority()
    {
        var second = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.SecondStore);
        var firstObject = await client.WriteAsync(Write("notes/shared", "first"));
        var secondObject = await second.WriteAsync(Write("notes/shared", "second"));
        await ErrorAsync("invalid_reference", () => second.ReadAsync(new("cross-read", firstObject.Item.Reference, 0, 100)));
        await ErrorAsync("invalid_reference", () => client.StatAsync(new("cross-stat", secondObject.Item.Reference)));
        var read = await second.ReadAsync(new("second-read", secondObject.Item.Reference, 0, 100));
        Assert.AreEqual("second", Encoding.UTF8.GetString(Convert.FromBase64String(read.ContentBase64)));
        await client.WriteAsync(Write("notes/z", "z"));
        var cursor = (await client.ListAsync(new("page", "notes/", 1))).Cursor;
        await ErrorAsync("invalid_reference", () => second.ListAsync(new("cross-cursor", "notes/", 1, cursor)));
        Assert.AreEqual(SampleAccess.SecondStore, second.Description.Store);
        Assert.HasCount(1, (await second.ListAsync(new("second-list", "notes/", 10))).Objects);
        await ErrorAsync("forbidden", () => AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore with { Revision = "binding-2" }));
    }

    [TestMethod]
    public async Task ReferenceCannotBeForgedOrUsedAsCursorOrAcrossAuthorizationContexts()
    {
        var written = await client.WriteAsync(Write("notes/a", "a"));
        await ErrorAsync("invalid_reference", () => client.StatAsync(new("forged", written.Item.Reference + "tampered")));
        await ErrorAsync("invalid_reference", () => client.ListAsync(new("kind", "notes/", 1, written.Item.Reference)));
        access.Revision = "grant-2";
        await ErrorAsync("invalid_reference", () => client.StatAsync(new("revision", written.Item.Reference)));
        access.Revision = "grant-1";
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access.OtherToken);
        await ErrorAsync("invalid_reference", () => client.StatAsync(new("subject", written.Item.Reference)));
        access.Enabled = false;
        await ErrorAsync("forbidden", () => client.ListAsync(new("revoked", "notes/", 1)));
    }

    [TestMethod]
    public async Task ReadonlyTargetCannotWriteThroughTheGenericToolOrClaimDurability()
    {
        await client.WriteAsync(Write("notes/a", "a"));
        var readOnly = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.ReadOnlyStore);
        Assert.IsFalse(readOnly.Description.Capabilities.Write);
        Assert.IsFalse(readOnly.Description.Capabilities.StableVersions);
        Assert.IsFalse(readOnly.Description.Capabilities.Retention);
        Assert.IsFalse(readOnly.Description.Capabilities.Delete);
        Assert.IsEmpty((await readOnly.ListAsync(new("ro-list", "notes/", 10))).Objects);
        await ErrorAsync("unsupported_operation", () => readOnly.WriteAsync(Write("notes/b", "b")));
        await RawErrorAsync("asp_write", Write("notes/b", "b"), "unsupported_operation", SampleAccess.ReadOnlyStore);
        await RawErrorAsync("asp_delete", new { requestId = "delete" }, "unsupported_operation");
        var tools = await mcp.ListToolsAsync();
        Assert.HasCount(5, tools);
        Assert.IsTrue(tools.All(tool => tool.Name.StartsWith("asp_", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task GenericArgumentsCannotWidenAuthorizedTargetPolicyOrPayloadBounds()
    {
        await RawErrorAsync("asp_list", new { requestId = "override", prefix = "notes/", limit = 1, storeId = "second" }, "invalid_request");
        await RawErrorAsync("asp_list", new { requestId = "override", prefix = "notes/", limit = 1, maxReadBytes = 99999 }, "invalid_request");
        await RawErrorAsync("asp_list", new Dictionary<string, object> { ["requestId"] = "schema", ["Prefix"] = "notes/", ["limit"] = 1 }, "invalid_request");
        await RawErrorAsync("asp_list", new { requestId = "schema", prefix = "notes/", limit = "1" }, "invalid_request");
        await RawErrorAsync("asp_list", new { requestId = "schema", limit = 1 }, "invalid_request");
        await RawErrorAsync("asp_list", new { requestId = "schema", prefix = (string?)null, limit = 1 }, "invalid_request");
        await ErrorAsync("forbidden", () => client.ListAsync(new("wide", "", 1)));
        await ErrorAsync("forbidden", () => client.WriteAsync(Write("outside/a", "a")));
        await ErrorAsync("invalid_request", () => client.WriteAsync(Write("notes/../escape", "a")));
        await ErrorAsync("limit_exceeded", () => client.WriteAsync(Write("notes/big", new string('x', 4097))));
        var written = await client.WriteAsync(Write("notes/a", "a"));
        await ErrorAsync("limit_exceeded", () => client.ReadAsync(new("read-limit", written.Item.Reference, 0, 4097)));
        await ErrorAsync("invalid_request", () => client.ReadAsync(new("offset", written.Item.Reference, 100, 1)));
        await RawErrorAsync("asp_write", Write("notes/a", "a") with { ContentBase64 = "?" }, "invalid_request");
    }

    [TestMethod]
    public async Task ExactWriteRetryReturnsOneReceiptWhileChangedArgumentsAndStaleVersionsConflict()
    {
        var request = Write("notes/retry", "v1");
        var initial = await client.WriteAsync(request);
        var retry = await client.WriteAsync(request);
        Assert.AreEqual(initial.Item.Version, retry.Item.Version);
        await ErrorAsync("idempotency_conflict", () => client.WriteAsync(request with { ContentBase64 = Convert.ToBase64String("v2"u8) }));
        await ErrorAsync("conflict", () => client.WriteAsync(Write("notes/retry", "v2")));
        var updated = await client.WriteAsync(Write("notes/retry", "v2") with { ExpectedVersion = initial.Item.Version });
        Assert.AreNotEqual(initial.Item.Version, updated.Item.Version);
        await ErrorAsync("conflict", () => client.WriteAsync(Write("notes/retry", "v3") with { ExpectedVersion = initial.Item.Version }));
        await ErrorAsync("version_unavailable", () => client.ReadAsync(new("old-version", initial.Item.Reference, 0, 100)));
        var replayOldReceipt = await client.WriteAsync(request);
        Assert.AreEqual(initial.Item.Version, replayOldReceipt.Item.Version);
        Assert.HasCount(1, (await client.ListAsync(new("count", "notes/", 10))).Objects);
    }

    [TestMethod]
    public async Task ConcurrentExactRetriesCommitOnce()
    {
        var request = Write("notes/concurrent", "content");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.WriteAsync(request)));
        Assert.HasCount(1, results.Select(value => value.Item.Version).Distinct().ToArray());
        Assert.HasCount(1, (await client.ListAsync(new("count", "notes/", 10))).Objects);
    }

    [TestMethod]
    public async Task WriteDeadlinesAndCancellationCannotProduceAnAcknowledgedPartialWrite()
    {
        await ErrorAsync("request_expired", () => client.WriteAsync(Write("notes/past", "a") with { NotAfter = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        await ErrorAsync("request_expired", () => client.WriteAsync(Write("notes/future", "a") with { NotAfter = DateTimeOffset.UtcNow.AddMinutes(6) }));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => client.WriteAsync(Write("notes/cancelled", "a"), cancelled.Token));
        Assert.IsEmpty((await client.ListAsync(new("count", "notes/", 10))).Objects);
    }

    [TestMethod]
    public async Task UnauthenticatedHttpCannotReachMcp()
    {
        using var anonymous = factory.CreateClient();
        using var response = await anonymous.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task MissingMappingAndProviderPinsFailExplicitly()
    {
        await ErrorAsync("unsupported_operation", () => AspClient.ConnectAsync(mcp, "missing", SampleAccess.FirstStore));
        await ErrorAsync("incompatible_provider", () => AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore, expectedProviderId: "different"));
        await ErrorAsync("incompatible_provider", () => AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore, expectedProviderVersion: "2.0.0"));
    }

    [TestMethod]
    public async Task NativeGenericToolSelectsTargetsAndReauthorizesEveryCall()
    {
        var tools = await mcp.ListToolsAsync();
        CollectionAssert.AreEquivalent(new[] { "asp_describe", "asp_list", "asp_stat", "asp_read", "asp_write" }, tools.Select(tool => tool.Name).ToArray());
        var listSchema = tools.Single(tool => tool.Name == "asp_list").ProtocolTool.InputSchema;
        Assert.IsTrue(listSchema.GetProperty("properties").TryGetProperty("store", out _));
        var request = new AspInvocation<AspWriteRequest>(SampleAccess.SecondStore, Write("notes/direct", "direct-agent-call"));
        var wire = JsonSerializer.SerializeToElement(request, AspProtocol.JsonOptions);
        var response = await mcp.CallToolAsync("asp_write", wire.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal));
        Assert.IsFalse(response.IsError == true);
        Assert.AreEqual("notes/direct", response.StructuredContent!.Value.GetProperty("data").GetProperty("item").GetProperty("key").GetString());
        Assert.IsEmpty((await client.ListAsync(new("first-empty", "notes/", 10))).Objects);
        var second = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.SecondStore);
        Assert.HasCount(1, (await second.ListAsync(new("second-visible", "notes/", 10))).Objects);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access.OtherToken);
        await ErrorAsync("forbidden", () => second.ListAsync(new("cached-client-denied", "notes/", 10)));
        await RawErrorAsync("asp_list", new AspListRequest("direct-denied", "notes/", 1), "forbidden", SampleAccess.SecondStore);
        Assert.IsEmpty((await client.ListAsync(new("first-authorized", "notes/", 1))).Objects);
        await ErrorAsync("forbidden", () => AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore with { StoreId = "unregistered" }));
    }

    [TestMethod]
    public async Task GenericEnvelopeRejectsPhysicalLocationsAndForgedBindingIdentities()
    {
        var valid = JsonSerializer.SerializeToElement(new AspInvocation<AspListRequest>(SampleAccess.FirstStore, new("invalid-envelope", "notes/", 1)), AspProtocol.JsonOptions);
        var arguments = valid.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        arguments["configuration"] = new { path = "/arbitrary/backend" };
        var response = await mcp.CallToolAsync("asp_list", arguments);
        Assert.IsTrue(response.IsError);
        Assert.AreEqual("invalid_request", response.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        await RawErrorAsync("asp_list", new AspListRequest("wrong-binding", "notes/", 1), "forbidden", SampleAccess.FirstStore with { BindingId = "other-physical-binding" });
    }

    private async Task RawErrorAsync(string tool, object args, string code, AspStoreRef? store = null)
    {
        var values = JsonSerializer.SerializeToElement(new AspInvocation<object>(store ?? SampleAccess.FirstStore, args), AspProtocol.JsonOptions).EnumerateObject()
            .ToDictionary(value => value.Name, value => (object?)value.Value.Clone(), StringComparer.Ordinal);
        var response = await mcp.CallToolAsync(tool, values);
        Assert.IsTrue(response.IsError);
        Assert.AreEqual(code, response.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
    }

    internal static AspWriteRequest Write(string key, string text) => new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(2), key, Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
    internal static async Task ErrorAsync(string code, Func<Task> action)
    {
        var error = await Assert.ThrowsExactlyAsync<AspException>(action);
        Assert.AreEqual(code, error.Code);
    }
}
