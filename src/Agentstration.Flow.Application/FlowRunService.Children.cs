using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Flow.Storage.Abstractions;

namespace Agentstration.Flow.Application;

public sealed partial class FlowRunService
{
    private static string ChildFlowRunId(FlowRun parent, string stepName, int attempt)
    {
        var identity = $"{parent.WorkspaceId}:{parent.Id}:{stepName}:{attempt}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $"flowrun-child-{hash[..32]}";
    }

    private async Task<StoredFlowRun> SuspendForChildAsync(
        StoredFlowRun stored,
        string stepName,
        string childRunId,
        CancellationToken cancellationToken)
    {
        var steps = stored.Value.Steps.Select(step => step.StepName == stepName
            ? step with { ChildFlowRunId = childRunId }
            : step).ToArray();
        var suspended = await SaveAsync(stored, stored.Value with
        {
            Status = FlowRunStatus.WaitingForChild,
            Steps = steps,
            ExecutionLeaseId = null,
            ExecutionLeaseExpiresAt = null
        }, cancellationToken);
        await EmitAsync(
            suspended.Value.WorkspaceId,
            suspended.Value.Id,
            FlowRunEventType.FlowRunWaitingForChild,
            stepName,
            JsonSerializer.SerializeToElement(new { childFlowRunId = childRunId }),
            cancellationToken);
        return suspended;
    }

    private async Task<StoredFlowRun> EnsureChildFlowRunAsync(
        FlowRun parent,
        FlowCallStepDefinition call,
        JsonElement input,
        string childRunId,
        CancellationToken cancellationToken)
    {
        var existing = await repository.GetRunAsync(parent.WorkspaceId, childRunId, cancellationToken);
        if (existing is not null)
        {
            ValidateChildIdentity(parent, call, existing.Value, childRunId);
            return existing;
        }

        var depth = parent.NestingDepth + 1;
        if (depth > executionOptions.MaximumNestingDepth)
            throw new FlowValidationException("flow_nesting_depth_exceeded", $"Nested Flow depth exceeds the configured limit of {executionOptions.MaximumNestingDepth}.");
        var rootRunId = parent.RootFlowRunId ?? parent.Id;
        if (await CountDescendantsAsync(parent, rootRunId, cancellationToken) >= executionOptions.MaximumDescendantRuns)
            throw new FlowValidationException("flow_descendant_limit_exceeded", $"The root Flow Run already has the configured limit of {executionOptions.MaximumDescendantRuns} descendants.");

        var targetId = call.Flow.Resolve(parent.FlowId.Namespace);
        var requestedVersion = call.Flow.VersionStrategy == FlowCallVersionStrategy.Exact ? call.Flow.Version : null;
        var resolved = await ResolveVersionAsync(parent.WorkspaceId, targetId, requestedVersion, cancellationToken);
        ValidateInput(resolved.Graph?.InputSchema, input);
        var now = timeProvider.GetUtcNow();
        var child = new FlowRun
        {
            WorkspaceId = parent.WorkspaceId,
            Id = childRunId,
            FlowId = targetId,
            FlowVersion = resolved.Version,
            DefinitionState = FlowDefinitionState.Published,
            DefinitionHash = resolved.DefinitionHash,
            DefinitionSnapshotId = $"{targetId.Value}:{resolved.Version}:{resolved.DefinitionHash ?? "legacy"}",
            DefinitionSnapshot = resolved,
            DeploymentResourceId = parent.DeploymentResourceId,
            Trigger = FlowRunTrigger.Flow,
            StartedBy = parent.StartedBy,
            CorrelationId = parent.CorrelationId,
            ParentFlowRunId = parent.Id,
            RootFlowRunId = rootRunId,
            NestingDepth = depth,
            InteractionId = parent.InteractionId,
            WorkTaskId = parent.WorkTaskId,
            TriggerMessageId = parent.TriggerMessageId,
            Scope = parent.Scope,
            Input = input.Clone(),
            CreatedAt = now,
            Steps = CreateSteps(resolved, input)
        };

        StoredFlowRun created;
        try
        {
            created = await repository.CreateRunAsync(child, cancellationToken);
        }
        catch (FlowConcurrencyException)
        {
            var recovered = await repository.GetRunAsync(parent.WorkspaceId, childRunId, cancellationToken);
            if (recovered is null) throw;
            created = recovered;
            ValidateChildIdentity(parent, call, created.Value, childRunId);
            return created;
        }

        RunsCreated.Add(1, new KeyValuePair<string, object?>("flow.definition.state", child.DefinitionState.ToString()));
        try
        {
            await EmitAsync(child.WorkspaceId, child.Id, FlowRunEventType.FlowRunCreated, null,
                JsonSerializer.SerializeToElement(new { child.Status, child.DefinitionState, child.ParentFlowRunId, child.RootFlowRunId, child.NestingDepth }), cancellationToken);
            await EmitAsync(parent.WorkspaceId, parent.Id, FlowRunEventType.ChildFlowRunCreated, call.Name,
                JsonSerializer.SerializeToElement(new { childFlowRunId = child.Id, flowId = child.FlowId.Value, flowNamespace = child.FlowId.Namespace.Value, flowVersion = child.FlowVersion }), cancellationToken);
        }
        finally
        {
            await queue.EnqueueAsync(new(child.Id, child.Scope), cancellationToken);
        }
        return created;
    }

    private async Task<int> CountDescendantsAsync(
        FlowRun parent,
        string rootRunId,
        CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var count = 0;
        for (var skip = 0; ; skip += pageSize)
        {
            var keys = await repository.ListRunKeysAsync(skip, pageSize, cancellationToken);
            foreach (var key in keys.Where(key => key.WorkspaceId == parent.WorkspaceId))
            {
                var run = await repository.GetRunAsync(parent.WorkspaceId, key.RunId, cancellationToken);
                if (run?.Value.RootFlowRunId == rootRunId
                    && run.Value.Scope == parent.Scope
                    && ++count >= executionOptions.MaximumDescendantRuns)
                    return count;
            }
            if (keys.Count < pageSize) return count;
        }
    }

    private static void ValidateChildIdentity(FlowRun parent, FlowCallStepDefinition call, FlowRun child, string expectedChildRunId)
    {
        var expectedFlowId = call.Flow.Resolve(parent.FlowId.Namespace);
        if (child.Id != expectedChildRunId
            || child.ParentFlowRunId != parent.Id
            || child.RootFlowRunId != (parent.RootFlowRunId ?? parent.Id)
            || child.NestingDepth != parent.NestingDepth + 1
            || child.FlowId != expectedFlowId
            || child.Trigger != FlowRunTrigger.Flow
            || child.Scope != parent.Scope
            || child.WorkspaceId != parent.WorkspaceId)
            throw new FlowValidationException("child_flow_identity_mismatch", $"Persisted child Flow Run for step '{call.Name}' does not match the declared target and parent execution scope.");
    }

    private async Task ResumeParentAfterChildAsync(FlowRun child, CancellationToken cancellationToken)
    {
        if (child.ParentFlowRunId is null) return;
        var parent = await repository.GetRunAsync(child.WorkspaceId, child.ParentFlowRunId, cancellationToken);
        if (parent is null
            || parent.Value.Status != FlowRunStatus.WaitingForChild
            || parent.Value.Scope != child.Scope
            || !parent.Value.Steps.Any(step => step.Status == FlowStepRunStatus.Running && step.ChildFlowRunId == child.Id))
            return;

        StoredFlowRun resumed;
        try
        {
            resumed = await repository.UpdateRunAsync(parent.Value with
            {
                Status = FlowRunStatus.Pending,
                ExecutionLeaseId = null,
                ExecutionLeaseExpiresAt = null
            }, parent.ETag, cancellationToken);
        }
        catch (FlowConcurrencyException)
        {
            return;
        }
        try
        {
            await EmitAsync(resumed.Value.WorkspaceId, resumed.Value.Id, FlowRunEventType.FlowRunResumedFromChild,
                resumed.Value.Steps.Single(step => step.ChildFlowRunId == child.Id).StepName,
                JsonSerializer.SerializeToElement(new { childFlowRunId = child.Id, childStatus = child.Status }), cancellationToken);
        }
        finally
        {
            await queue.EnqueueAsync(new(resumed.Value.Id, resumed.Value.Scope), cancellationToken);
        }
    }

    private async Task<IReadOnlyList<StoredFlowRun>> ListActiveDescendantsAsync(
        FlowRun parent,
        CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var rootRunId = parent.RootFlowRunId ?? parent.Id;
        var all = new List<StoredFlowRun>();
        for (var skip = 0; ; skip += pageSize)
        {
            var keys = await repository.ListRunKeysAsync(skip, pageSize, cancellationToken);
            foreach (var key in keys.Where(key => key.WorkspaceId == parent.WorkspaceId))
            {
                var run = await repository.GetRunAsync(parent.WorkspaceId, key.RunId, cancellationToken);
                if (run is not null && run.Value.Scope == parent.Scope && run.Value.RootFlowRunId == rootRunId) all.Add(run);
            }
            if (keys.Count < pageSize) break;
        }

        var result = new List<StoredFlowRun>();
        var parents = new Queue<string>();
        parents.Enqueue(parent.Id);
        while (parents.TryDequeue(out var parentId))
        {
            foreach (var child in all.Where(run => run.Value.ParentFlowRunId == parentId))
            {
                parents.Enqueue(child.Value.Id);
                if (!child.Value.Status.IsTerminal()) result.Add(child);
            }
        }
        return result.OrderBy(run => run.Value.NestingDepth).ToArray();
    }
}
