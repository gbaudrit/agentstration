using System.Text.Json;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flows.Application;

public sealed partial class FlowRunService
{
    /// <summary>Explicitly resume a failed durable graph using its successful step checkpoints and immutable definition.</summary>
    public async Task<StoredFlowRun> ResumePublicationAsync(string runId, FlowRunScope scope, CancellationToken token)
    {
        var stored = await RequiredAsync(runId, scope, token);
        var run = stored.Value;
        if (run.Status != FlowRunStatus.Failed || run.DefinitionSnapshot.Metadata.GetValueOrDefault("flow.durableGraph") != "true"
            || run.Steps.SingleOrDefault(step => step.StepName == "transformations")?.Status != FlowStepRunStatus.Succeeded
                && run.Error?.Code is not ("worker_lost" or "worker_execution_failed"))
            throw new FlowValidationException("flow_publication_resume_refused", "Only a failed publication can resume its accepted graph.");
        var pending = new Queue<string>(Children(run));
        var visited = new HashSet<string>(StringComparer.Ordinal) { runId };
        while (pending.TryDequeue(out var childId))
        {
            if (!visited.Add(childId)) continue;
            var child = await repository.GetRunExactAsync(run.Scope.ScopeRef, childId, token);
            if (child is null) continue;
            if (child.Value.RootFlowRunId != runId)
                throw new FlowValidationException("flow_root_composition_conflict", "Recovery requires children of the accepted root.");
            foreach (var nested in Children(child.Value)) pending.Enqueue(nested);
            if (child.Value.Status == FlowRunStatus.Failed)
                await repository.UpdateRunAsync(ResumeCheckpoint(child.Value), child.ETag, token);
        }
        var resumed = await SaveAsync(stored, ResumeCheckpoint(run), token);
        await EmitAsync(scope.ScopeRef, runId, FlowRunEventType.FlowRunResumed, null,
            JsonSerializer.SerializeToElement(new { reason = "publicationRetry" }), token);
        await queue.EnqueueAsync(new(runId, run.Scope), token);
        return resumed;
    }

    private static IEnumerable<string> Children(FlowRun run) => (run.ReservedDescendantRunIds ?? [])
        .Concat(run.Steps.SelectMany(step => step.ChildFlowRunIds))
        .Concat(run.Steps.Where(step => step.ChildFlowRunId is not null).Select(step => step.ChildFlowRunId!));

    private static FlowRun ResumeCheckpoint(FlowRun run) => run with
    {
        Status = FlowRunStatus.Pending,
        CompletedAt = null,
        Error = null,
        Output = null,
        OutputName = null,
        OutputOutcome = null,
        ExecutionLeaseId = null,
        ExecutionLeaseExpiresAt = null,
        Steps = run.Steps.Select(step => step.Status is FlowStepRunStatus.Failed or FlowStepRunStatus.Skipped || step.StepType == "output"
            ? step with { Status = FlowStepRunStatus.NotStarted, CompletedAt = null, Error = null, Output = null, SelectedTransition = null } : step).ToArray()
    };
}
