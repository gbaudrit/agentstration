using System.Text;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Asp.Samples.LocalStorage;

namespace Agentstration.Asp.Tests;

[TestClass]
public sealed class AspConformanceTests
{
    private AspTestHost host = null!;
    [TestInitialize] public async Task Initialize() { host = new(); await host.StartAsync(); }
    [TestCleanup] public async Task Cleanup() => await host.DisposeAsync();

    [TestMethod]
    public async Task CanonicalMappingsReplaceTheUnreleasedReadAndWriteTools()
    {
        var names = (await host.Mcp.ListToolsAsync()).Select(t => t.Name).ToArray();
        CollectionAssert.AreEquivalent(new[] { "asp_describe", "asp_list", "asp_stat", "asp_read_text", "asp_read_blob", "asp_write_text", "asp_write_blob", "asp_close_read", "asp_abort_write", "asp_write_status" }, names);
        Assert.IsFalse(names.Contains("asp_read", StringComparer.Ordinal) || names.Contains("asp_write", StringComparer.Ordinal));
    }
    [TestMethod]
    public async Task CompleteBlobRoundTripPreservesBytesAndExactReceipt()
    {
        var bytes = new byte[] { 0, 255, 128, 1, 13, 10 };
        var request = host.Write("notes/binary", bytes);
        var written = await host.Client.WriteBlobAsync(request);
        Assert.IsTrue(written.Final); Assert.IsNotNull(written.Receipt);
        Assert.AreEqual(AspValidation.Digest(bytes), written.Receipt.Sha256);
        var read = await host.Client.ReadBlobAsync(host.Read(written.Receipt.Item.Reference, preference: 100));
        CollectionAssert.AreEqual(bytes, Convert.FromBase64String(read.ContentBase64)); Assert.IsTrue(read.Final);
        Assert.AreEqual(0L, read.Offset); Assert.AreEqual(6L, read.SelectedLength);
        Assert.AreEqual(written.Receipt.Item.Version, (await host.Client.StatAsync(new("stat", written.Receipt.Item.Reference))).Version);
    }
    [TestMethod]
    public async Task TextAndProgressiveUnicodeConcatenateExactlyWithoutInjectedSeparators()
    {
        var first = await host.Client.WriteTextAsync(new("text-0", 0, false, "\uFEFFé😀", Options: new("notes/text"), NotAfter: host.Clock.Now.AddMinutes(1)));
        var last = await host.Client.WriteTextAsync(new("text-1", 1, true, "e\u0301\r\n終", first.SessionRef));
        var request = new AspReadTextRequest("read-0", Selection: new(last.Receipt!.Item.Reference, PreferredChunkSizeBytes: 1), NotAfter: host.Clock.Now.AddMinutes(1));
        var all = new StringBuilder(); var offset = 0L; var larger = false;
        while (true)
        {
            var read = await host.Client.ReadTextAsync(request);
            Assert.AreEqual(offset, read.Offset); offset += read.ByteLength;
            larger |= read.ByteLength > 1; all.Append(read.Content);
            if (read.Final) break;
            request = new("read-" + (request.Sequence + 1), request.Sequence + 1, read.SessionRef);
        }
        Assert.AreEqual("\uFEFFé😀e\u0301\r\n終", all.ToString()); Assert.IsTrue(larger);
    }
    [TestMethod]
    public async Task InitialIntermediateAndFinalWriteReplaysNeverWriteAgain()
    {
        var initial = host.Write("notes/replay", "abc"u8.ToArray(), final: false, id: "start");
        var first = await host.Client.WriteBlobAsync(initial);
        Assert.AreEqual(first, await host.Client.WriteBlobAsync(initial));
        var middle = new AspWriteBlobRequest("middle", 1, false, Convert.ToBase64String("def"u8), first.SessionRef);
        var second = await host.Client.WriteBlobAsync(middle);
        Assert.AreEqual(second, await host.Client.WriteBlobAsync(middle));
        var end = new AspWriteBlobRequest("end", 2, true, "", first.SessionRef);
        var last = await host.Client.WriteBlobAsync(end);
        Assert.AreEqual(last, await host.Client.WriteBlobAsync(end));
        Assert.AreEqual(first, await host.Client.WriteBlobAsync(initial));
        Assert.AreEqual(6L, last.WrittenBytes);
        Assert.AreEqual("finalized", (await host.Client.WriteStatusAsync(new("status", last.SessionRef))).State);
        await AspTestHost.Error("idempotency_conflict", () => host.Client.WriteBlobAsync(middle with { ContentBase64 = "eA==" }));
        await AspTestHost.Error("sequence_conflict", () => host.Client.WriteBlobAsync(end with { RequestId = "future", Sequence = 3 }));
    }
    [TestMethod]
    public async Task ReadReplaysDoNotAdvanceAndChunkPreferenceDoesNotSelectTotalLength()
    {
        var written = await host.Client.WriteBlobAsync(host.Write("notes/read", "abcdefgh"u8.ToArray()));
        var initial = host.Read(written.Receipt!.Item.Reference, preference: 2);
        var first = await host.Client.ReadBlobAsync(initial);
        Assert.AreEqual(first, await host.Client.ReadBlobAsync(initial)); Assert.IsFalse(first.Final);
        var next = new AspReadBlobRequest("next", 1, first.SessionRef);
        var second = await host.Client.ReadBlobAsync(next);
        Assert.AreEqual("cd", Encoding.UTF8.GetString(Convert.FromBase64String(second.ContentBase64)));
        Assert.AreEqual(second, await host.Client.ReadBlobAsync(next)); Assert.AreEqual(8L, second.SelectedLength);
        await AspTestHost.Error("idempotency_conflict", () => host.Client.ReadBlobAsync(next with { RequestId = "different" }));
        await AspTestHost.Error("sequence_conflict", () => host.Client.ReadBlobAsync(next with { RequestId = "future", Sequence = 3 }));
    }
    [TestMethod]
    [DataRow(0L, null, 6L)]
    [DataRow(2L, null, 4L)]
    [DataRow(2L, 100L, 4L)]
    [DataRow(6L, null, 0L)]
    [DataRow(3L, 0L, 0L)]
    public async Task ByteRangesClipAtEofAndAllowEmptySelections(long offset, long? length, long expected)
    {
        var written = await host.Client.WriteBlobAsync(host.Write("notes/range", "abcdef"u8.ToArray()));
        var result = await host.Client.ReadBlobAsync(host.Read(written.Receipt!.Item.Reference, new(offset, length), 100));
        Assert.AreEqual(offset, result.Offset); Assert.AreEqual(expected, result.SelectedLength);
        Assert.AreEqual((int)expected, result.ByteLength); Assert.IsTrue(result.Final);
        await AspTestHost.Error("invalid_range", () => host.Client.ReadBlobAsync(host.Read(written.Receipt.Item.Reference, new(7))));
    }
    [TestMethod]
    [DataRow(1L, 1L)]
    [DataRow(0L, 1L)]
    public async Task TextByteRangesRejectBrokenUtf8Boundaries(long offset, long length)
    {
        var written = await host.Client.WriteBlobAsync(host.Write("notes/utf8", "éx"u8.ToArray()));
        await AspTestHost.Error("invalid_text", () => host.Client.ReadTextAsync(new("boundary", Selection: new(written.Receipt!.Item.Reference, new(offset, length)), NotAfter: host.Clock.Now.AddMinutes(1))));
    }
    [TestMethod]
    public async Task ConditionsIntegrityAndModesAreEnforcedAtPublication()
    {
        var created = await host.Client.WriteBlobAsync(host.Write("notes/conditions", "one"u8.ToArray()));
        var item = created.Receipt!.Item;
        await AspTestHost.Error("conflict", () => host.Client.WriteBlobAsync(host.Write("notes/conditions", "two"u8.ToArray())));
        var replacement = host.Write("notes/conditions", "two"u8.ToArray()) with { Options = new("notes/conditions", "replace", item.Version) };
        var replaced = await host.Client.WriteBlobAsync(replacement);
        Assert.AreNotEqual(item.Version, replaced.Receipt!.Item.Version);
        await AspTestHost.Error("version_unavailable", () => host.Client.ReadBlobAsync(host.Read(item.Reference)));
        var mismatch = host.Write("notes/mismatch", "x"u8.ToArray()) with { Options = new("notes/mismatch", ExpectedLength: 2) };
        await AspTestHost.Error("integrity_mismatch", () => host.Client.WriteBlobAsync(mismatch));
        Assert.IsFalse((await host.Client.ListAsync(new("list", "notes/", 10))).Objects.Any(o => o.Key == "notes/mismatch"));
        var appended = await host.Client.WriteBlobAsync(host.Write("notes/conditions", "!"u8.ToArray()) with { Options = new("notes/conditions", "append") });
        Assert.AreEqual(1L, appended.WrittenBytes); Assert.AreEqual(4L, appended.Receipt!.Item.Length);
    }
    [TestMethod]
    public async Task AbortAndEarlyCloseAreIdempotentAndDoNotInventSuccess()
    {
        var open = await host.Client.WriteBlobAsync(host.Write("notes/abort", "partial"u8.ToArray(), false));
        var aborted = await host.Client.AbortWriteAsync(new("abort", open.SessionRef));
        Assert.AreEqual("aborted", aborted.State); Assert.IsNull(aborted.Receipt);
        Assert.AreEqual(aborted, await host.Client.AbortWriteAsync(new("abort-again", open.SessionRef)));
        Assert.IsEmpty((await host.Client.ListAsync(new("list", "notes/", 10))).Objects);
        var item = await host.Client.WriteBlobAsync(host.Write("notes/read", "abcdef"u8.ToArray()));
        var read = await host.Client.ReadBlobAsync(host.Read(item.Receipt!.Item.Reference, preference: 1));
        Assert.AreEqual("closed", (await host.Client.CloseReadAsync(new("close", read.SessionRef))).State);
        await AspTestHost.Error("sequence_conflict", () => host.Client.ReadBlobAsync(new("more", 1, read.SessionRef)));
    }
    [TestMethod]
    public async Task StreamAdaptersTransferThroughTheSameImplicitSessionMethods()
    {
        var bytes = Enumerable.Range(0, 20_000).Select(i => (byte)(i % 251)).ToArray();
        await using var source = new MemoryStream(bytes);
        await using var writer = host.Client.OpenWriteStream(new("notes/stream"), "stream", host.Clock.Now.AddMinutes(2));
        await source.CopyToAsync(writer); var receipt = await writer.CompleteAsync();
        await using var reader = host.Client.OpenReadStream(new(receipt.Item.Reference, PreferredChunkSizeBytes: 4096), "stream-read", host.Clock.Now.AddMinutes(2));
        await using var destination = new MemoryStream(); await reader.CopyToAsync(destination);
        CollectionAssert.AreEqual(bytes, destination.ToArray());
    }
}
