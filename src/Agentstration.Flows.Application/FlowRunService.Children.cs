using Agentstration.Schemas;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Flows.Application;

public sealed partial class FlowRunService
{
    public async Task<StoredFlowRun> CreateOrGetAssignedChildAsync(
        WorkspaceId workspaceId,
        string parentRunId,
        string stepDefinitionId,
        JsonElement input,
        string purpose,
        int? iteration,
        CancellationToken cancellationToken) => await CreateOrGetAssignedChildExactAsync(
            ResourceScopeRef.Workspace(workspaceId.Value), parentRunId, stepDefinitionId, input, purpose, iteration,
            cancellationToken);

    public async Task<StoredFlowRun> CreateOrGetAssignedChildExactAsync(
        ResourceScopeRef executionScopeRef,
        string parentRunId,
        string stepDefinitionId,
        JsonElement input,
        string purpose,
        int? iteration,
        CancellationToken cancellationToken,
        InvocationDataContracts? contractContext = null,
        FlowProcessUnit? processUnit = null, Guid? ownerStepExecutionId = null)
    {
        var parent = await repository.GetRunExactAsync(executionScopeRef, parentRunId, cancellationToken)
            ?? throw new FlowRunNotFoundException(parentRunId);
        var step = parent.Value.DefinitionSnapshot.Graph?.Steps.SingleOrDefault(value =>
            string.Equals(value.Name, stepDefinitionId, StringComparison.Ordinal))
            ?? throw new FlowValidationException("child_flow_step_invalid", "The assigned StepDefinition was not found.");
        if (purpose is "artifactProcessing" or "process" && step.Region() is { } region
            && (purpose != "artifactProcessing" || step is ForeachArtifactsFlowStepDefinition)
            && iteration is >= 0 && iteration < region.MaximumUnits)
        {
            var unit = processUnit ?? new FlowProcessUnit($"unit-{iteration.Value}", iteration.Value);
            if (unit.Ordinal != iteration.Value || string.IsNullOrWhiteSpace(unit.Key) || unit.Key.Length > 128
                || unit.Key.Any(char.IsControl) || unit.Descriptor is { Length: > 256 } || unit.Descriptor?.Any(char.IsControl) == true
                || unit.TotalUnits is { } total && (total > region.MaximumUnits || total <= unit.Ordinal))
                throw new FlowValidationException("process_unit_invalid", "A process unit requires a bounded key, ordinal and descriptor.");
            var body = FlowProcessGraph.Extract(parent.Value.DefinitionSnapshot.Graph!, step);
            var version = parent.Value.DefinitionSnapshot with { Graph = body, DefinitionHash = FlowDefinitionHash.Compute(body) };
            var inlineCall = new FlowCallStepDefinition { Name = step.Name,
                Flow = new(parent.Value.FlowId.Value, FlowCallVersionStrategy.Exact, parent.Value.FlowVersion,
                    parent.Value.FlowId.Namespace, version.ScopeRef) };
            var processAttempt = Math.Max(1, parent.Value.Steps.Single(value => value.StepName == stepDefinitionId).Attempt);
            var definition = new FlowProcessDefinitionOrigin(parent.Value.FlowId, parent.Value.DefinitionSnapshot.ScopeRef,
                parent.Value.FlowVersion, parent.Value.DefinitionSnapshotId ?? DefinitionSnapshotIdentity(parent.Value.DefinitionSnapshot),
                parent.Value.DefinitionHash ?? FlowDefinitionHash.Compute(parent.Value.DefinitionSnapshot.Graph!),
                step.Name, step.DisplayName, region.Key, version.DefinitionHash!);
            var origin = new FlowProcessRunOrigin(definition, parent.Value.Id, step.Name, processAttempt,
                ownerStepExecutionId, unit.Key, unit.Ordinal, unit.Descriptor);
            // Preserve existing Artifact child URLs and scoped capability identity.
            var childId = ChildFlowRunId(parent.Value, stepDefinitionId + (step is ForeachArtifactsFlowStepDefinition ? "-artifact" : ":process"), processAttempt, iteration);
            var priorAdmission = parent.Value.Steps.Single(value => value.StepName == stepDefinitionId).Process;
            if (priorAdmission is { Closed: true } && priorAdmission.Attempt == processAttempt
                && !priorAdmission.Units.Any(value => value.Key == unit.Key && value.Ordinal == unit.Ordinal))
                throw new FlowValidationException("process_admission_closed", "Process work-unit admission is closed for this step attempt.");
            await ReserveDescendantAsync(parent.Value, childId, cancellationToken);
            await AdmitProcessUnitAsync(parent.Value, region, origin, childId, unit.TotalUnits,
                purpose == "process" || processUnit is not null, cancellationToken);
            var processingContext = step is ForeachArtifactsFlowStepDefinition artifact
                ? new FlowArtifactProcessingContext(parent.Value.Id, artifact.Name, processAttempt, iteration.Value, artifact.Mode == ArtifactProcessingMode.Access)
                : null;
            return await EnsureChildFlowRunAsync(parent.Value, inlineCall, input, childId, cancellationToken, version,
                processingContext, contractContext ?? (step is ForeachFlowStepDefinition ? new InvocationDataContracts() : null), origin);
        }
        var call = purpose switch
        {
            "flowCall" when step is FlowCallStepDefinition flowCall && iteration is null => flowCall,
            "repeat" when step is RepeatFlowStepDefinition repeat
                && iteration is >= 1 && iteration <= repeat.MaximumIterations => RepeatCall(repeat),
            "artifactStorage" when step.ArtifactOutput?.StorageFlow is not null && iteration is null =>
                ArtifactStorageCall(step),
            _ => throw new FlowValidationException("child_flow_step_invalid",
                "The child Flow purpose does not match the assigned StepDefinition.")
        };
        var attempt = Math.Max(1, parent.Value.Steps.Single(value => value.StepName == stepDefinitionId).Attempt);
        var identity = purpose == "artifactStorage" ? $"{stepDefinitionId}:artifact-storage" : stepDefinitionId;
        var childRunId = ChildFlowRunId(parent.Value, identity, attempt, iteration);
        return await EnsureChildFlowRunAsync(parent.Value, call, input, childRunId, cancellationToken, ownerStepName: stepDefinitionId, artifactStorage: purpose == "artifactStorage");
    }

    private static FlowCallStepDefinition RepeatCall(RepeatFlowStepDefinition repeat) => new()
    {
        Name = repeat.Name,
        DisplayName = repeat.DisplayName,
        Description = repeat.Description,
        Flow = repeat.Flow,
        InputMapping = repeat.InputMapping
    };

    private static FlowCallStepDefinition ArtifactStorageCall(FlowStepDefinition step) => new()
    {
        Name = $"{step.Name}:artifact-storage",
        DisplayName = $"Store Artifact from {step.DisplayName ?? step.Name}",
        Flow = step.ArtifactOutput?.StorageFlow
            ?? throw new FlowValidationException("flow_step_artifact_storage_flow_missing",
                $"Step '{step.Name}' does not declare an Artifact storage Flow.")
    };

    private static string ChildFlowRunId(FlowRun parent, string stepName, int attempt, int? iteration = null)
    {
        var identity = iteration is null
            ? $"{parent.Scope.ScopeRef.Value}:{parent.Id}:{stepName}:{attempt}"
            : $"{parent.Scope.ScopeRef.Value}:{parent.Id}:{stepName}:{attempt}:{iteration.Value}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $"flowrun-child-{hash[..32]}";
    }

    private async Task<StoredFlowRun> SuspendForChildAsync(
        StoredFlowRun stored,
        string stepName,
        string childRunId,
        CancellationToken cancellationToken,
        JsonElement? resolvedInput = null,
        int? repeatIteration = null)
    {
        var steps = stored.Value.Steps.Select(step => step.StepName == stepName
            ? step with
            {
                ChildFlowRunId = childRunId,
                ChildFlowRunIds = step.ChildFlowRunIds.Contains(childRunId, StringComparer.Ordinal)
                    ? step.ChildFlowRunIds
                    : [.. step.ChildFlowRunIds, childRunId],
                ResolvedInput = resolvedInput?.Clone() ?? step.ResolvedInput?.Clone(),
                RepeatIteration = repeatIteration ?? step.RepeatIteration
            }
            : step).ToArray();
        var suspended = await SaveAsync(stored, stored.Value with
        {
            Status = FlowRunStatus.WaitingForChild,
            Steps = steps,
            ExecutionLeaseId = null,
            ExecutionLeaseExpiresAt = null
        }, cancellationToken);
        await EmitAsync(
            suspended.Value.Scope.ScopeRef,
            suspended.Value.Id,
            FlowRunEventType.FlowRunWaitingForChild,
            stepName,
            JsonSerializer.SerializeToElement(new { childFlowRunId = childRunId, repeatIteration }),
            cancellationToken);
        return suspended;
    }

    private async Task<StoredFlowRun> EnsureChildFlowRunAsync(
        FlowRun parent,
        FlowCallStepDefinition call,
        JsonElement input,
        string childRunId,
        CancellationToken cancellationToken,
        FlowVersion? inlineVersion = null,
        FlowArtifactProcessingContext? processingContext = null,
        InvocationDataContracts? contractContext = null,
        FlowProcessRunOrigin? processOrigin = null, string? ownerStepName = null, bool artifactStorage = false)
    {
        var existing = await repository.GetRunExactAsync(parent.Scope.ScopeRef, childRunId, cancellationToken);
        if (existing is not null)
        {
            ValidateChildIdentity(parent, call, existing.Value, childRunId);
            if (inlineVersion is not null && (existing.Value.DefinitionHash != inlineVersion.DefinitionHash
                || existing.Value.ArtifactProcessing != processingContext || !CompatibleProcessOrigin(existing.Value, processOrigin, processingContext)
                || !JsonElement.DeepEquals(existing.Value.Input, input)))
                throw new FlowValidationException(processingContext is null ? "process_child_conflict" : "artifact_processing_child_conflict",
                    "The accepted processing child cannot change its graph, context or input.");
            await AttachChildAsync(parent.Scope.ScopeRef, parent.Id, ownerStepName ?? call.Name, existing.Value.Id, artifactStorage, cancellationToken);
            return existing;
        }

        var depth = parent.NestingDepth + 1;
        if (depth > executionOptions.MaximumNestingDepth)
            throw new FlowValidationException("flow_nesting_depth_exceeded", $"Nested Flow depth exceeds the configured limit of {executionOptions.MaximumNestingDepth}.");
        var rootRunId = parent.RootFlowRunId ?? parent.Id;
        await ReserveDescendantAsync(parent, childRunId, cancellationToken);

        var targetId = call.Flow.Resolve(parent.FlowId.Namespace);
        var requestedVersion = call.Flow.VersionStrategy == FlowCallVersionStrategy.Exact ? call.Flow.Version : null;
        var resolved = inlineVersion ?? await ResolveVersionAsync(parent.Scope.AccessScopeRef, targetId, requestedVersion,
            call.Flow.ScopeRef, cancellationToken);
        if (call.CompositionPoint is not null && flowDefinitions is not null)
            await flowDefinitions.ValidateUseAsync(resolved.ScopeRef, targetId, resolved.Version, parent.Scope.AccessScopeRef, call.Flow.ResourceUid, cancellationToken);
        if (call.Flow.DefinitionHash is { } expected && expected != resolved.DefinitionHash)
            throw new FlowValidationException("flow_composition_child_changed", "The accepted child Flow no longer matches its exact version.");
        var outputExpectation = DataContracts.Declared(resolved.Graph?.ResolveSingleSuccessOutputSchema(out _), $"flow:{resolved.FlowId}/output", resolved.Graph is { } outputGraph ? FlowDataContracts.OutputMediaType(outputGraph) : null)
            ?? contractContext?.ExpectedOutput ?? (inlineVersion is null ? parent.ContractContext?.ExpectedOutput : null);
        input = DataContracts.BindOutputParameter(input, resolved.Graph?.ResolveInputSchema(), outputExpectation, "flow-input", parent.Id, call.Name);
        if (call.CompositionPoint is not null && resolved.Graph?.InputSchema is { } contract)
            FlowCompositionSchema.ValidateValue(contract, input, isProtectedString: value => SecretInputs.IsReference(value, parent.SecretInputs));
        if (call.CompositionParameters is { } parameters && (!input.TryGetProperty("parameters", out var actualParameters)
            || !JsonElement.DeepEquals(parameters, actualParameters)))
            throw new FlowValidationException("flow_composition_parameters_changed", "Accepted composition parameters cannot be replaced.");
        var composed = flowDefinitions is null ? new CompiledFlowComposition(resolved, null)
            : await new FlowCompositionService(flowDefinitions).ResolveAsync(resolved,
                new Dictionary<string, IReadOnlyList<FlowCompositionCall>>(), parent.Scope.AccessScopeRef, cancellationToken);
        resolved = composed.Definition;
        ValidateInput(resolved.Graph?.ResolveInputSchema(), input, parent.SecretInputs);
        var now = timeProvider.GetUtcNow();
        var child = new FlowRun
        {
            WorkspaceId = parent.WorkspaceId,
            Id = childRunId,
            FlowId = targetId,
            FlowVersion = resolved.Version,
            Composition = composed.Receipt,
            TransientPayloads = parent.TransientPayloads,
            ProcessOrigin = processOrigin,
            ArtifactProcessing = processingContext ?? parent.ArtifactProcessing,
            ContractContext = contractContext ?? (resolved.Graph?.ResolveInputSchema() is null ? FlowDataContracts.ChildContext(parent.DefinitionSnapshot.Graph, call.Name, parent.ContractContext) : null),
            RuntimeBindings = inlineVersion is null ? [] : parent.RuntimeBindings,
            DefinitionState = FlowDefinitionState.Published,
            DefinitionHash = resolved.DefinitionHash,
            DefinitionScopeRef = resolved.ScopeRef,
            DefinitionSnapshotId = DefinitionSnapshotIdentity(resolved),
            DefinitionSnapshot = resolved,
            DeploymentResourceId = parent.DeploymentResourceId,
            Trigger = FlowRunTrigger.Flow,
            StartedBy = parent.StartedBy,
            CorrelationId = parent.CorrelationId,
            ResolvedFromActiveReference = call.Flow.VersionStrategy == FlowCallVersionStrategy.Active,
            ParentFlowRunId = parent.Id,
            RootFlowRunId = rootRunId,
            NestingDepth = depth,
            InteractionId = parent.InteractionId,
            WorkTaskId = parent.WorkTaskId,
            TriggerMessageId = parent.TriggerMessageId,
            Scope = parent.Scope,
            Input = input.Clone(),
            SecretInputs = parent.SecretInputs,
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
            var recovered = await repository.GetRunExactAsync(parent.Scope.ScopeRef, childRunId, cancellationToken);
            if (recovered is null) throw;
            created = recovered;
            if (inlineVersion is not null && (created.Value.DefinitionHash != inlineVersion.DefinitionHash
                || !CompatibleProcessOrigin(created.Value, processOrigin, processingContext) || !JsonElement.DeepEquals(created.Value.Input, input)))
                throw new FlowValidationException("process_child_conflict", "The admitted process unit cannot change its definition, identity or input.");
            ValidateChildIdentity(parent, call, created.Value, childRunId);
            return created;
        }

        await AttachChildAsync(parent.Scope.ScopeRef, parent.Id, ownerStepName ?? call.Name, child.Id, artifactStorage, cancellationToken);
        RunsCreated.Add(1, new KeyValuePair<string, object?>("flow.definition.state", child.DefinitionState.ToString()));
        try
        {
            await EmitAsync(child.Scope.ScopeRef, child.Id, FlowRunEventType.FlowRunCreated, null,
                JsonSerializer.SerializeToElement(new { child.Status, child.DefinitionState, child.ParentFlowRunId, child.RootFlowRunId, child.NestingDepth }), cancellationToken);
            await EmitAsync(parent.Scope.ScopeRef, parent.Id, FlowRunEventType.ChildFlowRunCreated, call.Name,
                JsonSerializer.SerializeToElement(new { childFlowRunId = child.Id, flowId = child.FlowId.Value, flowNamespace = child.FlowId.Namespace.Value, flowVersion = child.FlowVersion }), cancellationToken);
        }
        finally
        {
            await queue.EnqueueAsync(new(child.Id, child.Scope), cancellationToken);
        }
        return created;
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
            || child.Scope != parent.Scope)
            throw new FlowValidationException("child_flow_identity_mismatch", $"Persisted child Flow Run for step '{call.Name}' does not match the declared target and parent execution scope.");
    }

    private async Task ResumeParentAfterChildAsync(FlowRun child, CancellationToken cancellationToken)
    {
        if (child.ParentFlowRunId is null) return;
        var parent = await repository.GetRunExactAsync(child.Scope.ScopeRef, child.ParentFlowRunId, cancellationToken);
        if (parent is null
            || parent.Value.Status != FlowRunStatus.WaitingForChild
            || parent.Value.Scope != child.Scope
            || !parent.Value.Steps.Any(step => step.Status == FlowStepRunStatus.Running
                && (step.ChildFlowRunId == child.Id || step.ArtifactStorageFlowRunId == child.Id)))
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
            await EmitAsync(resumed.Value.Scope.ScopeRef, resumed.Value.Id, FlowRunEventType.FlowRunResumedFromChild,
                resumed.Value.Steps.Single(step => step.ChildFlowRunId == child.Id
                    || step.ArtifactStorageFlowRunId == child.Id).StepName,
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
            foreach (var key in keys.Where(key => key.ScopeRef == parent.Scope.ScopeRef))
            {
                var run = await repository.GetRunExactAsync(parent.Scope.ScopeRef, key.RunId, cancellationToken);
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
