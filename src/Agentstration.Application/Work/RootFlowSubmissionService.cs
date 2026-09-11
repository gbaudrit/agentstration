using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed record ResolvedRootFlowTarget(FlowReference Reference);

public interface IRootFlowTargetResolver
{
    Task<ResolvedRootFlowTarget> ResolveAsync(
        FlowRunScope scope,
        FlowReference target,
        JsonElement input,
        CancellationToken cancellationToken);
}

public sealed record RootFlowRunRequest(
    string RunId,
    FlowReference Target,
    FlowRunTrigger Trigger,
    FlowInvocationOrigin Origin,
    string CallerId,
    string? CausationId,
    string? IdempotencyKey,
    string CorrelationId,
    JsonElement Input,
    WorkItemId WorkItemId,
    bool ResolvedFromActiveReference,
    string? ParentFlowRunId,
    string? InteractionId,
    string? WorkTaskId,
    string? TriggerMessageId,
    FlowRunScope Scope);

public sealed record RootFlowRunResult(FlowRun Run, string ETag);

public interface IRootFlowRunGateway
{
    Task<RootFlowRunResult> EnsureAsync(RootFlowRunRequest request, CancellationToken cancellationToken);
}

public interface IRootFlowSubmissionAuthorizer
{
    Task AuthorizeAsync(FlowRunScope scope, CancellationToken cancellationToken);
}

public sealed record SubmitRootFlowCommand(
    WorkspaceId WorkspaceId,
    FlowReference Target,
    JsonElement Input,
    FlowInvocationOrigin Origin,
    string CallerId,
    FlowRunTrigger Trigger,
    string? IdempotencyKey = null,
    string? CausationId = null,
    string? CorrelationId = null,
    string? Type = null,
    string? Instruction = null,
    string? Title = null,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<WorkInput>? WorkInputs = null,
    IReadOnlyList<WorkAttachment>? Attachments = null,
    string? InteractionId = null,
    string? WorkTaskId = null,
    string? TriggerMessageId = null,
    WorkItemId? WorkItemId = null);

public sealed record RootFlowSubmission(StoredWorkItem WorkItem, RootFlowRunResult FlowRun, bool Recovered);

public sealed class RootFlowSubmissionService(
    WorkItemService workItems,
    IWorkItemRepository repository,
    IRootFlowTargetResolver targets,
    IRootFlowRunGateway runs,
    IRootFlowSubmissionAuthorizer authorizer,
    IEnumerable<IWorkExecutionScopeAccessor> executionScopeAccessors)
{
    public const string RootRunIdMetadata = "flowInvocation.rootRunId";
    public const string OriginMetadata = "flowInvocation.origin";
    public const string TriggerMetadata = "flowInvocation.trigger";
    public const string CallerMetadata = "flowInvocation.caller";
    public const string CausationMetadata = "flowInvocation.causationId";
    public const string IdempotencyMetadata = "flowInvocation.idempotencyKey";
    public const string InputHashMetadata = "flowInvocation.inputHash";
    public const string ActiveReferenceMetadata = "flowInvocation.resolvedFromActiveReference";

    public Task<RootFlowSubmission> SubmitAsync(SubmitRootFlowCommand command, CancellationToken cancellationToken) =>
        SubmitAsync(command, null, cancellationToken);

    internal async Task<RootFlowSubmission> SubmitAsync(
        SubmitRootFlowCommand command,
        Func<StoredWorkItem, CancellationToken, Task>? beforeExecutionConfirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.CallerId))
            throw new WorkValidationException("flow_invocation_caller_required", "A root Flow invocation requires a caller identity.");
        var scope = executionScopeAccessors.Select(accessor => accessor.Current).FirstOrDefault(value => value is not null)
            ?? throw new WorkValidationException("work_execution_scope_required", "Root Flow submission requires an authenticated Workspace scope.");
        if (scope.WorkspaceId != command.WorkspaceId)
            throw new WorkValidationException("flow_invocation_scope_mismatch", "The root Flow invocation and execution scope must belong to the same Workspace.");
        await authorizer.AuthorizeAsync(scope, cancellationToken);

        var workItemId = command.WorkItemId ?? (string.IsNullOrWhiteSpace(command.IdempotencyKey)
            ? Agentstration.Work.WorkItemId.New()
            : DeterministicWorkItemId(scope, command.Origin, command.IdempotencyKey));
        var runId = $"flowrun-root-{workItemId.Value:N}";
        var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.Input.GetRawText()))).ToLowerInvariant();
        var existing = await repository.GetAsync(command.WorkspaceId, workItemId, cancellationToken);
        if (existing is not null)
        {
            ValidateExisting(existing.Value, command, runId, inputHash);
            var recoveredRun = await EnsureRunAsync(existing.Value, command, scope, runId, command.Target.UseActiveVersion, cancellationToken);
            return new(existing, recoveredRun, true);
        }

        var resolved = await targets.ResolveAsync(scope, command.Target, command.Input, cancellationToken);
        var correlation = string.IsNullOrWhiteSpace(command.CorrelationId)
            ? $"flow:{workItemId.Value:N}"
            : command.CorrelationId;
        var metadata = command.Metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(command.Metadata, StringComparer.Ordinal);
        metadata[RootRunIdMetadata] = runId;
        metadata[OriginMetadata] = command.Origin.ToString();
        metadata[TriggerMetadata] = command.Trigger.ToString();
        metadata[CallerMetadata] = command.CallerId;
        metadata[InputHashMetadata] = inputHash;
        metadata[ActiveReferenceMetadata] = command.Target.UseActiveVersion.ToString();
        if (!string.IsNullOrWhiteSpace(command.CausationId)) metadata[CausationMetadata] = command.CausationId;
        if (!string.IsNullOrWhiteSpace(command.IdempotencyKey)) metadata[IdempotencyMetadata] = command.IdempotencyKey;

        var submit = new SubmitWorkItemCommand(
            command.WorkspaceId,
            command.Type ?? "flow",
            command.Instruction ?? $"Execute Flow '{resolved.Reference.FlowId}'.",
            command.Title,
            command.Description,
            command.CallerId,
            new WorkCorrelationId(correlation),
            Metadata: metadata,
            Inputs: command.WorkInputs is null
                ? [new WorkInput(Structured: command.Input.Clone())]
                : [new WorkInput(Structured: command.Input.Clone()), .. command.WorkInputs],
            Attachments: command.Attachments,
            Flow: resolved.Reference,
            Id: workItemId);

        StoredWorkItem stored;
        try
        {
            stored = await workItems.SubmitAsync(submit, beforeExecutionConfirmed, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            var recovered = await repository.GetAsync(command.WorkspaceId, workItemId, cancellationToken);
            if (recovered is null) throw;
            stored = recovered;
            ValidateExisting(stored.Value, command, runId, inputHash);
        }

        var rootRun = await EnsureRunAsync(stored.Value, command with { Target = resolved.Reference, CorrelationId = correlation }, scope, runId, command.Target.UseActiveVersion, cancellationToken);
        return new(stored, rootRun, false);
    }

    private async Task<RootFlowRunResult> EnsureRunAsync(
        WorkItem item,
        SubmitRootFlowCommand command,
        FlowRunScope scope,
        string runId,
        bool resolvedFromActiveReference,
        CancellationToken cancellationToken)
    {
        var target = item.Flow ?? throw new WorkValidationException("flow_invocation_target_missing", "The WorkItem has no root Flow target.");
        return await runs.EnsureAsync(new RootFlowRunRequest(
            runId,
            target,
            command.Trigger,
            command.Origin,
            command.CallerId,
            command.CausationId,
            command.IdempotencyKey,
            command.CorrelationId ?? item.CorrelationId.Value,
            command.Input,
            item.Id,
            resolvedFromActiveReference,
            item.Metadata.GetValueOrDefault("workplace.parentFlowRunId"),
            command.InteractionId ?? item.Metadata.GetValueOrDefault("workplace.interactionId"),
            command.WorkTaskId ?? item.Metadata.GetValueOrDefault("workplace.taskId") ?? item.Id.Value.ToString("D"),
            command.TriggerMessageId ?? item.Metadata.GetValueOrDefault("workplace.triggerMessageId"),
            scope), cancellationToken);
    }

    private static void ValidateExisting(WorkItem item, SubmitRootFlowCommand command, string runId, string inputHash)
    {
        if (item.Flow is null
            || item.Flow.FlowId != command.Target.Resolve(command.Target.FlowId.Namespace)
            || !VersionMatches(item.Flow, command.Target)
            || !item.Metadata.TryGetValue(RootRunIdMetadata, out var storedRunId)
            || !string.Equals(storedRunId, runId, StringComparison.Ordinal)
            || !item.Metadata.TryGetValue(OriginMetadata, out var origin)
            || !string.Equals(origin, command.Origin.ToString(), StringComparison.Ordinal)
            || !item.Metadata.TryGetValue(TriggerMetadata, out var trigger)
            || !string.Equals(trigger, command.Trigger.ToString(), StringComparison.Ordinal)
            || !item.Metadata.TryGetValue(CallerMetadata, out var caller)
            || !string.Equals(caller, command.CallerId, StringComparison.Ordinal)
            || !item.Metadata.TryGetValue(InputHashMetadata, out var storedInputHash)
            || !string.Equals(storedInputHash, inputHash, StringComparison.Ordinal)
            || !Matches(item.Metadata, CausationMetadata, command.CausationId)
            || !Matches(item.Metadata, IdempotencyMetadata, command.IdempotencyKey))
            throw new WorkValidationException("flow_invocation_idempotency_conflict", "The idempotency key is already bound to another root Flow invocation.");
    }

    private static bool Matches(IReadOnlyDictionary<string, string> metadata, string key, string? value) =>
        metadata.TryGetValue(key, out var stored) ? string.Equals(stored, value, StringComparison.Ordinal) : string.IsNullOrWhiteSpace(value);

    private static bool VersionMatches(FlowReference stored, FlowReference requested) =>
        requested.UseActiveVersion || string.Equals(stored.Version, requested.Version, StringComparison.Ordinal);

    private static WorkItemId DeterministicWorkItemId(FlowRunScope scope, FlowInvocationOrigin origin, string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{scope.TenantId:D}\n{scope.WorkspaceId.Value:D}\n{origin}\n{key.Trim()}"));
        return new WorkItemId(new Guid(bytes.AsSpan(0, 16)));
    }
}
