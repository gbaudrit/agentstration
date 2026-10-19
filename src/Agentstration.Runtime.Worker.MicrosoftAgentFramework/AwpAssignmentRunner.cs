using System.Diagnostics;
using System.Text.Json;
using Agentstration.Awp.Abstractions;

namespace Agentstration.Runtime.Worker.MicrosoftAgentFramework;

internal sealed class AwpAssignmentRunner(
    AwpAssignmentExecutor executor,
    TimeProvider timeProvider,
    ILogger<AwpRuntimeWorkerService> logger)
{
    public async Task RunAsync(AwpClient client, AwpRunAssignment assignment, DateTimeOffset serverTime,
        RuntimeWorkerOptions configured, CancellationToken stoppingToken)
    {
        using var activity = AwpRuntimeWorkerService.ActivitySource.StartActivity(
            "runtime.worker.assignment.execute", ActivityKind.Consumer);
        activity?.SetTag("agentstration.worker.id", client.WorkerId.Value);
        activity?.SetTag("agentstration.worker.session.id", client.SessionId.Value);
        activity?.SetTag("agentstration.assignment.id", assignment.AssignmentId.Value);
        activity?.SetTag("agentstration.assignment.attempt.id", assignment.AttemptId.Value);
        activity?.SetTag("agentstration.assignment.fencing_generation", assignment.Ownership.FencingGeneration);
        activity?.SetTag("agentstration.run.id", assignment.Target switch
        {
            AwpDirectAgentRunTarget direct => direct.RuntimeRunId,
            AwpRootFlowRunTarget flow => flow.FlowRunId,
            _ => null
        });
        activity?.SetTag("agentstration.run.target_kind", assignment.Target.GetType().Name);
        var session = new AwpAssignmentSession(client, assignment, serverTime, timeProvider,
            TimeSpan.FromSeconds(configured.LeaseSafetyMarginSeconds));
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var cancellationRequested = false;
        var heartbeat = HeartbeatAsync(session, execution, () => cancellationRequested = true,
            heartbeatCancellation.Token);
        try
        {
            var runId = assignment.Target switch
            {
                AwpDirectAgentRunTarget direct => direct.RuntimeRunId,
                AwpRootFlowRunTarget flow => flow.FlowRunId,
                _ => throw new AwpExecutionNotSupportedException("target_kind_unsupported", "The assignment target kind is unsupported.")
            };
            var location = new AwpExecutionLocation(runId);
            await session.AppendEventAsync(AwpExecutionEventKind.AssignmentStarted, location, null, null, execution.Token);
            var material = (await client.GetMaterialAsync(session.Context, execution.Token)).Material;
            ValidateMaterialReference(assignment.ExecutionMaterial, material);
            await session.AppendEventAsync(AwpExecutionEventKind.RunStarted, location, null, null, execution.Token);
            var result = await executor.ExecuteAsync(session, material, execution.Token);
            await session.AppendEventAsync(AwpExecutionEventKind.RunCompleted, location,
                JsonSerializer.SerializeToElement(new
                {
                    status = result.Outcome == "error" ? "failed" : "succeeded",
                    output = result.Output,
                    outputName = result.OutputName,
                    outcome = result.Outcome,
                    errorCode = result.ErrorCode,
                    error = result.ErrorMessage,
                    errorDetails = result.ErrorDetails
                }), null, execution.Token);
            await session.EnsureCanStartMutationAsync(execution.Token);
            await client.CompleteAsync(new(session.Context, new(Guid.NewGuid()), result.Output), execution.Token);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (AwpWaitingForInputException) when (session.CanStartMutation())
        {
            await client.CompleteAsync(new(session.Context, new(Guid.NewGuid()), null), stoppingToken);
            activity?.SetStatus(ActivityStatusCode.Ok, "waiting_for_input");
        }
        catch (OperationCanceledException) when (cancellationRequested && session.CanStartMutation())
        {
            activity?.SetStatus(ActivityStatusCode.Error, "assignment_cancelled");
            await TryFailAsync(session, AwpExecutionFailureKind.Cancelled, "assignment_cancelled",
                "The authoritative server requested cancellation.", stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested && session.CanStartMutation())
        {
            activity?.SetStatus(ActivityStatusCode.Error, "worker_shutdown");
            await TryFailAsync(session, AwpExecutionFailureKind.Cancelled, "worker_shutdown",
                "The Runtime Worker is shutting down.", CancellationToken.None);
        }
        catch (OperationCanceledException) when (!session.CanStartMutation())
        {
            activity?.SetStatus(ActivityStatusCode.Error, "lease_unsafe");
            logger.LogWarning("Stopped assignment {AssignmentId} because its lease can no longer safely authorize mutations",
                assignment.AssignmentId.Value);
        }
        catch (AwpLeaseUnsafeException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "lease_unsafe");
            logger.LogWarning("Stopped assignment {AssignmentId} because its lease can no longer safely authorize mutations",
                assignment.AssignmentId.Value);
        }
        catch (AwpExecutionTimedOutException exception) when (session.CanStartMutation())
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Code);
            await TryFailAsync(session, AwpExecutionFailureKind.TimedOut, exception.Code,
                exception.Message, stoppingToken);
        }
        catch (Exception exception) when (session.CanStartMutation())
        {
            var code = exception is AwpExecutionNotSupportedException unsupported
                ? unsupported.Code : "worker_execution_failed";
            activity?.SetStatus(ActivityStatusCode.Error, code);
            activity?.SetTag("error.type", exception.GetType().FullName);
            logger.LogError(exception, "Assignment {AssignmentId} execution failed with code {ErrorCode}",
                assignment.AssignmentId.Value, code);
            await TryFailAsync(session, AwpExecutionFailureKind.Execution, code, exception.Message, stoppingToken);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "lease_unsafe");
            activity?.SetTag("error.type", exception.GetType().FullName);
            logger.LogWarning(exception,
                "Stopped assignment {AssignmentId} without another mutation because its lease is no longer safe",
                assignment.AssignmentId.Value);
        }
        finally
        {
            await heartbeatCancellation.CancelAsync();
            await execution.CancelAsync();
            try { await heartbeat; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task HeartbeatAsync(AwpAssignmentSession session, CancellationTokenSource execution,
        Action markCancellation, CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(session.Assignment.Ownership.HeartbeatIntervalSeconds);
        using var timer = new PeriodicTimer(interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (execution.IsCancellationRequested) return;
            try
            {
                var response = await session.Client.HeartbeatAsync(new(session.Context, session.LastEventSequence), stoppingToken);
                session.ApplyHeartbeat(response);
                if (response.Cancellation.Requested)
                {
                    markCancellation();
                    await execution.CancelAsync();
                    return;
                }
            }
            catch (Exception exception) when (exception is AwpClientException or HttpRequestException)
            {
                logger.LogWarning(exception, "Heartbeat failed for assignment {AssignmentId}",
                    session.Assignment.AssignmentId.Value);
                if (!session.CanStartMutation())
                {
                    await execution.CancelAsync();
                    return;
                }
            }
        }
    }

    private static async Task TryFailAsync(AwpAssignmentSession session, AwpExecutionFailureKind kind,
        string code, string message, CancellationToken cancellationToken)
    {
        try
        {
            session.EnsureCanStartMutation();
            await session.Client.FailAsync(new(session.Context, new(Guid.NewGuid()), new(kind, code, message)), cancellationToken);
        }
        catch (Exception exception) when (exception is AwpClientException or AwpLeaseUnsafeException or OperationCanceledException) { }
    }

    private static void ValidateMaterialReference(AwpExecutionMaterialReference reference, AwpExecutionMaterial material)
    {
        if (!string.Equals(reference.MaterialId, material.MaterialId, StringComparison.Ordinal)
            || !string.Equals(reference.SchemaVersion, material.SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(reference.Digest, material.Digest, StringComparison.Ordinal))
            throw new AwpExecutionNotSupportedException("execution_material_mismatch",
                "The execution material does not match the assignment reference.");
    }
}
