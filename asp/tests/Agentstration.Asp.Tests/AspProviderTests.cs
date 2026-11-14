using System.Text.Json;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Agentstration.Asp.Server;
using Asp.Samples.LocalStorage;

namespace Agentstration.Asp.Tests;

[TestClass]
public sealed class AspProviderTests
{
    [TestMethod]
    public async Task NativeFailureAfterCommitNeverBecomesAnInventedFinalReceipt()
    {
        var provider = new FailingProvider();
        await using var host = new AspTestHost(provider); await host.StartAsync();
        var first = await host.Client.WriteBlobAsync(host.Write("notes/unknown", [1], false));
        var end = new AspWriteBlobRequest("final", 1, true, "", first.SessionRef);
        await AspTestHost.Error("unknown_outcome", () => host.Client.WriteBlobAsync(end));
        await AspTestHost.Error("unknown_outcome", () => host.Client.WriteBlobAsync(end));
        Assert.AreEqual(1, provider.Commits);
        var status = await host.Client.WriteStatusAsync(new("status", first.SessionRef));
        Assert.AreEqual("unknown", status.State); Assert.IsNull(status.Receipt);
        Assert.HasCount(1, (await host.Client.ListAsync(new("count", "notes/", 10))).Objects);
    }
    [TestMethod]
    public async Task ExpiryReleasesProviderHandleWithoutAnotherContentCall()
    {
        var provider = new TrackingProvider();
        await using var host = new AspTestHost(provider); await host.StartAsync();
        _ = await host.Client.WriteBlobAsync(host.Write("notes/expiring", [1], false));
        host.Clock.Now = host.Clock.Now.AddMinutes(3);
        await provider.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(provider.Disposed);
    }
    private sealed class TrackingProvider : LimitedProvider
    {
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override AspTransferCapabilities Transfers => Inner.Transfers;
        public override async ValueTask<IAspWriteHandle> OpenWriteAsync(AspAccessContext c, AspWriteOptions o, CancellationToken ct) => new Tracked(this, await Inner.OpenWriteAsync(c, o, ct));
        private sealed class Tracked(TrackingProvider owner, IAspWriteHandle inner) : IAspWriteHandle
        {
            public Stream Content => inner.Content;
            public ValueTask<AspProviderObject> CompleteAsync(CancellationToken ct) => inner.CompleteAsync(ct);
            public ValueTask AbortAsync(CancellationToken ct) => inner.AbortAsync(ct);
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); owner.Disposed = true; owner.Aborted.TrySetResult(); }
        }
    }
    [TestMethod]
    public async Task ProviderCapabilityVariantRefusesUnsupportedOptionsAndProgressiveWrites()
    {
        await using var host = new AspTestHost(new LimitedProvider()); await host.StartAsync();
        await AspTestHost.Error("unsupported_option", () => host.Client.WriteBlobAsync(host.Write("notes/metadata", [1]) with { Options = new("notes/metadata", Metadata: new(ContentType: "text/plain")) }));
        await AspTestHost.Error("unsupported_operation", () => host.Client.WriteBlobAsync(host.Write("notes/progressive", [1], false)));
        var done = await host.Client.WriteBlobAsync(host.Write("notes/complete", [1])); Assert.IsTrue(done.Final);
    }
    [TestMethod]
    public async Task MultipleConcurrentIdenticalInitialCallsProduceOnePublication()
    {
        await using var host = new AspTestHost(); await host.StartAsync();
        var request = host.Write("notes/concurrent", [1, 2, 3]);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.Client.WriteBlobAsync(request)));
        Assert.HasCount(1, results.Select(r => r.Receipt!.Item.Version).Distinct().ToArray());
    }
    [TestMethod]
    public void StandaloneSdkDoesNotDependOnProvidersOrProductModules()
    {
        foreach (var assembly in new[] { typeof(AspProtocol).Assembly, typeof(AspClient).Assembly, typeof(AspMcpServer).Assembly })
            Assert.IsFalse(assembly.GetReferencedAssemblies().Any(r => r.Name!.StartsWith("Agentstration.", StringComparison.Ordinal) && !r.Name.StartsWith("Agentstration.Asp.", StringComparison.Ordinal) || r.Name!.Contains("OpenDAL", StringComparison.Ordinal)));
    }
    [TestMethod]
    public void CanonicalSchemasMatchEveryPublishedOperation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Asp.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        foreach (var operation in new[] { "describe", "list", "stat", "read_text", "read_blob", "write_text", "write_blob", "close_read", "abort_write", "write_status" })
            foreach (var direction in new[] { "input", "output" })
            {
                using var snapshot = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "docs/schemas", operation + "." + direction + ".json")));
                Assert.IsTrue(JsonElement.DeepEquals(snapshot.RootElement, direction == "input" ? AspSchemas.Input(operation) : AspSchemas.Output(operation)));
            }
    }
    private class LimitedProvider : IAspObjectReader, IAspObjectWriter
    {
        protected readonly MemoryObjectProvider Inner = new();
        public string ProviderId => Inner.ProviderId;
        public string ProviderVersion => Inner.ProviderVersion;
        public JsonElement ConfigurationSchema => Inner.ConfigurationSchema;
        public virtual AspTransferCapabilities Transfers => Inner.Transfers with { ProgressiveWrite = false, Metadata = false };
        public ValueTask<AspProviderPage> ListAsync(AspAccessContext c, string p, string? a, int l, CancellationToken ct) => Inner.ListAsync(c, p, a, l, ct);
        public ValueTask<AspProviderObject> StatAsync(AspAccessContext c, string k, string? v, string? etag, CancellationToken ct) => Inner.StatAsync(c, k, v, etag, ct);
        public ValueTask<AspReadHandle> OpenReadAsync(AspAccessContext c, string k, string? v, string? etag, AspByteRange? r, CancellationToken ct) => Inner.OpenReadAsync(c, k, v, etag, r, ct);
        public virtual ValueTask<IAspWriteHandle> OpenWriteAsync(AspAccessContext c, AspWriteOptions o, CancellationToken ct) => Inner.OpenWriteAsync(c, o, ct);
    }
    private sealed class FailingProvider : LimitedProvider
    {
        public int Commits { get; set; }
        public override AspTransferCapabilities Transfers => Inner.Transfers;
        public override async ValueTask<IAspWriteHandle> OpenWriteAsync(AspAccessContext c, AspWriteOptions o, CancellationToken ct) => new Failure(this, await Inner.OpenWriteAsync(c, o, ct));
        private sealed class Failure(FailingProvider owner, IAspWriteHandle inner) : IAspWriteHandle
        {
            public Stream Content => inner.Content;
            public async ValueTask<AspProviderObject> CompleteAsync(CancellationToken ct)
            {
                _ = await inner.CompleteAsync(ct); owner.Commits++;
                throw new OperationCanceledException("The native response was lost after publication.");
            }
            public ValueTask AbortAsync(CancellationToken ct) => inner.AbortAsync(ct);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
