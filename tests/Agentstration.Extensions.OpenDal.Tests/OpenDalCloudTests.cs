using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Agentstration.Extensions.OpenDal.Tests;

[TestClass]
public sealed class OpenDalCloudTests
{
    private const string Credential = "public-disposable-opendal-cloud-transport";
    [TestMethod]
    [DataRow("s3")]
    [DataRow("azblob")]
    public async Task RealNativeCloudOperatorsUseTheSameMcpAdapterAndProtectedConnection(string scheme)
    {
        // Synthetic protocol peer, not a cloud emulator or validation of a live account.
        await using var backend = await CloudPeer.StartAsync(scheme);
        var root = Path.Combine(Path.GetTempPath(), "opendal-cloud-test-" + Guid.NewGuid().ToString("N"));
        await using var factory = new Factory(root);
        try
        {
            using var http = factory.CreateClient(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Credential);
            var type = OpenDalStorageTypes.Contributions.Single(c => c.Id == "opendal." + scheme); var version = type.Versions.Single();
            var store = new AspStoreRef("cloud", "binding", "1");
            var values = version.Requirements.Select(r => r.Protection == AepValueProtection.Secured
                ? AepBoundValue.Secured(r.Id, new(AepProtocol.SecretAccessVersion, new Uri(backend.Endpoint + "/grant"), OpenDalStorageTypes.ExtensionId, r.Id, "fixture", "public-disposable-grant"))
                : AepBoundValue.Inline(r.Id, JsonSerializer.SerializeToElement(r.Id == "endpoint" ? backend.Endpoint : r.Id == "region" ? "test" : "fixture"))).ToArray();
            var binding = new AepStorageBindingRequest(type.Id, version.Version, AepStorageProtocol.Digest(version), JsonSerializer.SerializeToElement(store, AspProtocol.JsonOptions), values,
                JsonSerializer.SerializeToElement(scheme == "s3" ? new Dictionary<string, string> { { "bucket", "fixture" } } : new Dictionary<string, string> { { "container", "fixture" } }),
                JsonSerializer.SerializeToElement(new { prefix = "notes/", write = true }), JsonSerializer.SerializeToElement(new AspAccessPolicy(true, true, true, true, "notes/", 65536, 65536, 10), AspProtocol.JsonOptions),
                new string('a', 64), DateTimeOffset.UtcNow.AddSeconds(25));
            using var configured = await http.PostAsJsonAsync(AepStorageProtocol.BindingPath, binding, AepProtocol.JsonOptions);
            configured.EnsureSuccessStatusCode();
            var lease = (await configured.Content.ReadFromJsonAsync<AepStorageBindingLease>(AepProtocol.JsonOptions))!;
            Assert.AreEqual(1, backend.Redemptions); http.DefaultRequestHeaders.Add(AepStorageProtocol.BindingHeader, lease.Token);
            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new("http://localhost/mcp") }, http));
            var client = await AspClient.ConnectAsync(mcp, "asp", store, expectedProviderId: type.Id);
            var first = await client.WriteTextAsync(new("first", 0, false, "cloud-", Options: new("notes/key"), NotAfter: DateTimeOffset.UtcNow.AddMinutes(1)));
            var lastRequest = new AspWriteTextRequest("last", 1, true, "bytes", first.SessionRef);
            var last = await client.WriteTextAsync(lastRequest); Assert.AreEqual(last, await client.WriteTextAsync(lastRequest));
            Assert.AreEqual(1, backend.Publications); Assert.IsNull(last.Receipt!.Item.Version); Assert.IsNull(last.Receipt.Item.ETag);
            var listed = (await client.ListAsync(new("list", "notes/", 10))).Objects.Single();
            Assert.IsNotNull(listed.ETag);
            var read = await client.ReadTextAsync(new("read", Selection: new(listed.Reference, new(6, 5)), NotAfter: DateTimeOffset.UtcNow.AddMinutes(1)));
            Assert.AreEqual("bytes", read.Content); Assert.IsTrue(read.Final);
            var conflict = await Assert.ThrowsAsync<AspException>(() => client.WriteTextAsync(new("collision", 0, true, "bad", Options: new("notes/key"), NotAfter: DateTimeOffset.UtcNow.AddMinutes(1))));
            Assert.AreEqual("conflict", conflict.Code);
            Assert.IsFalse(Directory.Exists(root)); // Remote execution never falls back to local files.
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Factory(string root) : WebApplicationFactory<OpenDalBindingFactory>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Aep:EnrollmentMode"] = "Disabled", ["OpenDal:RootDirectory"] = root, ["OpenDal:AllowedClients:0"] = "product" }));
            builder.ConfigureServices(services => services.AddAepStaticBearerAuthentication(options => options.AddToken("product", "product", Credential)));
        }
    }
    private sealed class CloudPeer(WebApplication app, string scheme) : IAsyncDisposable
    {
        private readonly Dictionary<string, byte[]> objects = new(StringComparer.Ordinal);
        public string Endpoint => app.Urls.Single();
        public int Redemptions { get; private set; }
        public int Publications { get; private set; }
        public static async Task<CloudPeer> StartAsync(string scheme)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build(); var peer = new CloudPeer(app, scheme); app.Run(peer.HandleAsync); await app.StartAsync(); return peer;
        }
        private async Task HandleAsync(HttpContext context)
        {
            var request = context.Request; var response = context.Response;
            if (request.Path == "/grant")
            {
                Redemptions++; await response.WriteAsJsonAsync(new AepSecretAccessResponse(AepProtocol.SecretAccessVersion,
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(scheme == "azblob" ? "Zml4dHVyZS1rZXk=" : "public-disposable-native-secret"))), AepProtocol.JsonOptions); return;
            }
            // Native paths are /fixture/<isolated root>/<logical key> for both schemes.
            var key = request.Path.Value!.TrimStart('/'); if (key.StartsWith("fixture/", StringComparison.Ordinal)) key = key[8..];
            if (request.Method == "GET" && (request.Query.ContainsKey("list-type") || request.Query["comp"] == "list"))
            {
                var prefix = request.Query["prefix"].ToString();
                var items = objects.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal);
                var xml = scheme == "s3" ? new XElement("ListBucketResult", new XElement("IsTruncated", "false"), items.Select(p => new XElement("Contents", new XElement("Key", p.Key), new XElement("Size", p.Value.Length), new XElement("ETag", ETag(p.Value)), new XElement("LastModified", "2026-01-01T00:00:00Z"))))
                    : new XElement("EnumerationResults", new XElement("Blobs", items.Select(p => new XElement("Blob", new XElement("Name", p.Key), new XElement("Properties", new XElement("Content-Length", p.Value.Length), new XElement("BlobType", "BlockBlob"), new XElement("Etag", ETag(p.Value)), new XElement("Last-Modified", "Thu, 01 Jan 2026 00:00:00 GMT"))))), new XElement("NextMarker", ""));
                response.ContentType = "application/xml"; await response.WriteAsync(xml.ToString(SaveOptions.DisableFormatting)); return;
            }
            objects.TryGetValue(key, out var content);
            if (request.Method == "PUT")
            {
                if (request.Headers.IfNoneMatch == "*" && content is not null) { response.StatusCode = 412; response.Headers["x-ms-error-code"] = "ConditionNotMet"; response.ContentType = "application/xml"; await response.WriteAsync(scheme == "s3" ? "<Error><Code>PreconditionFailed</Code><Message>Fixture condition failed</Message></Error>" : "<Error><Code>ConditionNotMet</Code><Message>Fixture condition failed</Message></Error>"); return; }
                using var data = new MemoryStream(); await request.Body.CopyToAsync(data); content = data.ToArray(); objects[key] = content; Publications++;
                response.StatusCode = scheme == "s3" ? 200 : 201; response.Headers.ETag = ETag(content); response.Headers["x-ms-request-id"] = "fixture"; return;
            }
            if (content is null) { response.StatusCode = 404; response.Headers["x-ms-error-code"] = "BlobNotFound"; await response.WriteAsync("<Error><Code>NoSuchKey</Code></Error>"); return; }
            if (request.Headers.IfMatch.Count > 0 && request.Headers.IfMatch.ToString() != ETag(content)) { response.StatusCode = 412; return; }
            response.Headers.ETag = ETag(content); response.Headers.LastModified = "Thu, 01 Jan 2026 00:00:00 GMT"; response.Headers["x-ms-blob-type"] = "BlockBlob"; response.ContentType = "application/octet-stream";
            if (request.Method == "HEAD") { response.ContentLength = content.Length; return; }
            var range = request.Headers.Range.ToString(); if (range.Length == 0) range = request.Headers["x-ms-range"].ToString();
            var offset = 0; var count = content.Length;
            if (range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                var parts = range[6..].Split('-'); offset = int.Parse(parts[0], CultureInfo.InvariantCulture);
                count = Math.Min(content.Length - 1, parts[1].Length == 0 ? content.Length - 1 : int.Parse(parts[1], CultureInfo.InvariantCulture)) - offset + 1;
                response.StatusCode = 206; response.Headers.ContentRange = $"bytes {offset}-{offset + count - 1}/{content.Length}";
            }
            response.ContentLength = count; await response.Body.WriteAsync(content.AsMemory(offset, count));
        }
        private static string ETag(byte[] content) => "\"" + Convert.ToHexStringLower(SHA256.HashData(content)) + "\"";
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
