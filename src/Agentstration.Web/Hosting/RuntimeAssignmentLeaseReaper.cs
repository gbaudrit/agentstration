using Agentstration.Runtime.Core;

namespace Agentstration.Web.Hosting;

public sealed class RuntimeAssignmentLeaseReaper(
    RuntimeWorkerAssignmentService assignments,
    RuntimeWorkerLeaseOptions options,
    TimeProvider timeProvider,
    ILogger<RuntimeAssignmentLeaseReaper> logger) : BackgroundService
{
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await assignments.InitializeAsync(stoppingToken);
        using var timer = new PeriodicTimer(options.HeartbeatInterval, timeProvider);
        do
        {
            try
            {
                IReadOnlyList<Agentstration.Runtime.Abstractions.RuntimeAssignmentTerminalResult> expired;
                do
                {
                    expired = await assignments.ExpireLeasesAsync(BatchSize, stoppingToken);
                    foreach (var result in expired)
                        logger.LogWarning(
                            "Runtime Worker assignment {AssignmentId} expired for {WorkspaceId}/{RunId} with outcome {RunState}",
                            result.Assignment.Id.Value,
                            result.Assignment.WorkspaceId,
                            result.Assignment.TargetRunId,
                            result.RunState);
                }
                while (expired.Count == BatchSize);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Runtime Worker lease reconciliation failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
