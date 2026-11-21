using Agentstration.Infrastructure;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class BootstrapStartupLifecycleTests
{
    [TestMethod]
    public async Task DeferredWorkersStartOnceAfterAnExplicitRecoveryFromFailedStartup()
    {
        var state = new BootstrapStartupState(); state.Begin(); state.EnableAdministration();
        var worker = new ProbeWorker();
        using var deferred = new BootstrapDeferredHostedServices(state, [worker]);
        await deferred.StartAsync(default);
        var failedAttempt = state.Read().AttemptId;
        state.Fail("model_dependency_timeout");
        Assert.AreEqual(0, worker.Starts);
        state.BeginRetry();
        await state.WaitForAttemptChangeAsync(failedAttempt, default);
        state.Complete();
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, worker.Starts);
        await deferred.StopAsync(default);
        Assert.AreEqual(1, worker.Stops);
    }

    private sealed class ProbeWorker : IHostedService
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken) { Starts++; Started.TrySetResult(); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { Stops++; return Task.CompletedTask; }
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplicationStartedWaitsForBootstrapOrRetainedAdministration(bool failed)
    {
        var startup = new BootstrapStartupState();
        startup.Begin();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(startup);
            services.AddSingleton<BootstrapStartupLifecycle>();
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BootstrapStartupLifecycle>());
        }).Build();
        var starting = host.StartAsync();
        await host.Services.GetRequiredService<BootstrapStartupLifecycle>().ListenerStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.IsFalse(starting.IsCompleted);
        Assert.IsFalse(lifetime.ApplicationStarted.IsCancellationRequested);
        if (failed)
        {
            startup.EnableAdministration();
            startup.Fail("required_contribution_unavailable");
        }
        else startup.Complete();
        await starting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.AreEqual(!failed, startup.IsReady);
        await host.StopAsync();
    }

    [TestMethod]
    public async Task CancellationDuringBootstrapDoesNotPublishApplicationStarted()
    {
        var startup = new BootstrapStartupState();
        startup.Begin();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(startup);
            services.AddSingleton<BootstrapStartupLifecycle>();
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BootstrapStartupLifecycle>());
        }).Build();
        using var cancellation = new CancellationTokenSource();
        var starting = host.StartAsync(cancellation.Token);
        await host.Services.GetRequiredService<BootstrapStartupLifecycle>().ListenerStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => starting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        Assert.IsFalse(startup.IsReady);
    }
}
