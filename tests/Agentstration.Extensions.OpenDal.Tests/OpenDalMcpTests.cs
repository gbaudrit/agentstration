using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Agentstration.Extensions.OpenDal.Tests;

[TestClass]
public sealed class OpenDalMcpTests
{
    // Public disposable loopback test credentials, never production credentials.
    private const string Credential = "public-disposable-opendal-fixture-token-1039";
    private const string OtherCredential = "public-disposable-opendal-other-token-1039";
    private static readonly AspStoreRef First = new("first", "filesystem", "revision-1");
    private static readonly AspAccessPolicy Policy = new(true, true, true, true, "notes/", 65536, 65536, 10);
    private readonly string root = Path.Combine(Path.GetTempPath(), "opendal-mcp-" + Guid.NewGuid().ToString("N"));
    private Factory factory = null!;

    [TestInitialize]
    public void Initialize() => factory = new(root);

    [TestCleanup]
    public async Task Cleanup()
    {
        await factory.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [TestMethod]
    public async Task PublishedPackagePerformsRealMcpDescribeWriteListStatAndBoundedReads()
    {
        using var http = factory.Authenticated();
        var manifest = await http.GetFromJsonAsync<AepManifest>(AepProtocol.DiscoveryPath, AepProtocol.JsonOptions);
        Assert.IsNotNull(manifest);
        Assert.IsEmpty(AepDescriptorValidator.Validate(manifest));
        Assert.IsEmpty(manifest.Contributions.ConnectedServiceTypes!);
        Assert.IsEmpty(manifest.Contributions.Tools!);
        var type = manifest.Contributions.StorageTypes!.Single(c => c.Id == OpenDalStorageTypes.FileSystemId);
        Assert.AreEqual(OpenDalStorageTypes.FileSystemId, type.Id);
        Assert.IsFalse(type.Versions.Single().RequiresConnectedService);
        await using var lease = await ConnectAsync(First);
        var description = lease.Client.Description;
        Assert.AreEqual(OpenDalStorageTypes.FileSystemId, description.ProviderId);
        Assert.IsTrue(description.Capabilities.Write);
        Assert.IsFalse(description.Capabilities.StableVersions || description.Capabilities.Retention || description.Capabilities.Delete);
        var written = await lease.Client.WriteBlobAsync(Write("notes/a", "OpenDAL réel"));
        var item = await lease.Client.StatAsync(new("stat", written.Receipt!.Item.Reference));
        Assert.AreEqual(written.Receipt!.Item.Version, item.Version);
        Assert.AreEqual("notes/a", (await lease.Client.ListAsync(new("list", "notes/", 10))).Objects.Single().Key);
        var first = await lease.Client.ReadBlobAsync(new("read", Selection: new(item.Reference, new(0, 4)), NotAfter: DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.AreEqual("Open", Encoding.UTF8.GetString(Convert.FromBase64String(first.ContentBase64)));
        Assert.IsTrue(first.Final);
        var rest = await lease.Client.ReadBlobAsync(new("rest", Selection: new(item.Reference, new(4, 100)), NotAfter: DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.AreEqual("DAL réel", Encoding.UTF8.GetString(Convert.FromBase64String(rest.ContentBase64)));
        Assert.IsTrue(rest.Final);
        Assert.IsEmpty(Directory.GetFiles(root, "objects.json", SearchOption.AllDirectories));
        Assert.AreEqual("OpenDAL réel", await File.ReadAllTextAsync(Directory.GetFiles(root, "*.blob", SearchOption.AllDirectories).Single()));
    }

    [TestMethod]
    public async Task ExactChunkReplaysAndRestartPreserveContentWithoutInventingDurableReceipts()
    {
        var request = Write("notes/retry", "one");
        string oldReference;
        await using (var lease = await ConnectAsync(First))
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => lease.Client.WriteBlobAsync(request)));
            Assert.HasCount(1, results.Select(r => r.SessionRef).Distinct().ToArray());
            Assert.IsNull(results[0].Receipt!.Item.Version); Assert.IsNull(results[0].Receipt!.Item.ETag);
            oldReference = results[0].Receipt!.Item.Reference;
            await Error("idempotency_conflict", () => lease.Client.WriteBlobAsync(request with { ContentBase64 = "dHdv" }));
            await Error("conflict", () => lease.Client.WriteBlobAsync(Write("notes/retry", "two")));
            await lease.Client.WriteBlobAsync(Write("notes/retry", "two") with { Options = new("notes/retry", "replace") });
            Assert.AreEqual(results[0], await lease.Client.WriteBlobAsync(request));
        }
        await factory.DisposeAsync(); factory = new(root);
        await using var restarted = await ConnectAsync(First);
        await Error("invalid_reference", () => restarted.Client.ReadBlobAsync(new("old", Selection: new(oldReference), NotAfter: DateTimeOffset.UtcNow.AddMinutes(1))));
        // Durable object content survives; in-process sessions/receipts do not.
        await Error("conflict", () => restarted.Client.WriteBlobAsync(request));
        var current = (await restarted.Client.ListAsync(new("list", "notes/", 10))).Objects.Single();
        var read = await restarted.Client.ReadTextAsync(new("read", Selection: new(current.Reference), NotAfter: DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.AreEqual("two", read.Content);
    }

    [TestMethod]
    public async Task SharedRootKeepsStoresReferencesAndCursorsIsolated()
    {
        await using var first = await ConnectAsync(First);
        await using var second = await ConnectAsync(First with { StoreId = "second" });
        foreach (var name in new[] { "c", "a", "b" }) await first.Client.WriteBlobAsync(Write("notes/" + name, name));
        var page = await first.Client.ListAsync(new("page-1", "notes/", 1));
        Assert.AreEqual("notes/a", page.Objects.Single().Key);
        Assert.IsNotNull(page.Cursor);
        Assert.AreEqual("notes/b", (await first.Client.ListAsync(new("page-2", "notes/", 1, page.Cursor))).Objects.Single().Key);
        await Error("invalid_reference", () => first.Client.ListAsync(new("changed-page", "notes/", 2, page.Cursor)));
        await Error("invalid_reference", () => second.Client.ListAsync(new("foreign-page", "notes/", 1, page.Cursor)));
        await Error("invalid_reference", () => second.Client.ReadBlobAsync(new("foreign-object", Selection: new(page.Objects[0].Reference), NotAfter: DateTimeOffset.UtcNow.AddMinutes(1))));
        Assert.IsEmpty((await second.Client.ListAsync(new("empty", "notes/", 10))).Objects);
        Assert.IsEmpty(Directory.GetFiles(root, "objects.json", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task EnvelopeLimitsTraversalOverridesAndReadOnlyTargetsAreEnforced()
    {
        await using var lease = await ConnectAsync(First, new OpenDalEnvelope(Prefix: "notes/", Write: true, MaximumReadBytes: 4));
        var written = await lease.Client.WriteBlobAsync(Write("notes/a", "content"));
        var bounded = await lease.Client.ReadBlobAsync(new("limit", Selection: new(written.Receipt!.Item.Reference, PreferredChunkSizeBytes: 100), NotAfter: DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.AreEqual(4, bounded.ByteLength); Assert.IsFalse(bounded.Final);
        await lease.Client.CloseReadAsync(new("close", bounded.SessionRef));
        await Error("forbidden", () => lease.Client.ListAsync(new("wide", "", 1)));
        await Error("invalid_request", () => lease.Client.WriteBlobAsync(Write("notes/../escape", "x")));
        await Error("forbidden", () => lease.Client.WriteBlobAsync(Write("outside/a", "x")));
        await Error("limit_exceeded", () => lease.Client.WriteBlobAsync(Write("notes/large", new string('x', 65537))));
        var wire = JsonSerializer.SerializeToElement(new { store = First, arguments = new { requestId = "override", prefix = "notes/", limit = 1, root = "/tmp/escape" } });
        var response = await lease.Mcp.CallToolAsync("asp_list", wire.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value));
        Assert.AreEqual("invalid_request", response.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        await using var readOnly = await ConnectAsync(First, new { prefix = "notes/", write = false });
        Assert.IsFalse(readOnly.Client.Description.Capabilities.Write);
        await Error("unsupported_operation", () => readOnly.Client.WriteBlobAsync(Write("notes/b", "x")));
    }

    [TestMethod]
    public async Task AuthenticationLeaseIdentityRevocationAndVersionsFailClosed()
    {
        using var anonymous = factory.CreateClient();
        using var response = await anonymous.PostAsJsonAsync(AepStorageProtocol.BindingPath, new { });
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var lease = await ConnectAsync(First);
        using var other = factory.Authenticated(OtherCredential);
        other.DefaultRequestHeaders.Add(AepStorageProtocol.BindingHeader, lease.Token);
        await using var otherMcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new("http://localhost/mcp") }, other));
        await Error("forbidden", () => AspClient.ConnectAsync(otherMcp, "asp", First));
        await Error("forbidden", () => AspClient.ConnectAsync(lease.Mcp, "asp", First with { Revision = "forged" }));
        await Error("incompatible_provider", () => AspClient.ConnectAsync(lease.Mcp, "asp", First, expectedProviderVersion: "2.0.0"));
        factory.Options.Value = new() { RootDirectory = root, AllowedClients = ["product"], Enabled = false };
        await Error("forbidden", () => lease.Client.ListAsync(new("revoked", "notes/", 1)));
    }

    [TestMethod]
    public async Task InvalidConfigurationEnvelopeAndIncompatibleContributionAreRefusedBeforeStorageIo()
    {
        using var http = factory.Authenticated();
        foreach (var request in new[]
        {
            Binding(First) with { Version = "2.0.0" },
            Binding(First) with { Digest = "sha256:wrong" },
            Binding(First) with { Configuration = JsonSerializer.SerializeToElement(new { root = "/tmp/elsewhere" }) },
            Binding(First) with { Envelope = JsonSerializer.SerializeToElement(new { prefix = "other/", write = true }) },
            Binding(First) with { Envelope = JsonSerializer.SerializeToElement(new { prefix = "notes/", secret = "forbidden" }) }
        })
        {
            using var response = await http.PostAsJsonAsync(AepStorageProtocol.BindingPath, request, AepProtocol.JsonOptions);
            Assert.IsFalse(response.IsSuccessStatusCode);
        }
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public void EverySupportedBackendContributesExecutableTypesWithExplicitTypedRequirements()
    {
        CollectionAssert.AreEquivalent(new[] { "opendal.fs", "opendal.s3", "opendal.azblob" }, OpenDalStorageTypes.Contributions.Select(c => c.Id).ToArray());
        foreach (var descriptor in OpenDalStorageTypes.Contributions.Where(c => c.Id != OpenDalStorageTypes.FileSystemId))
        {
            var version = descriptor.Versions.Single(); Assert.IsTrue(version.Capabilities.List && version.Capabilities.Stat && version.Capabilities.Read && version.Capabilities.Write);
            Assert.IsTrue(version.RequiresConnectedService); Assert.HasCount(1, version.Requirements.Where(r => r.Protection == AepValueProtection.Secured).ToArray());
        }
    }

    [TestMethod]
    public async Task ProgressiveNativeTransferExceedsEightMiBWithBoundedBuffers()
    {
        await using var lease = await ConnectAsync(First);
        var block = new byte[32768]; for (var i = 0; i < block.Length; i++) block[i] = (byte)(i % 251);
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = lease.Client.OpenWriteStream(new("notes/large"), "large-write", DateTimeOffset.UtcNow.AddMinutes(2));
        for (var i = 0; i < 288; i++) { await output.WriteAsync(block); expected.AppendData(block); }
        var receipt = await output.CompleteAsync(); var digest = Convert.ToHexStringLower(expected.GetHashAndReset());
        Assert.AreEqual(9 * 1024 * 1024L, receipt.WrittenBytes); Assert.AreEqual(digest, receipt.Sha256);
        await using var input = lease.Client.OpenReadStream(new(receipt.Item.Reference, PreferredChunkSizeBytes: 65536), "large-read", DateTimeOffset.UtcNow.AddMinutes(2));
        using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); int read; long total = 0;
        while ((read = await input.ReadAsync(block)) != 0) { total += read; actual.AppendData(block, 0, read); }
        Assert.AreEqual(receipt.WrittenBytes, total); Assert.AreEqual(digest, Convert.ToHexStringLower(actual.GetHashAndReset()));
        Assert.AreEqual(receipt.WrittenBytes, new FileInfo(Directory.GetFiles(root, "*.blob", SearchOption.AllDirectories).Single()).Length);
    }

    [TestMethod]
    public async Task CancelledAndExpiredWritesLeaveNoAcknowledgedContent()
    {
        await using var lease = await ConnectAsync(First);
        await Error("request_expired", () => lease.Client.WriteBlobAsync(Write("notes/expired", "x") with { NotAfter = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        using var ct = new CancellationTokenSource();
        await ct.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => lease.Client.WriteBlobAsync(Write("notes/cancelled", "x"), ct.Token));
        Assert.IsEmpty((await lease.Client.ListAsync(new("empty", "notes/", 10))).Objects);
    }

    private async Task<Lease> ConnectAsync(AspStoreRef store, object? envelope = null)
    {
        var http = factory.Authenticated();
        try
        {
            using var response = await http.PostAsJsonAsync(AepStorageProtocol.BindingPath, Binding(store, envelope), AepProtocol.JsonOptions);
            response.EnsureSuccessStatusCode();
            var bound = (await response.Content.ReadFromJsonAsync<AepStorageBindingLease>(AepProtocol.JsonOptions))!;
            http.DefaultRequestHeaders.Add(AepStorageProtocol.BindingHeader, bound.Token);
            var mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new("http://localhost/mcp") }, http));
            try { return new(http, mcp, await AspClient.ConnectAsync(mcp, "asp", store), bound.Token); }
            catch { await mcp.DisposeAsync(); throw; }
        }
        catch { http.Dispose(); throw; }
    }

    private static AepStorageBindingRequest Binding(AspStoreRef store, object? envelope = null)
    {
        var version = OpenDalStorageTypes.Contributions.Single(c => c.Id == OpenDalStorageTypes.FileSystemId).Versions.Single();
        return new(OpenDalStorageTypes.FileSystemId, version.Version, AepStorageProtocol.Digest(version),
            JsonSerializer.SerializeToElement(store, AspProtocol.JsonOptions), [], JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(envelope ?? new { prefix = "notes/", write = true }, AspProtocol.JsonOptions),
            JsonSerializer.SerializeToElement(Policy, AspProtocol.JsonOptions), new string('a', 64), DateTimeOffset.UtcNow.AddSeconds(25));
    }
    private static AspWriteBlobRequest Write(string key, string value) => new(Guid.NewGuid().ToString("N"), 0, true,
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), Options: new(key), NotAfter: DateTimeOffset.UtcNow.AddMinutes(2));
    private static async Task Error(string code, Func<Task> action) =>
        Assert.AreEqual(code, (await Assert.ThrowsAsync<AspException>(action)).Code);

    private sealed record Lease(HttpClient Http, McpClient Mcp, AspClient Client, string Token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Mcp.DisposeAsync(); Http.Dispose(); }
    }
    private sealed class MutableOptions(OpenDalOptions value) : IOptionsMonitor<OpenDalOptions>
    {
        public OpenDalOptions Value { get; set; } = value;
        public OpenDalOptions CurrentValue => Value;
        public OpenDalOptions Get(string? name) => Value;
        public IDisposable? OnChange(Action<OpenDalOptions, string?> listener) => null;
    }
    private sealed class Factory(string directory) : WebApplicationFactory<OpenDalBindingFactory>
    {
        public MutableOptions Options { get; } = new(new() { RootDirectory = directory, AllowedClients = ["product"] });
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aep:EnrollmentMode"] = "Disabled",
                ["OpenDal:RootDirectory"] = directory,
                ["OpenDal:AllowedClients:0"] = "product"
            }));
            builder.ConfigureServices(services =>
            {
                services.AddAepStaticBearerAuthentication(options =>
                {
                    options.AddToken("product", "product", Credential);
                    options.AddToken("other", "other", OtherCredential);
                });
                services.AddSingleton<IOptionsMonitor<OpenDalOptions>>(Options);
            });
        }
        public HttpClient Authenticated(string credential = Credential)
        {
            var http = CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            return http;
        }
    }
}
