using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flows.Application;

public sealed partial class FlowRunService
{
    private static bool CompatibleProcessOrigin(FlowRun saved, FlowProcessRunOrigin? requested,
        FlowArtifactProcessingContext? context) => SameProcessOrigin(saved.ProcessOrigin, requested)
            // Verified legacy admission also checks exact deterministic ID, parent/scope, graph hash and input.
            || saved.ProcessOrigin is null && requested is not null && context is not null && saved.ArtifactProcessing == context;

    private static bool SameProcessOrigin(FlowProcessRunOrigin? left, FlowProcessRunOrigin? right) =>
        left is null ? right is null : right is not null
            && left with { OwnerStepExecutionId = null } == right with { OwnerStepExecutionId = null };

    private async Task AdmitProcessUnitAsync(FlowRun parent, FlowProcessRegion region,
        FlowProcessRunOrigin origin, string childId, int? totalUnits, bool requiresClosure, CancellationToken token)
    {
        await MutateProcessOwnerAsync(parent.Scope.ScopeRef, parent.Id, origin.OwnerStepName, current =>
        {
            var process = current.Process;
            if (process is not null && process.Attempt != origin.OwnerAttempt) process = null;
            process ??= new(origin.OwnerAttempt, region.Key, false, []) { TotalUnits = totalUnits, RequiresClosure = requiresClosure };
            if (totalUnits is not null && process.TotalUnits != totalUnits)
                throw new FlowValidationException("process_total_conflict", "The admitted process total cannot change.");
            var prior = process.Units.SingleOrDefault(unit => unit.Key == origin.UnitKey || unit.Ordinal == origin.Ordinal);
            if (prior is not null)
            {
                if (prior.Key != origin.UnitKey || prior.Ordinal != origin.Ordinal || prior.RunId != childId || prior.Descriptor != origin.Descriptor)
                    throw new FlowValidationException("process_unit_conflict", "The admitted process key or ordinal belongs to different work.");
                return current;
            }
            if (process.Closed) throw new FlowValidationException("process_admission_closed", "Process work-unit admission is closed for this step attempt.");
            if (process.Units.Count >= region.MaximumUnits)
                throw new FlowValidationException("process_unit_limit", "The owning step's process unit limit was reached.");
            return current with { Process = process with { Units = [.. process.Units, new(origin.UnitKey, origin.Ordinal, childId, origin.Descriptor)] } };
        }, token);
    }

    public async Task CloseProcessAdmissionExactAsync(ResourceScopeRef scope, string ownerRunId,
        string stepName, CancellationToken token)
    {
        var owner = await repository.GetRunExactAsync(scope, ownerRunId, token) ?? throw new FlowRunNotFoundException(ownerRunId);
        var definition = owner.Value.DefinitionSnapshot.Graph?.Steps.SingleOrDefault(step => step.Name == stepName);
        var region = definition?.Region() ?? throw new FlowValidationException("process_not_declared", "The step does not expose a process region.");
        await MutateProcessOwnerAsync(scope, ownerRunId, stepName, step => step with
        {
            Process = (step.Process ?? new(Math.Max(1, step.Attempt), region.Key, false, [])) with { Closed = true, TotalUnits = step.Process?.TotalUnits ?? step.Process?.Units.Count ?? 0 }
        }, token);
    }

    private async Task AttachChildAsync(ResourceScopeRef scope, string parentId, string stepName, string childId, bool artifactStorage, CancellationToken token) =>
        await MutateProcessOwnerAsync(scope, parentId, stepName, step => step.ChildFlowRunIds.Contains(childId, StringComparer.Ordinal)
            ? step : step with { ChildFlowRunId = artifactStorage ? step.ChildFlowRunId : childId,
                ArtifactStorageFlowRunId = artifactStorage ? childId : step.ArtifactStorageFlowRunId,
                ChildFlowRunIds = [.. step.ChildFlowRunIds, childId] }, token);

    private async Task MutateProcessOwnerAsync(ResourceScopeRef scope, string parentId, string stepName,
        Func<FlowStepRun, FlowStepRun> mutate, CancellationToken token)
    {
        for (var retry = 0; ; retry++)
        {
            token.ThrowIfCancellationRequested();
            var stored = await repository.GetRunExactAsync(scope, parentId, token) ?? throw new FlowRunNotFoundException(parentId);
            var step = stored.Value.Steps.Single(value => value.StepName == stepName);
            var changed = mutate(step);
            if (ReferenceEquals(step, changed)) return;
            if (stored.Value.Status.IsTerminal()) throw new FlowValidationException("process_owner_terminal", "A terminal run cannot admit or change process work.");
            try
            {
                await repository.UpdateRunAsync(stored.Value with { Steps = stored.Value.Steps.Select(value => value.StepName == stepName ? changed : value).ToArray() }, stored.ETag, token);
                return;
            }
            catch (FlowConcurrencyException) when (retry < 20) { }
        }
    }

    private async Task ReserveDescendantAsync(FlowRun parent, string childId, CancellationToken token)
    {
        var rootId = parent.RootFlowRunId ?? parent.Id;
        for (var retry = 0; ; retry++)
        {
            var stored = await repository.GetRunExactAsync(parent.Scope.ScopeRef, rootId, token) ?? throw new FlowRunNotFoundException(rootId);
            var reserved = stored.Value.ReservedDescendantRunIds;
            if (reserved is null)
            {
                // Seed only from exact persisted relationships, never correlation or display names.
                var ids = new List<string>();
                for (var skip = 0; ; skip += 200)
                {
                    var page = await repository.ListRunKeysAsync(skip, 200, token);
                    foreach (var key in page.Where(key => key.ScopeRef == parent.Scope.ScopeRef))
                    {
                        var candidate = await repository.GetRunExactAsync(key.ScopeRef, key.RunId, token);
                        if (candidate?.Value.RootFlowRunId == rootId && candidate.Value.Scope == parent.Scope) ids.Add(key.RunId);
                    }
                    if (page.Count < 200) break;
                }
                reserved = ids;
            }
            if (reserved.Contains(childId, StringComparer.Ordinal)) return;
            if (reserved.Count >= executionOptions.MaximumDescendantRuns)
                throw new FlowValidationException("flow_descendant_limit_exceeded", "The root Flow descendant admission limit was reached.");
            if (stored.Value.Status.IsTerminal()) throw new FlowValidationException("process_owner_terminal", "The root Flow is terminal.");
            try
            {
                await repository.UpdateRunAsync(stored.Value with { ReservedDescendantRunIds = [.. reserved, childId] }, stored.ETag, token);
                return;
            }
            catch (FlowConcurrencyException) when (retry < 20) { }
        }
    }
}
