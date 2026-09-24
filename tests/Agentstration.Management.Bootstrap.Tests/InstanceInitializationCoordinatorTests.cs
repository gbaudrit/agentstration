using Agentstration.Bootstrap.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Storage.Sqlite;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class InstanceInitializationCoordinatorTests
{
    [TestMethod]
    public async Task ConcurrentCoordinatorsExecuteInitializationOnce()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"agentstration-initialization-{Guid.NewGuid():N}"));
        try
        {
            await using var firstProvider = Provider(directory.FullName);
            await using var secondProvider = Provider(directory.FullName);
            var firstStore = firstProvider.GetRequiredService<IResourceStore>();
            var secondStore = secondProvider.GetRequiredService<IResourceStore>();
            await firstStore.InitializeAsync(default);
            await secondStore.InitializeAsync(default);
            var first = Coordinator(firstStore);
            var second = Coordinator(secondStore);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var executions = 0;

            var firstRun = first.RunAsync(async token =>
            {
                Interlocked.Increment(ref executions);
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }, default);
            await entered.Task;
            var secondRun = second.RunAsync(_ =>
            {
                Interlocked.Increment(ref executions);
                return Task.CompletedTask;
            }, default);

            await Task.Delay(100);
            Assert.AreEqual(1, Volatile.Read(ref executions));
            Assert.IsFalse(secondRun.IsCompleted);
            release.SetResult();
            await Task.WhenAll(firstRun, secondRun);

            Assert.AreEqual(1, executions);
            Assert.IsTrue(first.IsReady);
            Assert.IsTrue(second.IsReady);
            var state = await StateAsync(firstStore);
            Assert.AreEqual(InstanceInitializationStatus.Ready, state.Value.Definition.Status);
            Assert.AreEqual(1L, state.Value.Definition.FencingToken);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [TestMethod]
    public async Task ExpiredLeaseIsTakenOverWithANewerFencingToken()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"agentstration-initialization-{Guid.NewGuid():N}"));
        try
        {
            await using var provider = Provider(directory.FullName);
            var store = provider.GetRequiredService<IResourceStore>();
            await store.InitializeAsync(default);
            var now = DateTimeOffset.UtcNow;
            _ = await store.PutExactAsync(ResourceScopeRef.Instance, Resource(
                InstanceInitializationStatus.Initializing,
                targetVersion: 1,
                fencingToken: 7,
                owner: "stale-owner",
                leaseExpiresAt: now.AddMinutes(-1),
                now), null, true, default);
            var coordinator = Coordinator(store);
            var executed = false;

            await coordinator.RunAsync(_ => { executed = true; return Task.CompletedTask; }, default);

            Assert.IsTrue(executed);
            var state = await StateAsync(store);
            Assert.AreEqual(InstanceInitializationStatus.Ready, state.Value.Definition.Status);
            Assert.AreEqual(8L, state.Value.Definition.FencingToken);
            Assert.IsNull(state.Value.Definition.OwnerInstanceId);
            Assert.IsNull(state.Value.Definition.LeaseExpiresAt);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [TestMethod]
    public async Task FailedInitializationIsRecordedAndCanBeRetried()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"agentstration-initialization-{Guid.NewGuid():N}"));
        try
        {
            await using var provider = Provider(directory.FullName);
            var store = provider.GetRequiredService<IResourceStore>();
            await store.InitializeAsync(default);
            var first = Coordinator(store);

            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                first.RunAsync(_ => throw new InvalidOperationException("initialization failed"), default));
            var failed = await StateAsync(store);
            Assert.AreEqual(InstanceInitializationStatus.Failed, failed.Value.Definition.Status);
            Assert.AreEqual("initialization failed", failed.Value.Definition.LastError);

            var second = Coordinator(store);
            var retried = false;
            await second.RunAsync(_ => { retried = true; return Task.CompletedTask; }, default);

            Assert.IsTrue(retried);
            var ready = await StateAsync(store);
            Assert.AreEqual(InstanceInitializationStatus.Ready, ready.Value.Definition.Status);
            Assert.AreEqual(2L, ready.Value.Definition.FencingToken);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [TestMethod]
    public async Task NewTargetVersionRunsAfterEarlierVersionIsReady()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"agentstration-initialization-{Guid.NewGuid():N}"));
        try
        {
            await using var provider = Provider(directory.FullName);
            var store = provider.GetRequiredService<IResourceStore>();
            await store.InitializeAsync(default);
            await Coordinator(store).RunAsync(_ => Task.CompletedTask, default);
            var executions = 0;

            await Coordinator(store, targetVersion: 2).RunAsync(_ =>
            {
                executions++;
                return Task.CompletedTask;
            }, default);

            Assert.AreEqual(1, executions);
            var state = await StateAsync(store);
            Assert.AreEqual(2, state.Value.Definition.TargetVersion);
            Assert.AreEqual(2L, state.Value.Definition.FencingToken);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    private static ServiceProvider Provider(string directory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICurrentRequestContext>(new SystemOperationRequestContext());
        services.AddSqliteResourceManagement($"Data Source={Path.Combine(directory, "management.db")};Pooling=False");
        return services.BuildServiceProvider();
    }

    private static InstanceInitializationCoordinator Coordinator(IResourceStore store, int targetVersion = 1) => new(
        store,
        new InstanceInitializationOptions
        {
            TargetVersion = targetVersion,
            LeaseDuration = TimeSpan.FromSeconds(3),
            PollInterval = TimeSpan.FromMilliseconds(10)
        },
        TimeProvider.System,
        NullLogger<InstanceInitializationCoordinator>.Instance);

    private static async Task<StoredResource<InstanceInitializationResource>> StateAsync(IResourceStore store) =>
        await store.GetExactAsync<InstanceInitializationResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Instance,
            ResourceNamespace.Default,
            BootstrapKinds.InstanceInitialization,
            InstanceInitializationCoordinator.ResourceName), default)
        ?? throw new AssertFailedException("The instance initialization resource was not stored.");

    private static InstanceInitializationResource Resource(
        InstanceInitializationStatus status,
        int targetVersion,
        long fencingToken,
        string? owner,
        DateTimeOffset? leaseExpiresAt,
        DateTimeOffset now) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = BootstrapKinds.InstanceInitialization,
        Metadata = new ResourceMetadata { Name = InstanceInitializationCoordinator.ResourceName },
        ScopeRef = ResourceScopeRef.Instance,
        Generation = 1,
        Definition = new InstanceInitializationProperties
        {
            Status = status,
            TargetVersion = targetVersion,
            FencingToken = fencingToken,
            OwnerInstanceId = owner,
            LeaseExpiresAt = leaseExpiresAt,
            UpdatedAt = now
        }
    };
}
