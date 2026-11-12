using System.Text.Json;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Flows.Application;

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
    FlowRunScope Scope)
{
    public bool TransientPayloads { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<FlowCompositionCall>> Compositions { get; init; } = new Dictionary<string, IReadOnlyList<FlowCompositionCall>>();
    public ResourceScopeRef? DefinitionScopeRef { get; init; }
    public IReadOnlyList<SecretInputReference> SecretInputs { get; init; } = [];
}

public sealed partial class FlowRunService
{
    public async Task<StoredFlowRun> EnsureRootAsync(
        EnsureRootFlowRunCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var existing = await repository.GetRunExactAsync(command.Scope.ScopeRef, command.RunId, cancellationToken);
        if (existing is not null)
        {
            ValidateRootIdentity(existing.Value, command);
            return existing;
        }

        var resolved = await ResolveVersionAsync(command.Scope.AccessScopeRef, command.FlowId, command.Version, command.DefinitionScopeRef, cancellationToken);
        var composed = flowDefinitions is null ? new CompiledFlowComposition(resolved, null)
            : await new FlowCompositionService(flowDefinitions).ResolveAsync(resolved, command.Compositions, command.Scope.AccessScopeRef, cancellationToken);
        resolved = composed.Definition;
        ValidateInput(resolved.Graph?.ResolveInputSchema(), command.Input, command.SecretInputs);
        var now = timeProvider.GetUtcNow();
        var run = new FlowRun
        {
            Composition = composed.Receipt,
            TransientPayloads = command.TransientPayloads,
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
            SecretInputs = command.SecretInputs.ToArray(),
            CreatedAt = now,
            DefinitionSnapshot = resolved,
            DefinitionHash = resolved.DefinitionHash,
            DefinitionScopeRef = resolved.ScopeRef,
            DefinitionState = FlowDefinitionState.Published,
            DefinitionSnapshotId = DefinitionSnapshotIdentity(resolved),
            Steps = CreateSteps(resolved, command.Input)
        };

        StoredFlowRun stored;
        try
        {
            stored = await repository.CreateRunAsync(run, cancellationToken);
        }
        catch (FlowConcurrencyException)
        {
            var recovered = await repository.GetRunExactAsync(command.Scope.ScopeRef, command.RunId, cancellationToken);
            if (recovered is null) throw;
            stored = recovered;
            ValidateRootIdentity(stored.Value, command);
            return stored;
        }

        RunsCreated.Add(1, new KeyValuePair<string, object?>("flow.definition.state", run.DefinitionState.ToString()));
        await EmitAsync(run.Scope.ScopeRef, run.Id, FlowRunEventType.FlowRunCreated, null,
            JsonSerializer.SerializeToElement(new { run.Status, run.DefinitionState, run.InvocationOrigin, run.CausationId }), cancellationToken);
        await queue.EnqueueAsync(new(run.Id, run.Scope), cancellationToken);
        return stored;
    }

    private static void ValidateRootIdentity(FlowRun run, EnsureRootFlowRunCommand command)
    {
        if (run.TransientPayloads && !run.TransientPayloadsAvailable)
            throw new FlowValidationException("direct_content_unavailable", "The ephemeral execution payload is no longer available.");
        if (run.TransientPayloads != command.TransientPayloads || run.Scope != command.Scope
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

        var accepted = run.Composition?.Bindings ?? new Dictionary<string, IReadOnlyList<FlowCompositionCall>>();
        foreach (var point in command.Compositions)
        {
            if (!accepted.TryGetValue(point.Key, out var calls) || calls.Count != point.Value.Count)
                throw new FlowValidationException("flow_root_composition_conflict", "The root Flow Run already has another composition.");
            for (var index = 0; index < calls.Count; index++)
            {
                var requested = point.Value[index]; var pinned = calls[index];
                if (requested.Flow.ResourceId != pinned.Flow.ResourceId
                    || requested.Flow.Namespace is { } ns && ns != pinned.Flow.Namespace
                    || requested.Flow.ScopeRef is { } scope && scope != pinned.Flow.ScopeRef
                    || requested.Flow.VersionStrategy == FlowCallVersionStrategy.Exact && requested.Flow.Version != pinned.Flow.Version
                    || requested.Flow.ResourceUid is { } uid && uid != pinned.Flow.ResourceUid
                    || requested.Flow.DefinitionHash is { } hash && hash != pinned.Flow.DefinitionHash
                    || requested.PassInputDirectly != pinned.PassInputDirectly || !JsonElement.DeepEquals(requested.Parameters, pinned.Parameters))
                    throw new FlowValidationException("flow_root_composition_conflict", "The root Flow Run already has another composition.");
            }
        }
        if (accepted.Any(point => point.Value.Count > 0 && !command.Compositions.ContainsKey(point.Key)))
            throw new FlowValidationException("flow_root_composition_conflict", "The root Flow Run already has another composition.");

        if (!string.IsNullOrWhiteSpace(command.Version)
            && !string.Equals(run.FlowVersion, command.Version, StringComparison.Ordinal))
            throw new FlowValidationException("flow_root_version_conflict", "The root Flow Run is already bound to another Flow version.");
    }
}
