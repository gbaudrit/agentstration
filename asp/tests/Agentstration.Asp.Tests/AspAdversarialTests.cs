using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Agentstration.Asp.Server;
using Asp.Samples.LocalStorage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Asp.Tests;

[TestClass]
public sealed class AspAdversarialTests
{
    [TestMethod]
    [DataRow("version", "incompatible_version")]
    [DataRow("schema", "incompatible_schema")]
    [DataRow("mapping", "unsupported_operation")]
    [DataRow("missing", "invalid_result")]
    [DataRow("wrongType", "invalid_result")]
    [DataRow("case", "invalid_result")]
    [DataRow("extra", "invalid_result")]
    [DataRow("capability", "unsupported_capability")]
    [DataRow("identity", "invalid_result")]
    [DataRow("null", "invalid_result")]
    [DataRow("envelope", "invalid_result")]
    public async Task ClientRejectsIncompatibleOrMalformedPeersOverRealMcp(string mode, string expectedCode)
    {
        await using var factory = FaultyFactory(mode);
        using var http = AuthorizedHttp(factory);
        await using var mcp = await ConnectAsync(http);
        await AspConformanceTests.ErrorAsync(expectedCode, () => AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore));
    }

    [TestMethod]
    public async Task MalformedReadAndWriteReceiptsCannotBecomeSuccess()
    {
        foreach (var mode in new[] { "readDigest", "writeDigest", "nullItem" })
        {
            await using var factory = FaultyFactory(mode);
            using var http = AuthorizedHttp(factory);
            await using var mcp = await ConnectAsync(http);
            var client = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore);
            if (mode is "writeDigest" or "nullItem")
                await AspConformanceTests.ErrorAsync("invalid_result", () => client.WriteAsync(AspConformanceTests.Write("notes/a", "a")));
            else
            {
                var written = await client.WriteAsync(AspConformanceTests.Write("notes/a", "a"));
                await AspConformanceTests.ErrorAsync("invalid_result", () => client.ReadAsync(new("read", written.Item.Reference, 0, 10)));
            }
        }
    }

    [TestMethod]
    public async Task ProviderFailureAfterCommitIsNotSuccessAndExactRetryReconciles()
    {
        var provider = new FailureAfterCommitProvider();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAspObjectReader>();
            services.AddSingleton<IAspObjectReader>(provider);
        }));
        using var http = AuthorizedHttp(factory);
        await using var mcp = await ConnectAsync(http);
        var client = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore);
        var request = AspConformanceTests.Write("notes/commit", "once");
        await AspConformanceTests.ErrorAsync("cancelled", () => client.WriteAsync(request));
        var retry = await client.WriteAsync(request);
        Assert.AreEqual("1", retry.Item.Version);
        Assert.HasCount(1, (await client.ListAsync(new("list", "notes/", 10))).Objects);
    }

    [TestMethod]
    public async Task ProviderResponsesOutsideThePolicyFailAtTheServerBoundary()
    {
        var provider = new OutsideScopeProvider();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAspObjectReader>();
            services.AddSingleton<IAspObjectReader>(provider);
        }));
        using var http = AuthorizedHttp(factory);
        await using var mcp = await ConnectAsync(http);
        var client = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore);
        await AspConformanceTests.ErrorAsync("forbidden", () => client.ListAsync(new("outside", "notes/", 10)));
    }

    [TestMethod]
    public async Task FixedPolicyIntersectsBroaderHostGrantAndKeyRanges()
    {
        var store = SampleAccess.FirstStore;
        var broad = SampleAccess.Policy with { Prefix = "", MaxWriteBytes = 65536 };
        var fixedPolicy = SampleAccess.Policy with { KeyFrom = "notes/b", KeyBefore = "notes/d", MaxWriteBytes = 2 };
        var provider = new MemoryObjectProvider(TimeProvider.System);
        var server = new AspMcpServer([new(store, fixedPolicy, provider,
            (_, _) => ValueTask.FromResult(new AspAccessContext(store, "subject", "grant", broad)))]);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<AspMcpServer>();
            services.AddSingleton(server);
        }));
        using var http = AuthorizedHttp(factory);
        await using var mcp = await ConnectAsync(http);
        var client = await AspClient.ConnectAsync(mcp, "asp", store);
        Assert.AreEqual("notes/", client.Description.Policy.Prefix);
        Assert.AreEqual(2, client.Description.Policy.MaxWriteBytes);
        await AspConformanceTests.ErrorAsync("forbidden", () => client.WriteAsync(AspConformanceTests.Write("notes/a", "a")));
        await AspConformanceTests.ErrorAsync("forbidden", () => client.WriteAsync(AspConformanceTests.Write("notes/d", "d")));
        await AspConformanceTests.ErrorAsync("limit_exceeded", () => client.WriteAsync(AspConformanceTests.Write("notes/b", "big")));
        await client.WriteAsync(AspConformanceTests.Write("notes/b", "b"));
        Assert.HasCount(1, (await client.ListAsync(new("range", "notes/", 10))).Objects);
    }

    [TestMethod]
    public async Task ClientTimeoutCancelsTheProviderOverMcp()
    {
        var provider = new WaitingProvider();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAspObjectReader>();
            services.AddSingleton<IAspObjectReader>(provider);
        }));
        using var http = AuthorizedHttp(factory);
        await using var mcp = await ConnectAsync(http);
        var client = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore, timeout: TimeSpan.FromSeconds(1));
        try
        {
            await client.ListAsync(new("timeout", "notes/", 1));
            Assert.Fail("A timed-out provider must not return success.");
        }
        catch (OperationCanceledException) { }
        catch (AspException error) { Assert.AreEqual("cancelled", error.Code); }
        await provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ReferencesAndCursorsExpireWithDeterministicTime()
    {
        var clock = new AdjustableClock();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        using var http = AuthorizedHttp(factory);
        await using var mcp = await ConnectAsync(http);
        var client = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore);
        await client.WriteAsync(AspConformanceTests.Write("notes/a", "a") with { NotAfter = clock.Now.AddMinutes(2) });
        await client.WriteAsync(AspConformanceTests.Write("notes/b", "b") with { NotAfter = clock.Now.AddMinutes(2) });
        var page = await client.ListAsync(new("page", "notes/", 1));
        clock.Now = clock.Now.AddMinutes(15);
        await AspConformanceTests.ErrorAsync("invalid_reference", () => client.StatAsync(new("expired-object", page.Objects[0].Reference)));
        await AspConformanceTests.ErrorAsync("invalid_reference", () => client.ListAsync(new("expired-cursor", "notes/", 1, page.Cursor)));
        Assert.HasCount(2, (await client.ListAsync(new("new-list", "notes/", 10))).Objects);
    }

    [TestMethod]
    public async Task LogicalTargetCanResolveToDifferentProviderInstancesWithoutChangingToolContracts()
    {
        var firstBackend = new MemoryObjectProvider(TimeProvider.System);
        var secondBackend = new MemoryObjectProvider(TimeProvider.System);
        AspStoreBinding Configure(AspStoreRef store, IAspObjectReader provider) => new(store, SampleAccess.Policy, provider,
            (_, _) => ValueTask.FromResult(new AspAccessContext(store, "authorized-subject", "grant-1", SampleAccess.Policy)));
        var server = new AspMcpServer([Configure(SampleAccess.FirstStore, firstBackend), Configure(SampleAccess.SecondStore, secondBackend)]);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<AspMcpServer>();
            services.AddSingleton(server);
        }));
        using var http = AuthorizedHttp(factory);
        await using var mcp = await ConnectAsync(http);
        var first = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore);
        var second = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.SecondStore);
        await first.WriteAsync(AspConformanceTests.Write("notes/a", "backend-one"));
        Assert.IsEmpty((await second.ListAsync(new("second-empty", "notes/", 1))).Objects);
        await second.WriteAsync(AspConformanceTests.Write("notes/a", "backend-two"));
        var firstObject = (await first.ListAsync(new("first-list", "notes/", 1))).Objects.Single();
        var secondObject = (await second.ListAsync(new("second-list", "notes/", 1))).Objects.Single();
        Assert.AreNotEqual(firstObject.Sha256, secondObject.Sha256);
        await AspConformanceTests.ErrorAsync("invalid_reference", () => second.ReadAsync(new("cross-physical", firstObject.Reference, 0, 100)));
        Assert.HasCount(5, await mcp.ListToolsAsync());
    }

    private sealed class AdjustableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class WaitingProvider : IAspObjectReader
    {
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ProviderId => "waiting";
        public string ProviderVersion => "1.0.0";
        public JsonElement ConfigurationSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public async ValueTask<AspProviderPage> ListAsync(AspAccessContext context, string prefix, string? afterKey, int limit, CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Cancelled.TrySetResult(); }
            throw new InvalidOperationException("The provider should only finish through cancellation.");
        }
        public ValueTask<AspProviderObject> StatAsync(AspAccessContext context, string key, string version, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public ValueTask<AspProviderRead> ReadAsync(AspAccessContext context, string key, string version, long offset, int maxBytes, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private static WebApplicationFactory<Program> FaultyFactory(string mode) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.ConfigureServices(services => services.PostConfigure<McpServerOptions>(options =>
        {
            var list = options.Handlers.ListToolsHandler!;
            options.Handlers.ListToolsHandler = async (request, ct) =>
            {
                var result = await list(request, ct);
                if (mode == "schema") result.Tools.Single(tool => tool.Name == "asp_describe").InputSchema = JsonSerializer.SerializeToElement(new { type = "object" });
                if (mode == "mapping") result.Tools = result.Tools.Where(tool => tool.Name != "asp_list").ToList();
                return result;
            };
            var call = options.Handlers.CallToolHandler!;
            options.Handlers.CallToolHandler = async (request, ct) =>
            {
                var result = await call(request, ct);
                if (request.Params!.Name == "asp_describe")
                {
                    var node = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!.AsObject();
                    switch (mode)
                    {
                        case "version": node["aspVersion"] = "99.0"; break;
                        case "missing": node.Remove("data"); break;
                        case "wrongType": node["data"]!["providerId"] = 42; break;
                        case "extra": node["extra"] = "unknown"; break;
                        case "capability": node["data"]!["capabilities"]!["retention"] = true; break;
                        case "identity": node["requestId"] = "different"; break;
                        case "case": node["RequestId"] = node["requestId"]!.DeepClone(); node.Remove("requestId"); break;
                        case "null": node["data"]!["capabilities"] = null; break;
                        case "envelope": node["error"] = new JsonObject { ["code"] = "forbidden", ["message"] = "Denied" }; break;
                    }
                    result.StructuredContent = JsonSerializer.SerializeToElement(node);
                }
                else if (request.Params.Name == "asp_read" && mode == "readDigest")
                {
                    var node = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
                    node["data"]!["contentBase64"] = "Yg==";
                    result.StructuredContent = JsonSerializer.SerializeToElement(node);
                }
                else if (request.Params.Name == "asp_write" && mode is "writeDigest" or "nullItem")
                {
                    var node = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
                    if (mode == "writeDigest") node["data"]!["item"]!["sha256"] = new string('0', 64);
                    else node["data"]!["item"] = null;
                    result.StructuredContent = JsonSerializer.SerializeToElement(node);
                }
                return result;
            };
        })));

    private static HttpClient AuthorizedHttp(WebApplicationFactory<Program> factory)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<SampleAccess>().Token);
        return http;
    }
    private static Task<McpClient> ConnectAsync(HttpClient http) => McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new("http://localhost/mcp") }, http));

    private sealed class FailureAfterCommitProvider : IAspObjectReader, IAspObjectWriter
    {
        private readonly MemoryObjectProvider inner = new(TimeProvider.System);
        private int writes;
        public string ProviderId => inner.ProviderId;
        public string ProviderVersion => inner.ProviderVersion;
        public JsonElement ConfigurationSchema => inner.ConfigurationSchema;
        public async ValueTask<AspProviderObject> WriteAsync(AspAccessContext context, AspProviderWrite request, CancellationToken cancellationToken)
        {
            var result = await inner.WriteAsync(context, request, cancellationToken);
            if (Interlocked.Increment(ref writes) == 1) throw new OperationCanceledException("Simulated loss after atomic commit.");
            return result;
        }
        public ValueTask<AspProviderPage> ListAsync(AspAccessContext context, string prefix, string? afterKey, int limit, CancellationToken cancellationToken) => inner.ListAsync(context, prefix, afterKey, limit, cancellationToken);
        public ValueTask<AspProviderObject> StatAsync(AspAccessContext context, string key, string version, CancellationToken cancellationToken) => inner.StatAsync(context, key, version, cancellationToken);
        public ValueTask<AspProviderRead> ReadAsync(AspAccessContext context, string key, string version, long offset, int maxBytes, CancellationToken cancellationToken) => inner.ReadAsync(context, key, version, offset, maxBytes, cancellationToken);
    }

    private sealed class OutsideScopeProvider : IAspObjectReader
    {
        public string ProviderId => "outside";
        public string ProviderVersion => "1.0.0";
        public JsonElement ConfigurationSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public ValueTask<AspProviderPage> ListAsync(AspAccessContext context, string prefix, string? afterKey, int limit, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AspProviderPage([new("outside/a", "1", 0, AspValidation.Digest([]))], null));
        public ValueTask<AspProviderObject> StatAsync(AspAccessContext context, string key, string version, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public ValueTask<AspProviderRead> ReadAsync(AspAccessContext context, string key, string version, long offset, int maxBytes, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
