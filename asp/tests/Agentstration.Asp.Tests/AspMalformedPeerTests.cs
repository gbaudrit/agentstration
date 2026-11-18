using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Asp.Samples.LocalStorage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace Agentstration.Asp.Tests;

[TestClass]
public sealed class AspMalformedPeerTests
{
    [TestMethod]
    [DataRow("version", "incompatible_version")]
    [DataRow("schema", "incompatible_schema")]
    [DataRow("mapping", "unsupported_operation")]
    [DataRow("missing", "invalid_result")]
    [DataRow("wrongType", "invalid_result")]
    [DataRow("extra", "invalid_result")]
    [DataRow("capability", "unsupported_capability")]
    [DataRow("identity", "invalid_result")]
    [DataRow("case", "invalid_result")]
    [DataRow("null", "invalid_result")]
    [DataRow("envelope", "invalid_result")]
    public async Task ClientRefusesIncompatibleOrMalformedPeerDescriptions(string mode, string error)
    {
        await using var factory = Factory(mode);
        using var http = factory.CreateClient(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<SampleAccess>().Token);
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new("http://localhost/mcp") }, http));
        await AspTestHost.Error(error, () => AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore));
    }
    [TestMethod]
    [DataRow("readDigest")]
    [DataRow("readOffset")]
    [DataRow("readFinal")]
    [DataRow("writeDigest")]
    [DataRow("writeTarget")]
    [DataRow("writeNull")]
    public async Task CorruptTransferEvidenceCannotBecomeAValidResult(string mode)
    {
        await using var factory = Factory(mode);
        using var http = factory.CreateClient(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<SampleAccess>().Token);
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new("http://localhost/mcp") }, http));
        var client = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore);
        var request = new AspWriteBlobRequest("write", 0, true, "YQ==", Options: new("notes/a"), NotAfter: DateTimeOffset.UtcNow.AddMinutes(1));
        if (mode.StartsWith("write", StringComparison.Ordinal)) await AspTestHost.Error("invalid_result", () => client.WriteBlobAsync(request));
        else
        {
            var written = await client.WriteBlobAsync(request);
            await AspTestHost.Error("invalid_result", () => client.ReadBlobAsync(new("read", Selection: new(written.Receipt!.Item.Reference, PreferredChunkSizeBytes: 100), NotAfter: DateTimeOffset.UtcNow.AddMinutes(1))));
        }
    }
    private static WebApplicationFactory<Program> Factory(string mode) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services => services.PostConfigure<McpServerOptions>(options =>
    {
        var list = options.Handlers.ListToolsHandler!;
        options.Handlers.ListToolsHandler = async (request, ct) =>
        {
            var result = await list(request, ct);
            if (mode == "schema") result.Tools.Single(t => t.Name == "asp_describe").InputSchema = JsonSerializer.SerializeToElement(new { type = "object" });
            if (mode == "mapping") result.Tools = result.Tools.Where(t => t.Name != "asp_read_text").ToList();
            return result;
        };
        var call = options.Handlers.CallToolHandler!;
        options.Handlers.CallToolHandler = async (request, ct) =>
        {
            var result = await call(request, ct);
            var node = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!.AsObject();
            if (request.Params!.Name == "asp_describe")
            {
                switch (mode)
                {
                    case "version": node["aspVersion"] = "99.0"; break;
                    case "missing": node.Remove("data"); break;
                    case "wrongType": node["data"]!["providerId"] = 42; break;
                    case "extra": node["extra"] = "unknown"; break;
                    case "capability": node["data"]!["capabilities"]!["retention"] = true; break;
                    case "identity": node["requestId"] = "different"; break;
                    case "case": node["RequestId"] = node["requestId"]!.DeepClone(); node.Remove("requestId"); break;
                    case "null": node["data"]!["transfers"] = null; break;
                    case "envelope": node["error"] = new JsonObject { ["code"] = "forbidden", ["message"] = "Denied" }; break;
                }
            }
            if (request.Params.Name == "asp_read_blob")
            {
                if (mode == "readDigest") node["data"]!["contentBase64"] = "Yg==";
                if (mode == "readOffset") node["data"]!["offset"] = 99;
                if (mode == "readFinal") node["data"]!["final"] = false;
            }
            if (request.Params.Name == "asp_write_blob")
            {
                if (mode == "writeDigest") node["data"]!["receipt"]!["sha256"] = new string('0', 64);
                if (mode == "writeTarget") node["data"]!["receipt"]!["item"]!["key"] = "notes/other";
                if (mode == "writeNull") node["data"]!["receipt"] = null;
            }
            result.StructuredContent = JsonSerializer.SerializeToElement(node); return result;
        };
    })));
}
