using System.Net.Http.Headers;
using System.Text.Json;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Asp.Samples.LocalStorage;

namespace Agentstration.Asp.Tests;

[TestClass]
public sealed class AspAdversarialTests
{
    [TestMethod]
    public async Task EveryContinuationReauthorizesStoreSubjectRevisionAndNarrowingPolicy()
    {
        await using var host = new AspTestHost(); await host.StartAsync();
        var first = await host.Client.WriteBlobAsync(host.Write("notes/open", [1], false));
        var next = new AspWriteBlobRequest("next", 1, true, "Ag==", first.SessionRef);
        var foreign = await host.RawAsync("write_blob", next, SampleAccess.SecondStore);
        Assert.AreEqual("invalid_reference", foreign.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        host.Access.Revision = "revoked-grant";
        await AspTestHost.Error("invalid_reference", () => host.Client.WriteBlobAsync(next));
        host.Access.Revision = "grant-1"; host.Access.Enabled = false;
        await AspTestHost.Error("forbidden", () => host.Client.WriteBlobAsync(next));
        host.Access.Enabled = true;
        host.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.Access.OtherToken);
        await AspTestHost.Error("invalid_reference", () => host.Client.WriteBlobAsync(next));
        host.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.Access.Token);
        var changed = await host.RawAsync("write_blob", next, SampleAccess.FirstStore with { Revision = "forged" });
        Assert.AreEqual("forbidden", changed.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
    }
    [TestMethod]
    public async Task FixedSelectionsAndPhysicalConfigurationCannotBeChangedByContinuations()
    {
        await using var host = new AspTestHost(); await host.StartAsync();
        var initial = host.Write("notes/open", [1], false);
        var first = await host.Client.WriteBlobAsync(initial);
        await AspTestHost.Error("invalid_request", () => host.Client.WriteBlobAsync(new("options", 1, true, "", first.SessionRef, new("notes/other"))));
        await AspTestHost.Error("invalid_request", () => host.Client.WriteBlobAsync(new("deadline", 1, true, "", first.SessionRef, NotAfter: host.Clock.Now.AddMinutes(1))));
        var extra = await host.RawAsync("write_blob", new { requestId = "override", sequence = 1, final = true, contentBase64 = "", sessionRef = first.SessionRef, root = "/physical/override" });
        Assert.AreEqual("invalid_request", extra.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        await AspTestHost.Error("invalid_reference", () => host.Client.WriteTextAsync(new("representation", 1, true, "", first.SessionRef)));
        await AspTestHost.Error("forbidden", () => host.Client.WriteBlobAsync(host.Write("outside/key", [1])));
        await AspTestHost.Error("invalid_request", () => host.Client.WriteBlobAsync(host.Write("notes/../escape", [1])));
        await AspTestHost.Error("incompatible_provider", () => AspClient.ConnectAsync(host.Mcp, "asp", SampleAccess.FirstStore, expectedProviderVersion: "wrong"));
        await AspTestHost.Error("unsupported_operation", () => AspClient.ConnectAsync(host.Mcp, "missing", SampleAccess.FirstStore));
    }
    [TestMethod]
    public async Task SessionsAndReplaysExpireWithoutEvictingUnexpiredResults()
    {
        await using var host = new AspTestHost(limits: new(MaximumSessions: 1, MaximumReplayBytes: 4096, ReceiptWindowSeconds: 10));
        await host.StartAsync();
        var original = host.Write("notes/open", [1], false);
        var open = await host.Client.WriteBlobAsync(original);
        await AspTestHost.Error("capacity_exceeded", () => host.Client.WriteBlobAsync(host.Write("notes/other", [2], false)));
        Assert.AreEqual(open, await host.Client.WriteBlobAsync(original));
        host.Clock.Now = host.Clock.Now.AddSeconds(11);
        await AspTestHost.Error("request_expired", () => host.Client.WriteBlobAsync(original));
        var end = await host.Client.WriteBlobAsync(new("end", 1, true, "", open.SessionRef));
        Assert.IsTrue(end.Final);
        host.Clock.Now = host.Clock.Now.AddMinutes(2);
        await AspTestHost.Error("invalid_reference", () => host.Client.WriteStatusAsync(new("expired", open.SessionRef)));
        await AspTestHost.Error("request_expired", () => host.Client.WriteBlobAsync(original));
    }
    [TestMethod]
    public async Task LiveReadReplayCapacityRefusesBeforeAdvancingAndRecoversAfterExpiry()
    {
        await using var host = new AspTestHost(limits: new(MaximumReplayBytes: 4096, ReceiptWindowSeconds: 5)); await host.StartAsync();
        var item = await host.Client.WriteBlobAsync(host.Write("notes/read", "abcdef"u8.ToArray()));
        var first = await host.Client.ReadBlobAsync(host.Read(item.Receipt!.Item.Reference, preference: 2));
        var next = new AspReadBlobRequest("next", 1, first.SessionRef);
        await AspTestHost.Error("capacity_exceeded", () => host.Client.ReadBlobAsync(next));
        host.Clock.Now = host.Clock.Now.AddSeconds(6);
        var second = await host.Client.ReadBlobAsync(next);
        Assert.AreEqual("Y2Q=", second.ContentBase64);
    }
    [TestMethod]
    public async Task ReadOnlyTargetsAndWirePayloadBoundsFailExplicitly()
    {
        await using var host = new AspTestHost(); await host.StartAsync();
        var readOnly = await AspClient.ConnectAsync(host.Mcp, "asp", SampleAccess.ReadOnlyStore);
        await AspTestHost.Error("unsupported_operation", () => readOnly.WriteBlobAsync(host.Write("notes/no", [1])));
        await AspTestHost.Error("limit_exceeded", () => host.Client.WriteBlobAsync(host.Write("notes/big", new byte[4097])));
        await AspTestHost.Error("invalid_request", () => host.Client.WriteBlobAsync(host.Write("notes/base64", [1]) with { ContentBase64 = "?" }));
        await AspTestHost.Error("unsupported_encoding", () => host.Client.WriteTextAsync(new("encoding", 0, true, "text", Options: new("notes/text"), NotAfter: host.Clock.Now.AddMinutes(1), Encoding: "utf-16")));
        using var ct = new CancellationTokenSource(); await ct.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => host.Client.WriteBlobAsync(host.Write("notes/cancel", [1]), ct.Token));
    }
}
