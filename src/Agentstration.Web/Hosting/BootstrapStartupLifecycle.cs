using Agentstration.Infrastructure;

namespace Agentstration.Web.Hosting;

// The listener is usable during Bootstrap, while ApplicationStarted is published
// only after initialization finishes (or administration is retained on failure).
internal sealed class BootstrapStartupLifecycle(BootstrapStartupState startup) : IHostedLifecycleService
{
    private readonly TaskCompletionSource listenerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task ListenerStarted => listenerStarted.Task;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        listenerStarted.TrySetResult();
        try { await startup.WaitUntilReadyAsync(cancellationToken); }
        catch (OperationCanceledException) when (startup.Failure is not null
            && startup.AdministrationReady && !cancellationToken.IsCancellationRequested)
        { }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
