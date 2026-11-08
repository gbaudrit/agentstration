using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    private static readonly AspAccessPolicy Policy = new(true, true, true, true, "notes/", 4096, 4096, 10);
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
        var written = await lease.Client.WriteAsync(Write("notes/a", "OpenDAL réel"));
        var item = await lease.Client.StatAsync(new("stat", written.Item.Reference));
        Assert.AreEqual(written.Item.Version, item.Version);
        Assert.AreEqual("notes/a", (await lease.Client.ListAsync(new("list", "notes/", 10))).Objects.Single().Key);
        var first = await lease.Client.ReadAsync(new("read", item.Reference, 0, 4));
        Assert.AreEqual("Open", Encoding.UTF8.GetString(Convert.FromBase64String(first.ContentBase64)));
        Assert.IsFalse(first.EndOfObject);
        var rest = await lease.Client.ReadAsync(new("rest", item.Reference, 4, 100));
        Assert.AreEqual("DAL réel", Encoding.UTF8.GetString(Convert.FromBase64String(rest.ContentBase64)));
        Assert.IsTrue(rest.EndOfObject);
        var statePath = Directory.GetFiles(root, "objects.json", SearchOption.AllDirectories).Single();
        using var state = JsonDocument.Parse(await File.ReadAllBytesAsync(statePath));
        Assert.AreEqual("OpenDAL réel", Encoding.UTF8.GetString(state.RootElement.GetProperty("objects")
            .GetProperty("notes/a").GetProperty("content").GetBytesFromBase64()));
    }

    [TestMethod]
    public async Task ExactRetriesConcurrencyConditionalWritesAndRestartPreserveOriginalReceipts()
    {
        var request = Write("notes/retry", "one");
        string version;
        await using (var lease = await ConnectAsync(First))
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => lease.Client.WriteAsync(request)));
            Assert.HasCount(1, results.Select(r => r.Item.Version).Distinct().ToArray());
            version = results[0].Item.Version;
            await Error("idempotency_conflict", () => lease.Client.WriteAsync(request with { ContentBase64 = "dHdv" }));
            await Error("conflict", () => lease.Client.WriteAsync(Write("notes/retry", "two")));
            var updated = await lease.Client.WriteAsync(Write("notes/retry", "two") with { ExpectedVersion = version });
            await Error("version_unavailable", () => lease.Client.ReadAsync(new("old", results[0].Item.Reference, 0, 10)));
            Assert.AreEqual(version, (await lease.Client.WriteAsync(request)).Item.Version);
            Assert.AreNotEqual(version, updated.Item.Version);
        }
        await factory.DisposeAsync();
        factory = new(root);
        await using var restarted = await ConnectAsync(First);
        Assert.AreEqual(version, (await restarted.Client.WriteAsync(request)).Item.Version);
        var current = (await restarted.Client.ListAsync(new("list", "notes/", 10))).Objects.Single();
        var read = await restarted.Client.ReadAsync(new("read", current.Reference, 0, 100));
        Assert.AreEqual("two", Encoding.UTF8.GetString(Convert.FromBase64String(read.ContentBase64)));
    }

    [TestMethod]
    public async Task SharedRootKeepsStoresReferencesAndCursorsIsolated()
    {
        await using var first = await ConnectAsync(First);
        await using var second = await ConnectAsync(First with { StoreId = "second" });
        foreach (var name in new[] { "c", "a", "b" }) await first.Client.WriteAsync(Write("notes/" + name, name));
        var page = await first.Client.ListAsync(new("page-1", "notes/", 1));
        Assert.AreEqual("notes/a", page.Objects.Single().Key);
        Assert.IsNotNull(page.Cursor);
        Assert.AreEqual("notes/b", (await first.Client.ListAsync(new("page-2", "notes/", 1, page.Cursor))).Objects.Single().Key);
        await Error("invalid_reference", () => first.Client.ListAsync(new("changed-page", "notes/", 2, page.Cursor)));
        await Error("invalid_reference", () => second.Client.ListAsync(new("foreign-page", "notes/", 1, page.Cursor)));
        await Error("invalid_reference", () => second.Client.ReadAsync(new("foreign-object", page.Objects[0].Reference, 0, 10)));
        Assert.IsEmpty((await second.Client.ListAsync(new("empty", "notes/", 10))).Objects);
        Assert.HasCount(1, Directory.GetFiles(root, "objects.json", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task EnvelopeLimitsTraversalOverridesAndReadOnlyTargetsAreEnforced()
    {
        await using var lease = await ConnectAsync(First, new OpenDalEnvelope(Prefix: "notes/", Write: true, MaximumReadBytes: 4));
        var written = await lease.Client.WriteAsync(Write("notes/a", "content"));
        await Error("limit_exceeded", () => lease.Client.ReadAsync(new("limit", written.Item.Reference, 0, 5)));
        await Error("forbidden", () => lease.Client.ListAsync(new("wide", "", 1)));
        await Error("invalid_request", () => lease.Client.WriteAsync(Write("notes/../escape", "x")));
        await Error("forbidden", () => lease.Client.WriteAsync(Write("outside/a", "x")));
        await Error("limit_exceeded", () => lease.Client.WriteAsync(Write("notes/large", new string('x', 4097))));
        var wire = JsonSerializer.SerializeToElement(new { store = First, arguments = new { requestId = "override", prefix = "notes/", limit = 1, root = "/tmp/escape" } });
        var response = await lease.Mcp.CallToolAsync("asp_list", wire.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value));
        Assert.AreEqual("invalid_request", response.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        await using var readOnly = await ConnectAsync(First, new { prefix = "notes/", write = false });
        Assert.IsFalse(readOnly.Client.Description.Capabilities.Write);
        await Error("unsupported_operation", () => readOnly.Client.WriteAsync(Write("notes/b", "x")));
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
    public async Task RemoteDescriptorsHaveTypedRequirementsAndRefuseExecutionWithoutRedeemingSecrets()
    {
        foreach (var descriptor in OpenDalStorageTypes.Contributions.Where(c => c.Id != OpenDalStorageTypes.FileSystemId))
        {
            var version = descriptor.Versions.Single();
            Assert.IsFalse(version.Capabilities.List || version.Capabilities.Stat || version.Capabilities.Read || version.Capabilities.Write);
            Assert.IsTrue(version.RequiresConnectedService);
            Assert.HasCount(1, version.Requirements.Where(r => r.Protection == AepValueProtection.Secured).ToArray());
            var request = Binding(First) with
            {
                ContributionId = descriptor.Id,
                Version = version.Version,
                Digest = AepStorageProtocol.Digest(version),
                Connection = version.Requirements.Select(r => r.Protection == AepValueProtection.Standard
                    ? AepBoundValue.Inline(r.Id, JsonSerializer.SerializeToElement("Opaque_VENDOR_Value"))
                    : AepBoundValue.Secured(r.Id, new(AepProtocol.SecretAccessVersion,
                        new Uri("https://never-contact.invalid/secret"), OpenDalStorageTypes.ExtensionId, r.Id,
                        "fixture-execution", "opaque-fixture-handle"))).ToArray()
            };
            using var http = factory.Authenticated();
            using var response = await http.PostAsJsonAsync(AepStorageProtocol.BindingPath, request, AepProtocol.JsonOptions);
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.IsFalse(Directory.Exists(root));
        }
    }

    [TestMethod]
    public async Task CancelledAndExpiredWritesLeaveNoAcknowledgedContent()
    {
        await using var lease = await ConnectAsync(First);
        await Error("request_expired", () => lease.Client.WriteAsync(Write("notes/expired", "x") with { NotAfter = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        using var ct = new CancellationTokenSource();
        await ct.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => lease.Client.WriteAsync(Write("notes/cancelled", "x"), ct.Token));
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
    private static AspWriteRequest Write(string key, string value) => new(Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow.AddMinutes(2), key, Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
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
