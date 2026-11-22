using System.Collections.Concurrent;
using Agentstration.Infrastructure;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class BootstrapWorkerReadinessTests
{
    [TestMethod]
    public async Task DeferredWorkersWaitForReadinessAndRetainStartupAndShutdownOrder()
    {
        var state = new BootstrapStartupState();
        state.Begin();
        var events = new ConcurrentQueue<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var group = new BootstrapDeferredHostedServices(state,
            [new Probe("schema", events), new Probe("scheduler", events, started)]);
        await group.StartAsync(default);
        Assert.IsEmpty(events);
        state.Complete();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await group.StopAsync(default);
        CollectionAssert.AreEqual(new[] { "start-schema", "start-scheduler", "stop-scheduler", "stop-schema" }, events.ToArray());
    }

    private sealed class Probe(string name, ConcurrentQueue<string> events, TaskCompletionSource? started = null) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            events.Enqueue($"start-{name}");
            started?.TrySetResult();
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            events.Enqueue($"stop-{name}");
            return Task.CompletedTask;
        }
    }
}
