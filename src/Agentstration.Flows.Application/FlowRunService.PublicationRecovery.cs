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
            || !run.Steps.Any(step => step.StepName.StartsWith("publication-", StringComparison.Ordinal) && step.Status == FlowStepRunStatus.Failed))
            throw new FlowValidationException("flow_publication_resume_refused", "Only a failed publication can resume its accepted graph.");
        var resumed = await SaveAsync(stored, run with
        {
            Status = FlowRunStatus.Pending,
            CompletedAt = null,
            Error = null,
            Output = null,
            OutputName = null,
            OutputOutcome = null,
            Steps = run.Steps.Select(step => step.Status == FlowStepRunStatus.Failed || step.StepType == "output"
                ? step with { Status = FlowStepRunStatus.NotStarted, CompletedAt = null, Error = null, Output = null, SelectedTransition = null } : step).ToArray()
        }, token);
        await EmitAsync(scope.ScopeRef, runId, FlowRunEventType.FlowRunResumed, null,
            JsonSerializer.SerializeToElement(new { reason = "publicationRetry" }), token);
        await queue.EnqueueAsync(new(runId, run.Scope), token);
        return resumed;
    }
}
