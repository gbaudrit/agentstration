using System.Text;
using Agentstration.Asp.Abstractions;
using Agentstration.Infrastructure.Storage;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class LocalStorageContentTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "agentstration-local-storage-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock clock = new();
    private static readonly AspAccessPolicy Policy = new(true, true, true, true, "notes/", 65536, 65536, 100);
    private LocalStoragePaths Paths => new(new(root));
    private LocalStorageObjectProvider Provider => new(Paths, clock);
    private static StorageRevisionReference Store(Guid? workspace = null, Guid? uid = null) => new(
        new(ResourceScopeRef.Workspace(workspace ?? Guid.NewGuid()), ResourceNamespace.Default, "Storage", "local"),
        uid ?? Guid.NewGuid(), 1, "sha256:binding");
    private static AspAccessContext Access(StorageRevisionReference store) => new(
        new(store.StorageUid.ToString("N"), "builtin-local", store.BindingDigest), "caller", "access-v1", Policy);
    private LocalContentWrite Write(string id, string key = "notes/item", string value = "hello", string? version = null) =>
        new(id, clock.GetUtcNow().AddMinutes(1), key, Encoding.UTF8.GetBytes(value), version);

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    [TestMethod]
    public async Task RetirementRejectsReplacedContentAndKeepsTheExactWriteReceipt()
    {
        var store = Store(); var access = Access(store); var request = Write("retirement");
        var original = await Provider.WriteAsync(store, access, request, default);
        var exact = new StorageObject(original.Key, original.Version, original.Length, original.Sha256, "opaque");
        var replacement = await Provider.WriteAsync(store, access, Write("replace", value: "new content", version: original.Version), default);
        Assert.AreEqual("version_unavailable", (await Assert.ThrowsAsync<AspException>(() => Provider.RetireAsync(store, access, exact, default))).Code);
        Assert.AreEqual(replacement, await Provider.StatAsync(store, access, replacement.Key, replacement.Version, default));
        Assert.IsTrue(await Provider.RetireAsync(store, access, new(replacement.Key, replacement.Version, replacement.Length, replacement.Sha256, "opaque"), default));
        Assert.AreEqual(original, await Provider.WriteAsync(store, access, request, default));
        Assert.IsEmpty((await Provider.ListAsync(store, access, "notes/", null, 100, default)).Objects);
    }

    [TestMethod]
    public async Task ContentAndExactWriteReceiptsSurviveProviderRestart()
    {
        var store = Store(); var access = Access(store); var request = Write("create");
        var first = await Provider.WriteAsync(store, access, request, default);
        var restarted = Provider;
        var retried = await restarted.WriteAsync(store, access, request, default);
        Assert.AreEqual(first, retried);
        var read = await restarted.ReadAsync(store, access, first.Key, first.Version, 1, 3, default);
        Assert.AreEqual("ell", Encoding.UTF8.GetString(read.Content));
        Assert.IsFalse(read.EndOfObject);
        var stat = await restarted.StatAsync(store, access, first.Key, first.Version, default);
        Assert.AreEqual(first, stat);
        Assert.AreEqual("hello", Encoding.UTF8.GetString((await restarted.ReadAsync(store, access, first.Key, first.Version, 0, 100, default)).Content));
    }

    [TestMethod]
    public async Task SameWorkspaceDifferentStorageAndDifferentWorkspaceHaveSeparateDirectories()
    {
        var first = Store();
        var second = Store(first.Storage.ScopeRef.TargetId);
        var third = Store(uid: first.StorageUid);
        var one = await Provider.WriteAsync(first, Access(first), Write("write", value: "one"), default);
        await Provider.WriteAsync(second, Access(second), Write("write", value: "two"), default);
        await Provider.WriteAsync(third, Access(third), Write("write", value: "three"), default);
        Assert.AreNotEqual(Paths.DirectoryFor(first), Paths.DirectoryFor(second));
        Assert.AreNotEqual(Paths.DirectoryFor(first), Paths.DirectoryFor(third));
        Assert.AreEqual("one", Encoding.UTF8.GetString((await Provider.ReadAsync(first, Access(first), one.Key, one.Version, 0, 10, default)).Content));
        var foreign = await Assert.ThrowsAsync<AspException>(() => Provider.ReadAsync(second, Access(second), one.Key, one.Version, 0, 10, default));
        Assert.AreEqual("version_unavailable", foreign.Code);
        Assert.AreEqual(Paths.DirectoryFor(first), Paths.DirectoryFor(first with { Storage = first.Storage with { Name = "renamed" } }));
    }

    [TestMethod]
    public async Task ConditionalWritesAndConcurrentRequestsCommitOnce()
    {
        var store = Store(); var access = Access(store); var request = Write("same-request");
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Provider.WriteAsync(store, access, request, default)));
        Assert.HasCount(1, replies.Select(r => r.Version).Distinct().ToArray());
        var conflict = await Assert.ThrowsAsync<AspException>(() => Provider.WriteAsync(store, access, Write("other-request"), default));
        Assert.AreEqual("conflict", conflict.Code);
        var changed = await Assert.ThrowsAsync<AspException>(() => Provider.WriteAsync(store, access, request with { Content = [42] }, default));
        Assert.AreEqual("idempotency_conflict", changed.Code);
        var replacement = await Provider.WriteAsync(store, access, Write("replacement", value: "new", version: replies[0].Version), default);
        Assert.AreNotEqual(replies[0].Version, replacement.Version);
        var stale = await Assert.ThrowsAsync<AspException>(() => Provider.ReadAsync(store, access, replacement.Key, replies[0].Version, 0, 100, default));
        Assert.AreEqual("version_unavailable", stale.Code); // No unimplemented stable-version claim.
    }

    [TestMethod]
    [DataRow("../outside")]
    [DataRow("notes/../../outside")]
    [DataRow("/absolute")]
    [DataRow("notes\\outside")]
    [DataRow("foreign/item")]
    public async Task TraversalAndEnvelopeEscapesFailBeforeWriting(string key)
    {
        var store = Store();
        await Assert.ThrowsAsync<AspException>(() => Provider.WriteAsync(store, Access(store), Write("invalid", key), default));
        Assert.IsFalse(File.Exists(Path.Combine(root, "outside")));
    }

    [TestMethod]
    public async Task LinksAtStorageDirectoryAndContentFileAreRejected()
    {
        var store = Store();
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var directory = Paths.DirectoryFor(store);
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        Directory.CreateSymbolicLink(directory, outside);
        var linked = await Assert.ThrowsAsync<AspException>(() => Provider.WriteAsync(store, Access(store), Write("link"), default));
        Assert.AreEqual("storage_path_unsafe", linked.Code);
        Directory.Delete(directory);
        Directory.CreateDirectory(directory);
        File.CreateSymbolicLink(Path.Combine(directory, "objects.json"), Path.Combine(outside, "missing.json"));
        await Assert.ThrowsAsync<AspException>(() => Provider.ListAsync(store, Access(store), "notes/", null, 10, default));
        Assert.IsFalse(File.Exists(Path.Combine(outside, "missing.json")));
        File.Delete(Path.Combine(directory, "objects.json"));
    }

    [TestMethod]
    public async Task ReadsPaginationRestrictionsCancellationAndExpiredWritesAreBounded()
    {
        var store = Store(); var access = Access(store);
        foreach (var key in new[] { "notes/c", "notes/a", "notes/b" })
            await Provider.WriteAsync(store, access, Write(key, key), default);
        var page = await Provider.ListAsync(store, access, "notes/", null, 1, default);
        Assert.AreEqual("notes/a", page.Objects.Single().Key);
        Assert.AreEqual("notes/a", page.NextKey);
        Assert.AreEqual("notes/b", (await Provider.ListAsync(store, access, "notes/", page.NextKey, 1, default)).Objects.Single().Key);
        var restricted = access with { Policy = access.Policy with { KeyFrom = "notes/b", KeyBefore = "notes/c" } };
        Assert.AreEqual("notes/b", (await Provider.ListAsync(store, restricted, "notes/", null, 10, default)).Objects.Single().Key);
        await Assert.ThrowsAsync<AspException>(() => Provider.ListAsync(store, access, "notes/", null, 101, default));
        await Assert.ThrowsAsync<AspException>(() => Provider.WriteAsync(store, access with { Policy = Policy with { CanWrite = false } }, Write("denied"), default));
        await Assert.ThrowsAsync<AspException>(() => Provider.WriteAsync(store, access, Write("expired") with { NotAfter = clock.GetUtcNow() }, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Provider.WriteAsync(store, access, Write("cancelled"), cancelled.Token));
    }

    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    }
}
