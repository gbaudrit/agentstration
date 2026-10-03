using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentstration.Runtime.Worker.MicrosoftAgentFramework;

internal sealed class AwpRuntimeWorkerService(
    IOptions<RuntimeWorkerOptions> options,
    AwpWorkerCredentialProvider credentials,
    AwpAssignmentExecutor executor,
    RuntimeWorkerReadiness readiness,
    TimeProvider timeProvider,
    ILogger<AwpRuntimeWorkerService> logger) : BackgroundService
{
    public static readonly ActivitySource ActivitySource = new("Agentstration.Runtime.Worker.MicrosoftAgentFramework");

    private readonly ConcurrentDictionary<AwpAssignmentId, Task> active = new();
    private int activeCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configured = options.Value;
        using var credential = await credentials.LoadAsync(stoppingToken);
        var sessionId = new AwpWorkerSessionId(Guid.NewGuid());
        using var client = new AwpClient(new Uri(configured.AuthorityUrl, UriKind.Absolute), credential, sessionId,
            timeProvider, configured.TransientRetryCount);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await client.ActivateSessionAsync(stoppingToken);
                await client.RegisterAsync(Registration(client, configured), stoppingToken);
                readiness.MarkReady();
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("AWP Worker {WorkerId} session {SessionId} registered with {Authority}",
                        client.WorkerId.Value, client.SessionId.Value, configured.AuthorityUrl);
                await ClaimLoopAsync(client, configured, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (AwpClientException exception) when (IsAuthenticationFailure(exception))
            {
                readiness.MarkNotReady();
                if (logger.IsEnabled(LogLevel.Critical))
                    logger.LogCritical(exception, "AWP Worker authentication failed with {Code}; the process cannot claim work", exception.Code);
                throw;
            }
            catch (Exception exception)
            {
                readiness.MarkNotReady();
                logger.LogWarning(exception, "AWP authority is temporarily unavailable; registration will be retried");
                await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, stoppingToken);
            }
        }

        readiness.MarkNotReady();
        await Task.WhenAll(active.Values);
    }

    private async Task ClaimLoopAsync(AwpClient client, RuntimeWorkerOptions configured,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var available = configured.MaximumConcurrentAssignments - Volatile.Read(ref activeCount);
            if (available <= 0)
            {
                await Task.WhenAny(active.Values);
                continue;
            }

            var response = await client.ClaimAsync(new(client.WorkerId, client.SessionId, available,
                configured.ClaimWaitSeconds), stoppingToken);
            if (response.Assignment is null) continue;
            Interlocked.Increment(ref activeCount);
            var assignment = response.Assignment;
            var task = RunAssignmentAsync(client, assignment, response.ServerTime, configured, stoppingToken);
            if (!active.TryAdd(assignment.AssignmentId, task))
                throw new InvalidOperationException($"Assignment '{assignment.AssignmentId.Value:D}' is already active in this Worker.");
            _ = ObserveAsync(assignment.AssignmentId, task);
        }
    }

    private async Task ObserveAsync(AwpAssignmentId assignmentId, Task task)
    {
        try { await task; }
        catch (Exception exception) { logger.LogError(exception, "Unhandled AWP assignment task failure for {AssignmentId}", assignmentId.Value); }
        finally
        {
            _ = active.TryRemove(assignmentId, out _);
            Interlocked.Decrement(ref activeCount);
        }
    }

    private async Task RunAssignmentAsync(AwpClient client, AwpRunAssignment assignment, DateTimeOffset serverTime,
        RuntimeWorkerOptions configured, CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("runtime.worker.assignment.execute", ActivityKind.Consumer);
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
        var cancellationRequested = false;
        var heartbeat = HeartbeatAsync(session, execution, () => cancellationRequested = true, stoppingToken);
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
            var output = await executor.ExecuteAsync(session, material, execution.Token);
            session.EnsureCanStartMutation();
            await client.CompleteAsync(new(session.Context, new(Guid.NewGuid()), output), execution.Token);
            activity?.SetStatus(ActivityStatusCode.Ok);
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
        catch (Exception exception) when (session.CanStartMutation())
        {
            var code = exception is AwpExecutionNotSupportedException unsupported
                ? unsupported.Code : "worker_execution_failed";
            activity?.SetStatus(ActivityStatusCode.Error, code);
            activity?.SetTag("error.type", exception.GetType().FullName);
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

    private static AwpWorkerRegistrationRequest Registration(AwpClient client, RuntimeWorkerOptions configured)
    {
        var softwareVersion = typeof(AwpRuntimeWorkerService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AwpRuntimeWorkerService).Assembly.GetName().Version?.ToString() ?? "unknown";
        var mafVersion = typeof(Agentstration.Runtime.MicrosoftAgentFramework.AgentFrameworkRuntimeFactory)
            .Assembly.GetName().Version?.ToString();
        return new([AwpProtocol.Version], new(client.WorkerId, client.SessionId, softwareVersion,
            configured.MaximumConcurrentAssignments,
            [new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", ["1.0"], mafVersion)]));
    }

    private static void ValidateMaterialReference(AwpExecutionMaterialReference reference, AwpExecutionMaterial material)
    {
        if (!string.Equals(reference.MaterialId, material.MaterialId, StringComparison.Ordinal)
            || !string.Equals(reference.SchemaVersion, material.SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(reference.Digest, material.Digest, StringComparison.Ordinal))
            throw new AwpExecutionNotSupportedException("execution_material_mismatch",
                "The execution material does not match the assignment reference.");
    }

    private static bool IsAuthenticationFailure(AwpClientException exception) =>
        exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}
