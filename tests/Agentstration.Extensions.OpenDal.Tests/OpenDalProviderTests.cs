using System.Text.Json;
using Agentstration.Asp.Abstractions;
using OpenDAL;

namespace Agentstration.Extensions.OpenDal.Tests;

[TestClass]
public sealed class OpenDalProviderTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "opendal-provider-" + Guid.NewGuid().ToString("N"));
    private readonly OpenDalExecutionGate execution = new();
    private static readonly AspStoreRef Store = new("store", "binding", "revision");
    private static readonly AspAccessContext Context = new(Store, "subject", "grant", new(true, true, true, true, "notes/", 65536, 65536, 100));
    private OpenDalObjectProvider Provider => OpenDalFileSystem.Create(root, Store, execution);
    [TestCleanup]
    public void Cleanup() { execution.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    [TestMethod]
    public void NativeDependencyStaysInsideTheAutonomousExtension()
    {
        var dependencies = typeof(OpenDalBindingFactory).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.IsTrue(dependencies.Contains("OpenDAL", StringComparer.Ordinal));
        Assert.IsFalse(dependencies.Any(n => n.StartsWith("Agentstration.", StringComparison.Ordinal)
            && !n.StartsWith("Agentstration.Asp.", StringComparison.Ordinal) && !n.StartsWith("Agentstration.Aep.", StringComparison.Ordinal)));
    }
    [TestMethod]
    public async Task ProviderIndependentlyEnforcesExactStoreAuthorityAndKeyRestrictions()
    {
        var provider = Provider;
        await Error("forbidden", async () => await provider.OpenWriteAsync(Context with { Store = Store with { Revision = "other" } }, new("notes/a"), default));
        await Error("forbidden", async () => await provider.OpenWriteAsync(Context, new("outside/a"), default));
        await Error("invalid_request", async () => await provider.OpenWriteAsync(Context, new("notes/../escape"), default));
        await Error("forbidden", async () => await provider.ListAsync(Context, "", null, 1, default));
        await Error("forbidden", async () => await provider.OpenWriteAsync(Context with { Policy = Context.Policy with { CanWrite = false } }, new("notes/a"), default));
        await Error("unsupported_option", async () => await provider.OpenWriteAsync(Context, new("notes/a", ExpectedVersion: "fake"), default));
        await Error("unsupported_option", async () => await provider.OpenWriteAsync(Context, new("notes/a", Mode: "append"), default));
    }
    [TestMethod]
    public async Task NativeConditionalCreatesAcrossInstancesDoNotOverwriteEachOther()
    {
        async Task<bool> Create(byte value)
        {
            try { await using var writer = await Provider.OpenWriteAsync(Context, new("notes/race"), default); await writer.Content.WriteAsync(new byte[] { value }); await writer.CompleteAsync(default); return true; }
            catch (AspException e) when (e.Code == "conflict") { return false; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Create((byte)i)));
        Assert.AreEqual(1, results.Count(r => r));
        await using var input = await Provider.OpenReadAsync(Context, "notes/race", null, null, null, default);
        Assert.AreEqual(1L, input.Length);
    }
    [TestMethod]
    public async Task MoreThanFormerObjectQuotaUsesIndependentNativeKeysWithoutAStateFile()
    {
        var provider = Provider;
        for (var i = 0; i < 129; i++)
        {
            await using var writer = await provider.OpenWriteAsync(Context, new($"notes/{i:D3}"), default);
            await writer.Content.WriteAsync(new byte[] { (byte)i }); await writer.CompleteAsync(default);
        }
        var first = await provider.ListAsync(Context, "notes/", null, 100, default);
        var second = await provider.ListAsync(Context, "notes/", first.NextKey, 100, default);
        Assert.AreEqual(129, first.Objects.Count + second.Objects.Count);
        Assert.IsEmpty(Directory.GetFiles(root, "objects.json", SearchOption.AllDirectories));
        Assert.AreEqual(129, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
    }
    [TestMethod]
    public async Task NativeMemoryCapabilityVariantUsesBoundedSingleCallWrites()
    {
        using var native = new Operator("memory");
        var provider = new OpenDalObjectProvider(Store, "fixture.memory", JsonSerializer.SerializeToElement(new { type = "object" }), native.Duplicate, execution);
        Assert.IsFalse(provider.Transfers.ProgressiveWrite);
        var bytes = new byte[65536]; bytes[^1] = 255;
        await using (var writer = await provider.OpenWriteAsync(Context, new("notes/a"), default)) { await writer.Content.WriteAsync(bytes); var item = await writer.CompleteAsync(default); Assert.AreEqual(bytes.Length, item.Length); }
        await using var input = await provider.OpenReadAsync(Context, "notes/a", null, null, null, default);
        using var output = new MemoryStream(); await input.Content.CopyToAsync(output); CollectionAssert.AreEqual(bytes, output.ToArray());
    }
    [TestMethod]
    public async Task SymbolicAndDanglingLinksCannotRedirectPhysicalKeys()
    {
        if (OperatingSystem.IsWindows()) return; // CI Windows accounts need not have link creation rights.
        var provider = Provider;
        var data = Directory.GetDirectories(root, "data", SearchOption.AllDirectories).Single();
        Directory.CreateDirectory(Path.Combine(data, "notes"));
        File.CreateSymbolicLink(Path.Combine(data, "notes", "linked"), Path.Combine(root, "missing-target"));
        await Error("forbidden", async () => await provider.OpenWriteAsync(Context, new("notes/linked"), default));
        Directory.CreateSymbolicLink(Path.Combine(data, "notes", "external"), Path.GetTempPath());
        await Error("forbidden", async () => await provider.OpenReadAsync(Context, "notes/external/value", null, null, null, default));
        File.Delete(Path.Combine(data, "notes", "linked")); Directory.Delete(Path.Combine(data, "notes", "external"));
    }
    private static async Task Error(string code, Func<Task> operation) => Assert.AreEqual(code, (await Assert.ThrowsAsync<AspException>(operation)).Code);
}
