using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Afc.Abstractions;

namespace Agentstration.Afc.Tests;

[TestClass]
public sealed class AfcPeerTests
{
    [TestMethod]
    [DataRow("identity", "invalid_result")]
    [DataRow("head", "invalid_result")]
    [DataRow("base", "invalid_result")]
    [DataRow("draft", "invalid_result")]
    [DataRow("receipt_key", "invalid_result")]
    [DataRow("receipt_digest", "invalid_result")]
    [DataRow("false_missing", "invalid_result")]
    [DataRow("unknown_enum", "invalid_result")]
    public async Task CreationEvidenceCannotSilentlyChangeIdentityOrPinnedIntent(string mode, string code)
    {
        await using var host = new AfcTestHost(options =>
        {
            var call = options.Handlers.CallToolHandler!;
            options.Handlers.CallToolHandler = async (request, ct) =>
            {
                var result = await call(request, ct);
                if (request.Params!.Name != "afc_create") return result;
                var node = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
                var item = node["data"]!["changeRequest"]!;
                switch (mode)
                {
                    case "identity": item["identity"]!["targetId"] = "foreign"; break;
                    case "head": item["head"]!["objectId"] = new string('c', 40); break;
                    case "base": item["base"]!["objectId"] = new string('c', 40); break;
                    case "draft": item["draft"] = false; break;
                    case "receipt_key": node["data"]!["operationKey"] = "foreign"; break;
                    case "receipt_digest": node["data"]!["intentDigest"] = "foreign"; break;
                    case "false_missing": node["data"]!["status"] = "missing"; node["data"]!.AsObject().Remove("changeRequest"); break;
                    case "unknown_enum": node["data"]!["status"] = "invented"; break;
                }
                result.StructuredContent = JsonSerializer.SerializeToElement(node); return result;
            };
        });
        await host.StartAsync();
        await AfcTestHost.Error(code, () => host.CreateAsync());
    }

    [TestMethod]
    [DataRow("false_fresh", "incomplete_observation")]
    [DataRow("false_stale", "invalid_result")]
    [DataRow("incomplete", "incomplete_observation")]
    [DataRow("duplicate", "invalid_result")]
    [DataRow("null_review", "invalid_result")]
    [DataRow("unknown_state", "invalid_result")]
    [DataRow("unrequested", "invalid_result")]
    [DataRow("old_review_commit", "incomplete_observation")]
    public async Task ReviewObservationsCannotFabricateFreshOrCompleteEvidence(string mode, string code)
    {
        await using var host = new AfcTestHost(options =>
        {
            var call = options.Handlers.CallToolHandler!;
            options.Handlers.CallToolHandler = async (request, ct) =>
            {
                var result = await call(request, ct);
                if (request.Params!.Name != "afc_observe") return result;
                var node = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
                var data = node["data"]!;
                switch (mode)
                {
                    case "false_fresh": data["headObjectId"] = new string('c', 40); break;
                    case "false_stale": data["status"] = "stale"; break;
                    case "incomplete": data["reviewsComplete"] = false; break;
                    case "duplicate": data["reviews"]!.AsArray().Add(data["reviews"]![0]!.DeepClone()); break;
                    case "null_review": data["reviews"] = new JsonArray((JsonNode?)null); break;
                    case "unknown_state": data["reviews"]![0]!["state"] = "invented"; break;
                    case "unrequested": data["reviewsComplete"] = true; break;
                    case "old_review_commit": data["reviews"]![0]!["objectId"] = new string('c', 40); break;
                }
                result.StructuredContent = JsonSerializer.SerializeToElement(node); return result;
            };
        });
        await host.StartAsync(); var item = await host.CreateAsync();
        await AfcTestHost.Error(code, () => host.Client.ObserveAsync(new("observe", item.Identity.RepositoryId,
            item.Identity.NativeId, item.Head.ObjectId, item.Base.ObjectId, Reviews: mode != "unrequested")));
    }

    [TestMethod]
    public async Task LostResponseRequiresExplicitRecoveryAndDoesNotCreateADuplicate()
    {
        var lose = true;
        await using var host = new AfcTestHost(options =>
        {
            var call = options.Handlers.CallToolHandler!;
            options.Handlers.CallToolHandler = async (request, ct) =>
            {
                var result = await call(request, ct);
                if (request.Params!.Name == "afc_create" && lose)
                {
                    lose = false;
                    result.StructuredContent = JsonSerializer.SerializeToElement(new { malformed = true });
                }
                return result;
            };
        });
        await host.StartAsync(); var request = host.Request();
        await AfcTestHost.Error("invalid_result", () => host.Client.CreateAsync(request));
        var recovery = await host.Client.CreateAsync(request with { Mode = AfcMutationMode.Recover });
        Assert.AreEqual(AfcReceiptStatus.Applied, recovery.Status);
        Assert.AreEqual("change-1", recovery.ChangeRequest!.Identity.NativeId);
        var next = await host.CreateAsync("second explicit intent");
        Assert.AreEqual("change-2", next.Identity.NativeId);
    }

    [TestMethod]
    public async Task CancellationReportedAfterAnEffectDoesNotJustifyAnotherCreate()
    {
        var cancel = true;
        await using var host = new AfcTestHost(options =>
        {
            var call = options.Handlers.CallToolHandler!;
            options.Handlers.CallToolHandler = async (request, ct) =>
            {
                var result = await call(request, ct);
                if (request.Params!.Name == "afc_create" && cancel)
                {
                    cancel = false;
                    result.StructuredContent = JsonSerializer.SerializeToElement(new AfcReply<object>(AfcContract.Id,
                        AfcContract.Version, request.Params.Arguments!["arguments"].GetProperty("requestId").GetString()!, Error: new("cancelled")), AfcContract.Json);
                    result.IsError = true;
                }
                return result;
            };
        });
        await host.StartAsync(); var request = host.Request();
        await AfcTestHost.Error("cancelled", () => host.Client.CreateAsync(request));
        Assert.AreEqual("change-1", (await host.Client.CreateAsync(request with { Mode = AfcMutationMode.Recover })).ChangeRequest!.Identity.NativeId);
    }

    [TestMethod]
    public async Task NegotiatedReplyLimitIsMeasuredBeforeParsingAnOversizedPeerResult()
    {
        await using var host = new AfcTestHost(options =>
        {
            var call = options.Handlers.CallToolHandler!;
            options.Handlers.CallToolHandler = async (request, ct) =>
            {
                var result = await call(request, ct);
                var node = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
                if (request.Params!.Name == "afc_describe") node["data"]!["capabilities"]!["limits"]!["maximumResultBytes"] = 4096;
                if (request.Params.Name == "afc_read") node["data"]!["body"] = new string('é', 4000);
                result.StructuredContent = JsonSerializer.SerializeToElement(node); return result;
            };
        });
        await host.StartAsync(); var item = await host.CreateAsync();
        await AfcTestHost.Error("limit_exceeded", () => host.Client.ReadAsync(new("read", item.Identity.RepositoryId, item.Identity.NativeId)));
    }
}
