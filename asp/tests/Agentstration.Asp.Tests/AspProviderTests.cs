using System.Text.Json;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Agentstration.Asp.Server;
using Asp.Samples.LocalStorage;

namespace Agentstration.Asp.Tests;

[TestClass]
public sealed class AspProviderTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public async Task BoundedReceiptLedgerRefusesCapacityWithoutLosingLiveIdempotence()
    {
        var clock = new Clock();
        var provider = new MemoryObjectProvider(clock);
        var context = new AspAccessContext(SampleAccess.FirstStore, "subject", "grant", SampleAccess.Policy);
        var first = new AspProviderWrite("first", clock.Now.AddMinutes(2), "notes/a", [1], null);
        var metadata = await provider.WriteAsync(context, first, CancellationToken.None);
        for (var i = 1; i < 128; i++)
            metadata = await provider.WriteAsync(context, new($"write-{i}", clock.Now.AddMinutes(2), "notes/a", [(byte)i], metadata.Version), CancellationToken.None);
        await AspConformanceTests.ErrorAsync("capacity_exceeded", async () => await provider.WriteAsync(context,
            new("overflow", clock.Now.AddMinutes(2), "notes/a", [1], metadata.Version), CancellationToken.None));
        Assert.AreEqual("1", (await provider.WriteAsync(context, first, CancellationToken.None)).Version);
        Assert.AreEqual(metadata.Version, (await provider.StatAsync(context, "notes/a", metadata.Version, CancellationToken.None)).Version);
        clock.Now = clock.Now.AddMinutes(2);
        await AspConformanceTests.ErrorAsync("request_expired", async () => await provider.WriteAsync(context, first, CancellationToken.None));
        var next = await provider.WriteAsync(context, new("after-expiry", clock.Now.AddMinutes(2), "notes/a", [2], metadata.Version), CancellationToken.None);
        Assert.AreEqual("129", next.Version);
        await AspConformanceTests.ErrorAsync("request_expired", async () => await provider.WriteAsync(context, first, CancellationToken.None));
    }

    [TestMethod]
    public async Task ProviderEnforcesAuthorityEvenWithoutMcpAdapter()
    {
        var clock = new Clock();
        var provider = new MemoryObjectProvider(clock);
        var context = new AspAccessContext(SampleAccess.FirstStore, "subject", "grant", SampleAccess.Policy);
        var written = await provider.WriteAsync(context, new("write", clock.Now.AddMinutes(2), "notes/a", [1], null), CancellationToken.None);
        await AspConformanceTests.ErrorAsync("not_found", async () => await provider.ReadAsync(context with { Store = SampleAccess.SecondStore }, "notes/a", written.Version, 0, 10, CancellationToken.None));
        await AspConformanceTests.ErrorAsync("forbidden", async () => await provider.WriteAsync(context, new("outside", clock.Now.AddMinutes(2), "other/a", [1], null), CancellationToken.None));
        await AspConformanceTests.ErrorAsync("forbidden", async () => await provider.ReadAsync(context with { Policy = context.Policy with { CanRead = false } }, "notes/a", written.Version, 0, 10, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await provider.WriteAsync(context, new("cancelled", clock.Now.AddMinutes(2), "notes/b", [1], null), cancelled.Token));
        Assert.HasCount(1, (await provider.ListAsync(context, "notes/", null, 10, CancellationToken.None)).Objects);
    }

    [TestMethod]
    public async Task TotalByteCapacityIsBoundedAndRefusalLeavesExistingObjectsIntact()
    {
        var clock = new Clock();
        var provider = new MemoryObjectProvider(clock);
        var context = new AspAccessContext(SampleAccess.FirstStore, "subject", "grant",
            SampleAccess.Policy with { MaxWriteBytes = AspProtocol.MaxContentBytes });
        for (var index = 0; index < 16; index++)
            await provider.WriteAsync(context, new($"write-{index}", clock.Now.AddMinutes(2), $"notes/{index:D2}", new byte[65536], null), CancellationToken.None);
        await AspConformanceTests.ErrorAsync("capacity_exceeded", async () => await provider.WriteAsync(context,
            new("overflow", clock.Now.AddMinutes(2), "notes/overflow", [1], null), CancellationToken.None));
        var first = await provider.ListAsync(context, "notes/", null, 10, CancellationToken.None);
        Assert.HasCount(10, first.Objects);
        var second = await provider.ListAsync(context, "notes/", first.NextKey, 10, CancellationToken.None);
        Assert.HasCount(6, second.Objects);
        Assert.AreEqual(1_048_576L, first.Objects.Concat(second.Objects).Sum(value => value.Length));
    }

    [TestMethod]
    public void StandaloneAssembliesHaveOnlyAspAndFrameworkDependencies()
    {
        foreach (var assembly in new[] { typeof(AspProtocol).Assembly, typeof(AspClient).Assembly, typeof(AspMcpServer).Assembly })
        {
            var references = assembly.GetReferencedAssemblies().Select(value => value.Name!).ToArray();
            Assert.IsFalse(references.Any(name => name.StartsWith("Agentstration.", StringComparison.Ordinal) && !name.StartsWith("Agentstration.Asp.", StringComparison.Ordinal)));
            Assert.IsFalse(references.Any(name => name.Contains("EntityFramework", StringComparison.Ordinal) || name.Contains("OpenDAL", StringComparison.Ordinal) || name.Contains("Agents.AI", StringComparison.Ordinal)));
        }
        Assert.IsFalse(typeof(AspProtocol).Assembly.GetReferencedAssemblies().Any(value => value.Name!.StartsWith("ModelContextProtocol", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CanonicalSchemaSnapshotsMatchThePublishedContract()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Asp.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        foreach (var operation in new[] { "describe", "list", "stat", "read", "write" })
        {
            foreach (var direction in new[] { "input", "output" })
            {
                using var snapshot = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "docs", "schemas", $"{operation}.{direction}.json")));
                Assert.IsTrue(JsonElement.DeepEquals(snapshot.RootElement, direction == "input" ? AspSchemas.Input(operation) : AspSchemas.Output(operation)));
            }
        }
    }
}
