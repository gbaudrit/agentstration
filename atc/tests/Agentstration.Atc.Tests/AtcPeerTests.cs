using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Atc.Abstractions;
using ModelContextProtocol.Server;

namespace Agentstration.Atc.Tests;

[TestClass]
public sealed class AtcPeerTests
{
    [TestMethod]
    [DataRow("schema", "incompatible_schema")]
    [DataRow("output_schema", "incompatible_schema")]
    [DataRow("missing_mapping", "incompatible_mapping")]
    [DataRow("duplicate_mapping", "incompatible_mapping")]
    [DataRow("contract", "invalid_result")]
    [DataRow("version", "invalid_result")]
    [DataRow("target", "incompatible_target")]
    [DataRow("major", "incompatible_contract")]
    [DataRow("digest", "incompatible_schema")]
    [DataRow("revision", "incompatible_contract")]
    [DataRow("null", "invalid_result")]
    [DataRow("extra", "invalid_result")]
    [DataRow("request_id", "invalid_result")]
    [DataRow("capability", "invalid_capabilities")]
    [DataRow("error_flag", "invalid_result")]
    public async Task ClientRejectsMalformedDeclarationsAndMappings(string mode, string expected)
    {
        await using var host = new AtcTestHost(options =>
        {
            var list = options.Handlers.ListToolsHandler!;
            options.Handlers.ListToolsHandler = async (request, ct) =>
            {
                var result = await list(request, ct);
                if (mode == "schema") result.Tools[0].InputSchema = JsonSerializer.SerializeToElement(new { type = "object" });
                if (mode == "output_schema") result.Tools[0].OutputSchema = JsonSerializer.SerializeToElement(new { type = "object" });
                if (mode == "missing_mapping") result.Tools.RemoveAt(0);
                if (mode == "duplicate_mapping") result.Tools.Add(result.Tools[0]);
                return result;
            };
            var call = options.Handlers.CallToolHandler!;
            options.Handlers.CallToolHandler = async (request, ct) =>
            {
                var result = await call(request, ct);
                if (request.Params!.Name != "atc_describe") return result;
                var node = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
                switch (mode)
                {
                    case "contract": node["contractId"] = "another.contract"; break;
                    case "version": node["version"] = "2.0"; break;
                    case "target": node["data"]!["target"]!["targetId"] = "foreign"; break;
                    case "major": node["data"]!["declaration"]!["contractId"] = "agentstration.tracking.contracts.mcp.v2"; break;
                    case "digest": node["data"]!["declaration"]!["schemaDigests"]!["read.input"] = "wrong"; break;
                    case "revision": node["data"]!["declaration"]!["version"] = "2.0"; break;
                    case "null": node["data"] = null; break;
                    case "extra": node["authority"] = "forged"; break;
                    case "request_id": node["requestId"] = "foreign"; break;
                    case "capability": node["data"]!["capabilities"]!["operations"]![0] = "arbitrary_api"; break;
                    case "error_flag": result.IsError = true; break;
                }
                result.StructuredContent = JsonSerializer.SerializeToElement(node); return result;
            };
        });
        await AtcTestHost.Error(expected, () => host.StartAsync());
    }

    [TestMethod]
    [DataRow("item_identity", "invalid_result")]
    [DataRow("incomplete", "incomplete_observation")]
    [DataRow("receipt_key", "invalid_result")]
    [DataRow("receipt_digest", "invalid_result")]
    [DataRow("false_missing", "invalid_result")]
    [DataRow("null_page", "invalid_result")]
    [DataRow("duplicate_page", "invalid_result")]
    [DataRow("unknown_enum", "invalid_result")]
    [DataRow("oversize_utf8", "limit_exceeded")]
    public async Task ClientRejectsCorruptProviderEvidence(string mode, string expected)
    {
        await using var host = new AtcTestHost(options =>
        {
            var call = options.Handlers.CallToolHandler!;
            options.Handlers.CallToolHandler = async (request, ct) =>
            {
                var result = await call(request, ct);
                var node = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
                if (request.Params!.Name == "atc_mutate" && node["data"]?["item"] is { } item)
                {
                    switch (mode)
                    {
                        case "item_identity": item["identity"]!["targetId"] = "foreign"; break;
                        case "incomplete": item["relationsComplete"] = false; break;
                        case "receipt_key": node["data"]!["operationKey"] = "different"; break;
                        case "receipt_digest": node["data"]!["intentDigest"] = "different"; break;
                        case "false_missing": node["data"]!["status"] = "missing"; node["data"]!.AsObject().Remove("item"); break;
                        case "unknown_enum": node["data"]!["status"] = "invented"; break;
                    }
                }
                if (request.Params.Name == "atc_list")
                {
                    if (mode == "null_page") node["data"]!["items"] = new JsonArray((JsonNode?)null);
                    if (mode == "duplicate_page") node["data"]!["items"]!.AsArray().Add(node["data"]!["items"]![0]!.DeepClone());
                    if (mode == "oversize_utf8") node["data"]!["items"]![0]!["body"] = new string('é', 4000);
                }
                if (mode == "oversize_utf8" && request.Params.Name == "atc_describe")
                    node["data"]!["capabilities"]!["limits"]!["maximumResultBytes"] = 4096;
                result.StructuredContent = JsonSerializer.SerializeToElement(node); return result;
            };
        });
        await host.StartAsync();
        if (mode is "null_page" or "duplicate_page" or "oversize_utf8")
        {
            await host.CreateAsync(); await AtcTestHost.Error(expected, () => host.Client.ListAsync(new("list")));
        }
        else await AtcTestHost.Error(expected, () => host.CreateAsync());
    }

    [TestMethod]
    public async Task LostResponseIsRecoveredWithoutAutomaticResubmission()
    {
        var lose = true;
        await using var host = new AtcTestHost(options =>
        {
            var call = options.Handlers.CallToolHandler!;
            options.Handlers.CallToolHandler = async (request, ct) =>
            {
                var result = await call(request, ct);
                if (request.Params!.Name == "atc_mutate" && lose)
                {
                    lose = false;
                    result.StructuredContent = JsonSerializer.SerializeToElement(new { corrupt = true });
                }
                return result;
            };
        });
        await host.StartAsync(); var request = host.Mutation(new AtcCreate("once"));
        await AtcTestHost.Error("invalid_result", () => host.Client.MutateAsync(request));
        Assert.HasCount(1, (await host.Client.ListAsync(new("observe"))).Items);
        var recovered = await host.Client.MutateAsync(request with { Mode = AtcMutationMode.Recover });
        Assert.AreEqual(AtcMutationStatus.Applied, recovered.Status);
        Assert.HasCount(1, (await host.Client.ListAsync(new("again"))).Items);
    }

    [TestMethod]
    public async Task CancellationAfterEffectDoesNotJustifyAnotherCreate()
    {
        var cancel = true;
        await using var host = new AtcTestHost(options =>
        {
            var call = options.Handlers.CallToolHandler!;
            options.Handlers.CallToolHandler = async (request, ct) =>
            {
                var result = await call(request, ct);
                if (request.Params!.Name == "atc_mutate" && cancel)
                {
                    cancel = false;
                    result.StructuredContent = JsonSerializer.SerializeToElement(new AtcReply<object>(AtcContract.Id, AtcContract.Version,
                        request.Params.Arguments!["arguments"].GetProperty("requestId").GetString()!, Error: new("cancelled")), AtcContract.Json);
                    result.IsError = true;
                }
                return result;
            };
        });
        await host.StartAsync(); var request = host.Mutation(new AtcCreate("once"));
        await AtcTestHost.Error("cancelled", () => host.Client.MutateAsync(request));
        var receipt = await host.Client.MutateAsync(request with { Mode = AtcMutationMode.Recover });
        Assert.AreEqual(AtcMutationStatus.Applied, receipt.Status);
        Assert.HasCount(1, (await host.Client.ListAsync(new("count"))).Items);
    }
}
