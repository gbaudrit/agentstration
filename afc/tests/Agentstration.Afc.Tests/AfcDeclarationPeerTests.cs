using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Afc.Abstractions;
using ModelContextProtocol.Server;

namespace Agentstration.Afc.Tests;

[TestClass]
public sealed class AfcDeclarationPeerTests
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
        await using var host = new AfcTestHost(options =>
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
                if (request.Params!.Name != "afc_describe") return result;
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
        await AfcTestHost.Error(expected, () => host.StartAsync());
    }

}
