using System.Security.Cryptography;
using System.Text;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;

namespace Agentstration.Asp.Tests;

[TestClass]
public sealed class AspLargeTransferTests
{
    [TestMethod]
    public async Task RealMcpProgressiveDiskTransferExceedsFormerWholeContentLimitsWithFixedBuffers()
    {
        using var provider = new DiskProvider();
        await using var host = new AspTestHost(provider); await host.StartAsync();
        var block = new byte[4096]; for (var i = 0; i < block.Length; i++) block[i] = (byte)(i % 251);
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = host.Client.OpenWriteStream(new("notes/large"), "large-write", host.Clock.Now.AddMinutes(4));
        for (var i = 0; i < 2304; i++) { await output.WriteAsync(block); expected.AppendData(block); }
        var written = await output.CompleteAsync();
        var digest = Convert.ToHexStringLower(expected.GetHashAndReset());
        Assert.AreEqual(9 * 1024 * 1024L, written.WrittenBytes); Assert.AreEqual(digest, written.Sha256);
        await using var input = host.Client.OpenReadStream(new(written.Item.Reference, PreferredChunkSizeBytes: 4096), "large-read", host.Clock.Now.AddMinutes(4));
        using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4096]; long count = 0; int read;
        while ((read = await input.ReadAsync(buffer)) > 0) { count += read; actual.AppendData(buffer, 0, read); }
        Assert.AreEqual(written.WrittenBytes, count); Assert.AreEqual(digest, Convert.ToHexStringLower(actual.GetHashAndReset()));
    }
    [TestMethod]
    public async Task EmptySingleCallAndEmptyFinalChunkRemainDistinctValidPaths()
    {
        await using var host = new AspTestHost(); await host.StartAsync();
        var empty = await host.Client.WriteBlobAsync(host.Write("notes/empty", []));
        Assert.IsTrue(empty.Final); Assert.AreEqual(0L, empty.WrittenBytes);
        var read = await host.Client.ReadBlobAsync(host.Read(empty.Receipt!.Item.Reference));
        Assert.IsTrue(read.Final); Assert.AreEqual("", read.ContentBase64); Assert.AreEqual(0L, read.SelectedLength);
        var text = await host.Client.WriteTextAsync(new("empty-text", 0, true, "", Options: new("notes/empty-text"), NotAfter: host.Clock.Now.AddMinutes(1)));
        var result = await host.Client.ReadTextAsync(new("empty-read", Selection: new(text.Receipt!.Item.Reference), NotAfter: host.Clock.Now.AddMinutes(1)));
        Assert.AreEqual("", result.Content); Assert.IsTrue(result.Final);
    }
    [TestMethod]
    public async Task InvalidUtf8AndHardCharacterBoundariesCannotProduceFinalTextSuccess()
    {
        await using var host = new AspTestHost(); await host.StartAsync();
        var invalid = await host.Client.WriteBlobAsync(host.Write("notes/invalid", [0xFF]));
        await AspTestHost.Error("invalid_text", () => host.Client.ReadTextAsync(new("invalid-text", Selection: new(invalid.Receipt!.Item.Reference), NotAfter: host.Clock.Now.AddMinutes(1))));
    }
}
