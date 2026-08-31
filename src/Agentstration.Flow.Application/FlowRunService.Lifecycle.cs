using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flow.Application;

public sealed partial class FlowRunService
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var now = timeProvider.GetUtcNow();
        for (var skip = 0; ; skip += pageSize)
        {
            var keys = await repository.ListRecoverableRunsAsync(skip, pageSize, cancellationToken);
            foreach (var key in keys)
            {
                var run = await repository.GetRunAsync(key.WorkspaceId, key.RunId, cancellationToken)
                    ?? throw new FlowRunNotFoundException(key.RunId);
                if (run.Value.Status == FlowRunStatus.WaitingForInput)
                {
                    var pending = await repository.ListInputRequestsAsync(key.WorkspaceId, key.RunId, InputRequestStatus.Pending, cancellationToken);
                    var expired = pending.FirstOrDefault(value => value.Value.ExpiresAt <= now);
                    if (expired is not null)
                    {
                        await ExpireInputAsync(run, expired, now, cancellationToken);
                        continue;
                    }
                    if (inputRequestSink is not null)
                        foreach (var request in pending)
                            await inputRequestSink.PublishRequestedAsync(run.Value, request.Value, cancellationToken);
                    if ((await repository.ListInputRequestsAsync(key.WorkspaceId, key.RunId, InputRequestStatus.Answered, cancellationToken)).Count > 0)
                        await queue.EnqueueAsync(new(run.Value.Id, run.Value.Scope), cancellationToken);
                    continue;
                }
                if (run.Value.Status == FlowRunStatus.Pending
                    || run.Value.Status == FlowRunStatus.Running && run.Value.ExecutionLeaseExpiresAt <= now)
                    await queue.EnqueueAsync(new(run.Value.Id, run.Value.Scope), cancellationToken);
            }
            if (keys.Count < pageSize) break;
        }
    }

    public Task<StoredFlowRun> CreateAsync(
        FlowId flowId,
        string? version,
        string? deploymentResourceId,
        FlowRunTrigger trigger,
        string? startedBy,
        string? correlationId,
        JsonElement input,
        FlowRunScope scope,
        CancellationToken cancellationToken) =>
        CreateAsync(flowId, version, deploymentResourceId, trigger, startedBy, correlationId, input, null, null, null, null, scope, cancellationToken);

    public async Task<StoredFlowRun> CreateAsync(
        FlowId flowId,
        string? version,
        string? deploymentResourceId,
        FlowRunTrigger trigger,
        string? startedBy,
        string? correlationId,
        JsonElement input,
        string? parentFlowRunId,
        string? interactionId,
        string? workTaskId,
        string? triggerMessageId,
        FlowRunScope scope,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveVersionAsync(scope.WorkspaceId, flowId, version, cancellationToken);
        ValidateInput(resolved.Graph?.InputSchema, input);
        var now = timeProvider.GetUtcNow();
        var run = new FlowRun
        {
            WorkspaceId = scope.WorkspaceId,
            Id = $"flowrun-{Guid.NewGuid():N}",
            FlowId = flowId,
            FlowVersion = resolved.Version,
            DeploymentResourceId = string.IsNullOrWhiteSpace(deploymentResourceId) ? "local" : deploymentResourceId,
            Trigger = trigger,
            StartedBy = string.IsNullOrWhiteSpace(startedBy) ? "local-user" : startedBy,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
            ParentFlowRunId = parentFlowRunId,
            InteractionId = interactionId,
            WorkTaskId = workTaskId,
            TriggerMessageId = triggerMessageId,
            Scope = scope,
            Input = input.Clone(),
            CreatedAt = now,
            DefinitionSnapshot = resolved,
            DefinitionHash = resolved.DefinitionHash,
            DefinitionState = FlowDefinitionState.Published,
            DefinitionSnapshotId = $"{flowId.Value}:{resolved.Version}:{resolved.DefinitionHash ?? "legacy"}",
            Steps = CreateSteps(resolved, input)
        };
        var stored = await repository.CreateRunAsync(run, cancellationToken);
        RunsCreated.Add(1, new KeyValuePair<string, object?>("flow.definition.state", run.DefinitionState.ToString()));
        await EmitAsync(run.WorkspaceId, run.Id, FlowRunEventType.FlowRunCreated, null, JsonSerializer.SerializeToElement(new { run.Status, run.DefinitionState }), cancellationToken);
        await queue.EnqueueAsync(new(run.Id, run.Scope), cancellationToken);
        return stored;
    }

    public async Task<StoredFlowRun> CreateDraftAsync(FlowDraft draft, FlowRunTrigger trigger, string? startedBy, string? correlationId, JsonElement input, FlowRunScope scope, CancellationToken cancellationToken)
    {
        if (draft.WorkspaceId != scope.WorkspaceId)
            throw new FlowValidationException("flow_run_scope_mismatch", "The Flow Draft and execution scope must belong to the same Workspace.");
        ValidateInput(draft.Definition.InputSchema, input);
        var validationVersion = $"0.0.0-draft.{draft.Revision}";
        var snapshot = new FlowVersion(draft.WorkspaceId, draft.FlowId, validationVersion, draft.Description, FlowDraftSnapshotAdapter.ToRoutingDefinition(draft.Definition), draft.Tags,
            timeProvider.GetUtcNow(), draft.Definition, draft.DefinitionHash);
        var now = timeProvider.GetUtcNow();
        var run = new FlowRun
        {
            WorkspaceId = scope.WorkspaceId,
            Id = $"flowrun-{Guid.NewGuid():N}",
            FlowId = draft.FlowId,
            FlowVersion = validationVersion,
            DefinitionState = FlowDefinitionState.Draft,
            DraftRevision = draft.Revision,
            DefinitionHash = draft.DefinitionHash,
            DefinitionSnapshotId = $"snapshot-{Guid.NewGuid():N}",
            DeploymentResourceId = "designer",
            Trigger = trigger,
            StartedBy = string.IsNullOrWhiteSpace(startedBy) ? "local-user" : startedBy,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
            Scope = scope,
            Input = input.Clone(),
            CreatedAt = now,
            DefinitionSnapshot = snapshot,
            Steps = CreateSteps(snapshot, input)
        };
        var stored = await repository.CreateRunAsync(run, cancellationToken);
        RunsCreated.Add(1, new KeyValuePair<string, object?>("flow.definition.state", run.DefinitionState.ToString()));
        await EmitAsync(run.WorkspaceId, run.Id, FlowRunEventType.FlowRunCreated, null, JsonSerializer.SerializeToElement(new { run.Status, run.DefinitionState, run.DraftRevision }), cancellationToken);
        await queue.EnqueueAsync(new(run.Id, run.Scope), cancellationToken);
        return stored;
    }

    public async Task<StoredInputRequest> RespondAsync(
        string runId,
        string requestId,
        JsonElement value,
        string principalId,
        FlowRunScope scope,
        CancellationToken cancellationToken)
    {
        var run = await RequiredAsync(runId, scope, cancellationToken);
        return await RespondAsync(run, requestId, value, principalId, cancellationToken);
    }

    public async Task<StoredInputRequest> RespondAsync(
        string runId,
        string requestId,
        JsonElement value,
        string principalId,
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        var run = await RequiredAsync(workspaceId, runId, cancellationToken);
        return await RespondAsync(run, requestId, value, principalId, cancellationToken);
    }

    private async Task<StoredInputRequest> RespondAsync(
        StoredFlowRun run,
        string requestId,
        JsonElement value,
        string principalId,
        CancellationToken cancellationToken)
    {
        var runId = run.Value.Id;
        var workspaceId = run.Value.WorkspaceId;
        if (run.Value.Status != FlowRunStatus.WaitingForInput)
            throw new FlowValidationException("input_request_run_not_waiting", "The Flow Run is not waiting for external input.");
        var stored = await repository.GetInputRequestAsync(workspaceId, runId, requestId, cancellationToken)
            ?? throw new FlowValidationException("input_request_not_found", $"Input Request '{requestId}' was not found.");
        if (stored.Value.Status != InputRequestStatus.Pending)
            throw new InputRequestAlreadyResolvedException(requestId);
        var now = timeProvider.GetUtcNow();
        if (stored.Value.ExpiresAt <= now)
        {
            await ExpireInputAsync(run, stored, now, cancellationToken);
            throw new FlowValidationException("input_request_expired", "The Input Request has expired.");
        }
        ValidateInputResponse(stored.Value, value);
        StoredInputRequest answered;
        try
        {
            answered = await repository.UpdateInputRequestAsync(stored.Value with
            {
                Status = InputRequestStatus.Answered,
                Response = new InputResponse(now, value.Clone(), string.IsNullOrWhiteSpace(principalId) ? "local-user" : principalId)
            }, stored.ETag, cancellationToken);
        }
        catch (FlowConcurrencyException)
        {
            throw new InputRequestAlreadyResolvedException(requestId);
        }
        await EmitAsync(workspaceId, runId, FlowRunEventType.InputReceived, stored.Value.Source,
            JsonSerializer.SerializeToElement(new { requestId, principalId = answered.Value.Response!.PrincipalId }), cancellationToken);
        await queue.EnqueueAsync(new(runId, run.Value.Scope), cancellationToken);
        return answered;
    }

    public async Task ExpireDueInputsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        const int pageSize = 200;
        for (var skip = 0; ; skip += pageSize)
        {
            var keys = await repository.ListRecoverableRunsAsync(skip, pageSize, cancellationToken);
            foreach (var key in keys)
            {
                var run = await repository.GetRunAsync(key.WorkspaceId, key.RunId, cancellationToken);
                if (run?.Value.Status != FlowRunStatus.WaitingForInput) continue;
                var pending = await repository.ListInputRequestsAsync(key.WorkspaceId, key.RunId, InputRequestStatus.Pending, cancellationToken);
                var expired = pending.FirstOrDefault(value => value.Value.ExpiresAt <= now);
                if (expired is not null) await ExpireInputAsync(run, expired, now, cancellationToken);
            }
            if (keys.Count < pageSize) break;
        }
    }

    public async Task<FlowRevisionUsage> GetRevisionUsageAsync(string revisionId, CancellationToken cancellationToken)
        => await RevisionRetention().GetUsageAsync(revisionId, cancellationToken);

    public async Task<FlowRevisionUsage> ForceTerminateRevisionRunsAsync(string revisionId, CancellationToken cancellationToken)
        => await RevisionRetention().ForceTerminateAsync(revisionId, cancellationToken);

    private FlowRevisionRetentionService RevisionRetention() => new(repository, cancellations, eventSink, timeProvider);

    public async Task<StoredFlowRun> CancelAsync(string runId, FlowRunScope scope, CancellationToken cancellationToken)
    {
        var stored = await RequiredAsync(scope.WorkspaceId, runId, cancellationToken);
        if (stored.Value.Status.IsTerminal()) return stored;
        cancellations.Cancel(new FlowRunKey(scope.WorkspaceId, runId));
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.Status is FlowStepRunStatus.NotStarted or FlowStepRunStatus.Running
            ? step with { Status = FlowStepRunStatus.Cancelled, CompletedAt = now }
            : step).ToArray();
        var cancelled = await repository.UpdateRunAsync(stored.Value with
        {
            Status = FlowRunStatus.Cancelled,
            CompletedAt = now,
            Error = new FlowRunError("flow_run_cancelled", "The Flow Run was cancelled."),
            Steps = steps,
            ExecutionLeaseId = null,
            ExecutionLeaseExpiresAt = null
        }, stored.ETag, cancellationToken);
        foreach (var input in await repository.ListInputRequestsAsync(scope.WorkspaceId, runId, InputRequestStatus.Pending, cancellationToken))
            await repository.UpdateInputRequestAsync(input.Value with { Status = InputRequestStatus.Cancelled }, input.ETag, cancellationToken);
        await EmitAsync(scope.WorkspaceId, runId, FlowRunEventType.FlowRunCancelled, null, null, cancellationToken);
        return cancelled;
    }

    private async Task<FlowVersion> ResolveVersionAsync(WorkspaceId workspaceId, FlowId flowId, string? version, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(version))
            return (await repository.GetVersionAsync(workspaceId, flowId, version, cancellationToken))?.Value
                ?? throw new FlowValidationException("flow_version_not_found", $"Published Flow version '{version}' was not found.");
        var definition = await repository.GetAsync(workspaceId, flowId, cancellationToken) ?? throw new FlowNotFoundException(flowId);
        if (definition.Value.ActiveVersion is null)
            throw new FlowValidationException("flow_active_version_required", "A Flow Run requires a published active version.");
        return (await repository.GetVersionAsync(workspaceId, flowId, definition.Value.ActiveVersion, cancellationToken))?.Value
            ?? throw new FlowValidationException("flow_version_not_found", $"Published Flow version '{definition.Value.ActiveVersion}' was not found.");
    }

    private async Task ExpireInputAsync(StoredFlowRun run, StoredInputRequest input, DateTimeOffset now, CancellationToken token)
    {
        await repository.UpdateInputRequestAsync(input.Value with { Status = InputRequestStatus.Expired }, input.ETag, token);
        await EmitAsync(run.Value.WorkspaceId, run.Value.Id, FlowRunEventType.InputExpired, input.Value.Source,
            JsonSerializer.SerializeToElement(new { input.Value.Id, input.Value.ExpiresAt }), token);
        await FailAsync(run.Value.WorkspaceId, run.Value.Id, FlowRunStatus.TimedOut, "input_request_timed_out", "The Flow Run timed out while waiting for external input.", token);
    }
}

