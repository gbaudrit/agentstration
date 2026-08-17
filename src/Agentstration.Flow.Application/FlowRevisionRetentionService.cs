using System.Text.Json;
using Agentstration.Flow.Storage.Abstractions;

namespace Agentstration.Flow.Application;

public sealed class FlowRevisionRetentionService(
    IFlowRepository repository,
    IFlowRunCancellationRegistry cancellations,
    IFlowRunEventSink eventSink,
    TimeProvider timeProvider)
{
    public async Task<FlowRevisionUsage> GetUsageAsync(string revisionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        var active = new List<string>();
        var waiting = 0;
        var historical = 0;
        var skip = 0;
        while (true)
        {
            var page = await repository.ListRunsAsync(null, null, skip, 200, cancellationToken);
            foreach (var run in page.Items.Where(value => value.Value.RuntimeBindings.Any(binding =>
                         string.Equals(binding.RevisionId, revisionId, StringComparison.Ordinal))))
            {
                if (run.Value.Status.IsTerminal()) historical++;
                else
                {
                    active.Add(run.Value.Id);
                    if (run.Value.Status == FlowRunStatus.WaitingForInput) waiting++;
                }
            }
            skip += page.Items.Count;
            if (!page.HasMore || page.Items.Count == 0) break;
        }
        return new(revisionId, active.Count, waiting, historical, active);
    }

    public async Task<FlowRevisionUsage> ForceTerminateAsync(string revisionId, CancellationToken cancellationToken)
    {
        var usage = await GetUsageAsync(revisionId, cancellationToken);
        foreach (var runId in usage.ActiveRunIds)
        {
            cancellations.Cancel(runId);
            try
            {
                var stored = await repository.GetRunAsync(runId, cancellationToken);
                if (stored is null || stored.Value.Status.IsTerminal()) continue;
                var now = timeProvider.GetUtcNow();
                var error = new FlowRunError(
                    "agent_revision_force_purged",
                    "The agent revision required to resume this Flow Run was force-purged.",
                    revisionId);
                var steps = stored.Value.Steps.Select(step => step.Status == FlowStepRunStatus.Running
                    ? step with { Status = FlowStepRunStatus.Failed, CompletedAt = now, Error = error }
                    : step).ToArray();
                await repository.UpdateRunAsync(stored.Value with
                {
                    Status = FlowRunStatus.Failed,
                    CompletedAt = now,
                    Error = error,
                    Steps = steps,
                    ExecutionLeaseId = null,
                    ExecutionLeaseExpiresAt = null
                }, stored.ETag, cancellationToken);
                var runEvent = await repository.AppendRunEventAsync(new FlowRunEvent(
                    runId,
                    0,
                    FlowRunEventType.FlowRunFailed,
                    steps.FirstOrDefault(step => step.Status == FlowStepRunStatus.Failed)?.StepName,
                    JsonSerializer.SerializeToElement(error),
                    now), cancellationToken);
                await eventSink.PublishAsync(runEvent, cancellationToken);
            }
            catch (FlowConcurrencyException)
            {
                // A concurrent worker completed or transitioned the run; the caller recalculates impact.
            }
        }
        return usage;
    }
}
