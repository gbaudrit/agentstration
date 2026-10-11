using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using Agentstration.Awp.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentstration.Runtime.Worker.MicrosoftAgentFramework;

internal sealed class AwpRuntimeWorkerService(
    IOptions<RuntimeWorkerOptions> options,
    AwpWorkerCredentialProvider credentials,
    AwpAssignmentRunner assignmentRunner,
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
            var task = assignmentRunner.RunAsync(client, assignment, response.ServerTime, configured, stoppingToken);
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

    private static bool IsAuthenticationFailure(AwpClientException exception) =>
        exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}
