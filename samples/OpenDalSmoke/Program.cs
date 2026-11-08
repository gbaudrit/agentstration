using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using ModelContextProtocol.Client;

if (args.Length is < 2 or > 3 || !Uri.TryCreate(args[0], UriKind.Absolute, out var endpoint)
    || !endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https") || args.Length == 3 && args[2] != "--read-only")
    throw new ArgumentException("Usage: OpenDalSmoke <loopback-extension-url> <shared-key-file> [--read-only]");
using var http = new HttpClient { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AepSharedKeyFile.Read(args[1]));
var manifest = await http.GetFromJsonAsync<AepManifest>(AepProtocol.DiscoveryPath, AepProtocol.JsonOptions)
    ?? throw new InvalidDataException("The extension returned no manifest.");
if (AepDescriptorValidator.Validate(manifest).Count != 0) throw new InvalidDataException("The AEP manifest is invalid.");
var type = manifest.Contributions.StorageTypes!.Single(t => t.Id == "opendal.fs");
var version = type.Versions.Single(v => v.Version == type.PreferredVersion);
var store = new AspStoreRef("offline-smoke", "filesystem", "1");
var policy = new AspAccessPolicy(true, true, true, true, "demo/", 4096, 4096, 10);
var request = new AepStorageBindingRequest(type.Id, version.Version, AepStorageProtocol.Digest(version),
    JsonSerializer.SerializeToElement(store, AspProtocol.JsonOptions), [], JsonSerializer.SerializeToElement(new { }),
    JsonSerializer.SerializeToElement(new { prefix = "demo/", write = true }),
    JsonSerializer.SerializeToElement(policy, AspProtocol.JsonOptions), new string('a', 64), DateTimeOffset.UtcNow.AddSeconds(25));
using var response = await http.PostAsJsonAsync(AepStorageProtocol.BindingPath, request, AepProtocol.JsonOptions);
response.EnsureSuccessStatusCode();
var lease = await response.Content.ReadFromJsonAsync<AepStorageBindingLease>(AepProtocol.JsonOptions)
    ?? throw new InvalidDataException("The extension returned no binding.");
http.DefaultRequestHeaders.Add(AepStorageProtocol.BindingHeader, lease.Token);
await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new(endpoint, "/mcp") }, http));
var client = await AspClient.ConnectAsync(mcp, version.Asp.ToolPrefix, store,
    expectedProviderId: version.Asp.ProviderId, expectedProviderVersion: version.Asp.ProviderVersion);
var existing = (await client.ListAsync(new("before", "demo/", 10))).Objects.SingleOrDefault(o => o.Key == "demo/smoke.txt");
if (args.Length == 2)
{
    var write = new AspWriteRequest(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(2), "demo/smoke.txt",
        Convert.ToBase64String("OpenDAL ASP/MCP offline"u8), existing?.Version);
    var written = await client.WriteAsync(write);
    var retry = await client.WriteAsync(write);
    if (retry.Item.Version != written.Item.Version) throw new InvalidDataException("The write retry changed the committed version.");
}
var item = (await client.ListAsync(new("after", "demo/", 10))).Objects.Single(o => o.Key == "demo/smoke.txt");
var metadata = await client.StatAsync(new("stat", item.Reference));
var content = await client.ReadAsync(new("read", metadata.Reference, 0, 4096));
if (Encoding.UTF8.GetString(Convert.FromBase64String(content.ContentBase64)) != "OpenDAL ASP/MCP offline")
    throw new InvalidDataException("The filesystem round trip differs.");
Console.WriteLine($"OpenDAL ASP/MCP smoke passed; bytes={metadata.Length}; version={metadata.Version}");
