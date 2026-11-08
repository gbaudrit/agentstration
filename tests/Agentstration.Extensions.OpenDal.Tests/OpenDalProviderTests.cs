using System.Text.Json;
using Agentstration.Asp.Abstractions;

namespace Agentstration.Extensions.OpenDal.Tests;

[TestClass]
public sealed class OpenDalProviderTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "opendal-provider-" + Guid.NewGuid().ToString("N"));
    private readonly Clock clock = new();
    private readonly OpenDalExecutionGate execution = new();
    private static readonly AspStoreRef Store = new("store", "binding", "revision");
    private static readonly AspAccessContext Context = new(Store, "subject", "grant",
        new(true, true, true, true, "notes/", 65536, 65536, 100));
    private OpenDalFileSystemProvider Provider => new(root, Store, clock, execution);

    [TestMethod]
    public void NativeDependencyStaysInsideTheAutonomousExtension()
    {
        var dependencies = typeof(OpenDalBindingFactory).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.IsTrue(dependencies.Contains("OpenDAL", StringComparer.Ordinal));
        Assert.IsFalse(dependencies.Any(name => name.StartsWith("Agentstration.", StringComparison.Ordinal)
            && !name.StartsWith("Agentstration.Asp.", StringComparison.Ordinal)
            && !name.StartsWith("Agentstration.Aep.", StringComparison.Ordinal)));
    }

    [TestCleanup]
    public void Cleanup()
    {
        execution.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [TestMethod]
    public async Task ReceiptCapacityNeverEvictsLiveReceiptsAndExpiryCannotReplay()
    {
        var provider = Provider;
        var first = Write("first", "notes/a");
        var initial = await provider.WriteAsync(Context, first, default);
        var current = initial;
        for (var i = 1; i < OpenDalFileSystemProvider.MaximumReceipts; i++)
            current = await provider.WriteAsync(Context, Write("write-" + i, "notes/a") with { ExpectedVersion = current.Version }, default);
        await Error("capacity_exceeded", async () => await provider.WriteAsync(Context,
            Write("overflow", "notes/a") with { ExpectedVersion = current.Version }, default));
        Assert.AreEqual(initial.Version, (await Provider.WriteAsync(Context, first, default)).Version);
        clock.Now = clock.Now.AddMinutes(2);
        await Error("request_expired", async () => await Provider.WriteAsync(Context, first, default));
        await Provider.WriteAsync(Context, Write("after-expiry", "notes/a") with { ExpectedVersion = current.Version }, default);
        await Error("request_expired", async () => await Provider.WriteAsync(Context, first, default));
    }

    [TestMethod]
    public async Task ObjectAndByteCapacityPreserveExistingDataOnRefusal()
    {
        for (var i = 0; i < OpenDalFileSystemProvider.MaximumObjects; i++)
            await Provider.WriteAsync(Context, Write("write-" + i, $"notes/{i:D3}") with { Content = new byte[65536] }, default);
        await Error("capacity_exceeded", async () => await Provider.WriteAsync(Context, Write("overflow", "notes/overflow"), default));
        var first = await Provider.ListAsync(Context, "notes/", null, 100, default);
        var second = await Provider.ListAsync(Context, "notes/", first.NextKey, 100, default);
        Assert.HasCount(128, first.Objects.Concat(second.Objects).ToArray());
        Assert.AreEqual((long)OpenDalFileSystemProvider.MaximumContentBytes, first.Objects.Concat(second.Objects).Sum(o => o.Length));
    }

    [TestMethod]
    public async Task ProviderEnforcesExactIdentityAndRestrictionsWithoutMcp()
    {
        await Error("forbidden", async () => await Provider.ListAsync(Context with { Store = Store with { BindingId = "forged" } }, "notes/", null, 1, default));
        await Error("forbidden", async () => await Provider.WriteAsync(Context, Write("outside", "other/key"), default));
        await Error("forbidden", async () => await Provider.ReadAsync(Context with { Policy = Context.Policy with { CanRead = false } }, "notes/a", "1", 0, 1, default));
        await Error("invalid_request", async () => await Provider.WriteAsync(Context, Write("traversal", "notes/../escape"), default));
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public async Task ConcurrentProviderInstancesCommitOneConditionalCreate()
    {
        var original = Write("same-request", "notes/a");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            await Provider.WriteAsync(Context, original, default))));
        Assert.HasCount(1, results.Select(r => r.Version).Distinct().ToArray());
        Assert.HasCount(1, (await Provider.ListAsync(Context, "notes/", null, 10, default)).Objects);
    }

    [TestMethod]
    public async Task CancellationWhileWaitingForFileLockNeverCommits()
    {
        await Provider.WriteAsync(Context, Write("initial", "notes/a"), default);
        var directory = Directory.GetDirectories(root).Single();
        await using var held = new FileStream(Path.Combine(directory, "write.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var ct = new CancellationTokenSource();
        var waiting = Provider.WriteAsync(Context, Write("waiting", "notes/b"), ct.Token).AsTask();
        await ct.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
        await held.DisposeAsync();
        Assert.HasCount(1, (await Provider.ListAsync(Context, "notes/", null, 10, default)).Objects);
    }

    [TestMethod]
    public async Task IntegrityAndOversizedStateAreRefusedWithRedactedErrors()
    {
        await Provider.WriteAsync(Context, Write("initial", "notes/a"), default);
        var path = Directory.GetFiles(root, "objects.json", SearchOption.AllDirectories).Single();
        var text = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, text.Replace("AQ==", "Ag==", StringComparison.Ordinal));
        await Error("invalid_result", async () => await Provider.ListAsync(Context, "notes/", null, 10, default));
        await using (var oversized = new FileStream(path, FileMode.Create, FileAccess.Write)) oversized.SetLength(16 * 1024 * 1024 + 1);
        await Error("invalid_result", async () => await Provider.ListAsync(Context, "notes/", null, 10, default));
        await File.WriteAllTextAsync(path, "{");
        var exception = await Assert.ThrowsAsync<AspException>(async () => await Provider.ListAsync(Context, "notes/", null, 10, default));
        Assert.DoesNotContain(root, exception.Message);
    }

    [TestMethod]
    public async Task SymbolicAndDanglingLinksCannotRedirectState()
    {
        Directory.CreateDirectory(root);
        var outside = root + "-outside";
        Directory.CreateDirectory(outside);
        try
        {
            var partition = AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new { Store.StoreId, Store.BindingId }, AspProtocol.JsonOptions));
            var link = Path.Combine(root, partition);
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            { Assert.Inconclusive("The Windows runner does not allow creating symbolic links."); }
            await Error("forbidden", async () => await Provider.WriteAsync(Context, Write("redirect", "notes/a"), default));
            Directory.Delete(link);
            Directory.CreateDirectory(link);
            File.CreateSymbolicLink(Path.Combine(link, "objects.json"), Path.Combine(outside, "absent.json"));
            await Error("forbidden", async () => await Provider.ListAsync(Context, "notes/", null, 1, default));
            Assert.IsEmpty(Directory.GetFileSystemEntries(outside));
        }
        finally { Directory.Delete(outside, true); }
    }

    private AspProviderWrite Write(string id, string key) => new(id, clock.Now.AddMinutes(2), key, [1], null);
    private static async Task Error(string code, Func<Task> action) =>
        Assert.AreEqual(code, (await Assert.ThrowsAsync<AspException>(action)).Code);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
