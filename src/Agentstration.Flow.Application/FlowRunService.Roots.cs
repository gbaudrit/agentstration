using System.Text.Json;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flow.Application;

public sealed record EnsureRootFlowRunCommand(
    string RunId,
    FlowId FlowId,
    string? Version,
    string? DeploymentResourceId,
    FlowRunTrigger Trigger,
    FlowInvocationOrigin Origin,
    string CallerId,
    string? CausationId,
    string? IdempotencyKey,
    string CorrelationId,
    JsonElement Input,
    string WorkItemResourceId,
    bool ResolvedFromActiveReference,
    string? ParentFlowRunId,
    string? InteractionId,
    string? WorkTaskId,
    string? TriggerMessageId,
    FlowRunScope Scope);

public sealed partial class FlowRunService
{
    public async Task<StoredFlowRun> EnsureRootAsync(
        EnsureRootFlowRunCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var existing = await repository.GetRunAsync(command.Scope.WorkspaceId, command.RunId, cancellationToken);
        if (existing is not null)
        {
            ValidateRootIdentity(existing.Value, command);
            return existing;
        }

        var resolved = await ResolveVersionAsync(command.Scope.WorkspaceId, command.FlowId, command.Version, cancellationToken);
        ValidateInput(resolved.Graph?.InputSchema, command.Input);
        var now = timeProvider.GetUtcNow();
        var run = new FlowRun
        {
            WorkspaceId = command.Scope.WorkspaceId,
            Id = command.RunId,
            FlowId = command.FlowId,
            FlowVersion = resolved.Version,
            DeploymentResourceId = string.IsNullOrWhiteSpace(command.DeploymentResourceId) ? "local" : command.DeploymentResourceId,
            Trigger = command.Trigger,
            StartedBy = command.CallerId,
            InvocationOrigin = command.Origin,
            CallerId = command.CallerId,
            CausationId = command.CausationId,
            IdempotencyKey = command.IdempotencyKey,
            CorrelationId = command.CorrelationId,
            WorkItemResourceId = command.WorkItemResourceId,
            ResolvedFromActiveReference = command.ResolvedFromActiveReference,
            ParentFlowRunId = command.ParentFlowRunId,
            InteractionId = command.InteractionId,
            WorkTaskId = command.WorkTaskId,
            TriggerMessageId = command.TriggerMessageId,
            Scope = command.Scope,
            Input = command.Input.Clone(),
            CreatedAt = now,
            DefinitionSnapshot = resolved,
            DefinitionHash = resolved.DefinitionHash,
            DefinitionState = FlowDefinitionState.Published,
            DefinitionSnapshotId = $"{command.FlowId.Value}:{resolved.Version}:{resolved.DefinitionHash ?? "legacy"}",
            Steps = CreateSteps(resolved, command.Input)
        };

        StoredFlowRun stored;
        try
        {
            stored = await repository.CreateRunAsync(run, cancellationToken);
        }
        catch (FlowConcurrencyException)
        {
            var recovered = await repository.GetRunAsync(command.Scope.WorkspaceId, command.RunId, cancellationToken);
            if (recovered is null) throw;
            stored = recovered;
            ValidateRootIdentity(stored.Value, command);
            return stored;
        }

        RunsCreated.Add(1, new KeyValuePair<string, object?>("flow.definition.state", run.DefinitionState.ToString()));
        await EmitAsync(run.WorkspaceId, run.Id, FlowRunEventType.FlowRunCreated, null,
            JsonSerializer.SerializeToElement(new { run.Status, run.DefinitionState, run.InvocationOrigin, run.CausationId }), cancellationToken);
        await queue.EnqueueAsync(new(run.Id, run.Scope), cancellationToken);
        return stored;
    }

    private static void ValidateRootIdentity(FlowRun run, EnsureRootFlowRunCommand command)
    {
        if (run.Scope != command.Scope
            || run.FlowId != command.FlowId
            || run.RootFlowRunId is not null
            || run.NestingDepth != 0
            || run.Trigger != command.Trigger
            || !string.Equals(run.WorkItemResourceId, command.WorkItemResourceId, StringComparison.Ordinal)
            || run.ResolvedFromActiveReference != command.ResolvedFromActiveReference
            || !string.Equals(run.ParentFlowRunId, command.ParentFlowRunId, StringComparison.Ordinal)
            || run.InvocationOrigin != command.Origin
            || !string.Equals(run.CallerId, command.CallerId, StringComparison.Ordinal)
            || !string.Equals(run.CausationId, command.CausationId, StringComparison.Ordinal)
            || !string.Equals(run.IdempotencyKey, command.IdempotencyKey, StringComparison.Ordinal)
            || !string.Equals(run.CorrelationId, command.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(run.InteractionId, command.InteractionId, StringComparison.Ordinal)
            || !string.Equals(run.WorkTaskId, command.WorkTaskId, StringComparison.Ordinal)
            || !string.Equals(run.TriggerMessageId, command.TriggerMessageId, StringComparison.Ordinal)
            || !JsonElement.DeepEquals(run.Input, command.Input))
            throw new FlowValidationException("flow_root_identity_conflict", "The root Flow Run identity is already bound to another invocation.");

        if (!string.IsNullOrWhiteSpace(command.Version)
            && !string.Equals(run.FlowVersion, command.Version, StringComparison.Ordinal))
            throw new FlowValidationException("flow_root_version_conflict", "The root Flow Run is already bound to another Flow version.");
    }
}
