using System.Runtime.CompilerServices;
using Agentstration.SystemGraph;
using Agentstration.SystemOperations.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Agentstration.SystemOperations.Api;

public sealed class SystemOperationsHub(ISystemOperationsService operations, ISystemOperationsAccess access,
    SystemOperationsSubscriptions subscriptions, TimeProvider clock, ILogger<SystemOperationsHub> logger) : Hub
{
    public async IAsyncEnumerable<SystemOperationsFrame> WatchAsync(SystemOperationsQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var context = await access.GetAsync(cancellationToken);
        using var lease = subscriptions.TryAcquire(context.ActorId) ?? throw new HubException("system_operations_subscription_limit");
        lock (Context.Items)
        {
            if (Context.Items.ContainsKey("system-operations-stream")) throw new HubException("system_operations_subscription_limit");
            Context.Items["system-operations-stream"] = true;
        }
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Context.ConnectionAborted);
        var state = new SystemOperationsStreamState();
        try
        {
            while (!cancelled.IsCancellationRequested)
            {
                SystemOperationsFrame frame;
                var stopped = false;
                try
                {
                    // The service rechecks current RBAC, exact root visibility and binding rights
                    // after collecting evidence, immediately before this direct client delivery.
                    frame = state.Next(await operations.SnapshotAsync(query, cancelled.Token));
                    stopped = frame.Clear;
                }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { yield break; }
                catch (SystemOperationsException error) { if (logger.IsEnabled(LogLevel.Information)) logger.LogInformation("Operational stream stopped: {Code}", error.Code); frame = state.Stop(clock.GetUtcNow(), "resync"); stopped = true; }
                catch (SystemGraphException error) { if (logger.IsEnabled(LogLevel.Information)) logger.LogInformation("Operational stream stopped: {Code}", error.Code); frame = state.Stop(clock.GetUtcNow(), "revoked"); stopped = true; }
                catch (Exception error) { logger.LogWarning(error, "Operational snapshot is unavailable."); frame = state.Stop(clock.GetUtcNow(), "unavailable"); stopped = true; }
                yield return frame;
                if (stopped) yield break;
                // Enumeration/write backpressure permits one frame at a time; ticks never queue.
                await Task.Delay(TimeSpan.FromSeconds(5), clock, cancelled.Token);
            }
        }
        finally { lock (Context.Items) Context.Items.Remove("system-operations-stream"); }
    }
}
