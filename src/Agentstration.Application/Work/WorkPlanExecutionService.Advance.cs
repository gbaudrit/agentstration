using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed partial class WorkPlanExecutionService
{
    public async Task<Result<WorkPlanView>> AdvanceAsync(WorkspaceId workspaceId, WorkPlanId id, CancellationToken token)
    {
        var loaded = await planService.GetAsync(workspaceId, id, token);
        if (!loaded.IsSuccess) return loaded;
        var view = loaded.Value!;
        if (view.Plan.Execution is not { } execution) return loaded;
        var scope = Scope(workspaceId);
        if (scope != execution.Scope) return Failure("work_execution_scope_mismatch");
        await authorizer.AuthorizeAsync(scope, token);
        foreach (var pending in view.Plan.Attempts.Where(IsOpen).ToArray())
        {
            var updated = await ProgressAttemptAsync(view, pending, token);
            if (!updated.IsSuccess) return updated;
            view = updated.Value!;
        }
        if (view.Plan.Status != WorkPlanStatus.Active || view.Plan.Execution?.Enabled != true) return Result<WorkPlanView>.Success(view);
        if (view.Plan.Execution.Revision != view.Revision.Number || view.Plan.Execution.RevisionDigest != view.Revision.Digest)
            return Failure("work_plan_revision_conflict");
        foreach (var target in execution.Targets)
        {
            // Reread readiness after each durable admission. Never dispatch from an old view.
            loaded = await planService.GetAsync(workspaceId, id, token);
            if (!loaded.IsSuccess) return loaded;
            view = loaded.Value!;
            if (view.Plan.Execution?.Enabled != true || view.Plan.Status != WorkPlanStatus.Active) break;
            if (view.Tasks.Single(task => task.TaskId == target.TaskId).Readiness != WorkPlanTaskReadiness.Ready) continue;
            var members = await MembersAsync(view, token);
            if (members.Any(item => view.Tasks.Single(state => state.TaskId.ToWorkItemId() == item.Value.Id).Version != item.Value.Version))
                return Failure("work_plan_conflict");
            var task = members.Single(item => WorkTaskId.FromWorkItem(item.Value.Id) == target.TaskId).Value;
            if (view.Plan.Attempts.Any(attempt => attempt.TaskId == target.TaskId && IsOpen(attempt))) continue;
            WorkProjectContextSnapshot? projectContext = null;
            if (view.Plan.Execution.ProjectId is { } projectId)
            {
                var captured = await projectContexts.CaptureAsync(workspaceId, projectId, id, token);
                if (!captured.IsSuccess) return Failure(captured.ErrorCode!);
                projectContext = captured.Value!;
            }
            var evidence = new List<WorkPlanAdmissionEvidence>();
            foreach (var gate in gates)
            {
                var observation = await gate.ObserveAsync(scope, view.Plan, view.Revision, target.TaskId, token);
                if (!observation.IsSuccess) return Failure(observation.ErrorCode!);
                evidence.AddRange(observation.Value!);
            }
            if (evidence.Count > 32 || evidence.Any(item => string.IsNullOrWhiteSpace(item.Kind) || item.Kind.Length > 128
                || string.IsNullOrWhiteSpace(item.Reference) || item.Reference.Length > 1024 || string.IsNullOrWhiteSpace(item.Revision)
                || item.Revision.Length > 256 || item.ObservedAt > clock.GetUtcNow()))
                return Failure("work_plan_admission_evidence_invalid");
            // Resolve access again at admission; the stored target is already an exact published version.
            var resolved = await targets.ResolveAsync(scope, target.Flow, target.Input, token);
            if (resolved.Reference != target.Flow) return Failure("work_plan_execution_target_changed");
            var now = clock.GetUtcNow();
            var attemptId = Guid.NewGuid();
            var workId = WorkItemId.New();
            var observations = Observe(members);
            var dependencies = view.Revision.Definition.Dependencies.Where(edge => edge.TaskId == target.TaskId)
                .Select(edge => observations.Single(item => item.TaskId == edge.DependsOnTaskId)).ToArray();
            var attempt = new WorkPlanAttempt(attemptId, target.TaskId, view.Revision.Number, view.Revision.Digest,
                task.Version, task.TaskHierarchy!.CreationDigest, workId, $"flowrun-root-{workId.Value:N}", target.Flow,
                target.Input, dependencies, WorkPlanAttemptStatus.Admitted, false, now, now) { AdmissionEvidence = evidence, ProjectContext = projectContext };
            var version = task.Version;
            task.AdmitPlanAttempt(id, attemptId, new(attemptId), now);
            var admitted = await SaveObservedAsync(view, view.Plan with { Attempts = [.. view.Plan.Attempts, attempt] }, attemptId,
                WorkPlanAction.AdvanceExecution, WorkPlanDefinition.Digest(attempt), observations, [new(task, version)], token,
                projectContext is null ? null : new(projectContext.ProjectId, projectContext.ProjectVersion));
            if (!admitted.IsSuccess) return admitted;
            var progressed = await ProgressAttemptAsync(admitted.Value!, attempt, token);
            if (!progressed.IsSuccess) return progressed;
            view = progressed.Value!;
        }
        return await planService.GetAsync(workspaceId, id, token);
    }

    private static IReadOnlyDictionary<string, string> AttemptMetadata(WorkPlanSnapshot plan, WorkPlanAttempt attempt)
    {
        var metadata = new Dictionary<string, string>
        {
            ["workPlan.id"] = plan.Id.ToString(), ["workPlan.revision"] = attempt.PlanRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["workPlan.attemptId"] = attempt.Id.ToString("D"), ["workplace.taskId"] = attempt.TaskId.ToString()
        };
        if (attempt.ProjectContext is { } context)
        {
            metadata["workProject.id"] = context.ProjectId.ToString();
            metadata["workProject.version"] = context.ProjectVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            metadata["workProject.contextDigest"] = context.Digest;
        }
        return metadata;
    }

    private async Task<Result<WorkPlanView>> ProgressAttemptAsync(WorkPlanView view, WorkPlanAttempt attempt, CancellationToken token)
    {
        var execution = view.Plan.Execution!;
        var members = await MembersAsync(view, token);
        var task = members.Single(item => WorkTaskId.FromWorkItem(item.Value.Id) == attempt.TaskId).Value;
        var cancelling = attempt.CancellationRequested || task.Status == WorkItemStatus.Cancelled;
        if (attempt.Status == WorkPlanAttemptStatus.Admitted)
        {
            // The attempt is already durable. An uncertain submission is replayed with the same WorkItem/root Run identities.
            await submissions.SubmitAsync(new SubmitRootFlowCommand(view.Plan.WorkspaceId, attempt.Flow, attempt.Input,
                FlowInvocationOrigin.Api, execution.Scope.PrincipalId.ToString("D"), FlowRunTrigger.WorkItem,
                IdempotencyKey: attempt.Id.ToString("D"), CausationId: view.Plan.Id.ToString(),
                CorrelationId: $"plan:{view.Plan.Id.Value:N}:attempt:{attempt.Id:N}", Type: "flow",
                Instruction: task.Instruction, Title: task.Title, WorkTaskId: attempt.TaskId.ToString(), WorkItemId: attempt.WorkItemId,
                Metadata: AttemptMetadata(view.Plan, attempt)), execution.Scope.PrincipalId, token);
        }
        if (cancelling) await flows.CancelAsync(execution.Scope, attempt.FlowRunId, token);
        var run = await flows.GetAsync(execution.Scope, attempt.FlowRunId, token);
        if (run is null) return Failure("work_plan_execution_run_unavailable");
        if (run.Scope != execution.Scope || run.WorkItemResourceId != attempt.WorkItemId.ToString()
            || run.FlowId != attempt.Flow.Resolve(attempt.Flow.FlowId.Namespace) || run.FlowVersion != attempt.Flow.Version)
            return Failure("work_plan_execution_run_mismatch");
        var status = run.Status switch
        {
            FlowRunStatus.Succeeded => WorkPlanAttemptStatus.Completed,
            FlowRunStatus.Cancelled => WorkPlanAttemptStatus.Cancelled,
            FlowRunStatus.Failed or FlowRunStatus.TimedOut => WorkPlanAttemptStatus.Failed,
            _ => WorkPlanAttemptStatus.Submitted
        };
        if (attempt.Status == status) return Result<WorkPlanView>.Success(view);
        var now = clock.GetUtcNow();
        var changed = attempt with { Status = status, UpdatedAt = now, Diagnostic = run.Error?.Code };
        var observations = Observe(members);
        var updates = new List<WorkTaskTreeUpdate>();
        if (!IsOpen(changed))
        {
            var version = task.Version;
            var result = status == WorkPlanAttemptStatus.Completed
                ? new WorkResult([new WorkResultContent(Structured: run.Output?.Clone())], [],
                    new Dictionary<string, string> { ["flowRunId"] = attempt.FlowRunId, ["attemptId"] = attempt.Id.ToString("D") }, now)
                : null;
            var error = status == WorkPlanAttemptStatus.Completed ? null : new WorkError(run.Error?.Code ?? "work_plan_execution_failed",
                "The task execution did not complete successfully.", status == WorkPlanAttemptStatus.Cancelled ? WorkErrorCategory.Cancelled : WorkErrorCategory.Execution,
                true, now, new(attempt.Id));
            await workItems.ObservePlanFlowOutcomeAsync(attempt.WorkItemId, run, result, error, token);
            task.ObservePlanAttempt(Guid.NewGuid(), new(attempt.Id), status switch
            {
                WorkPlanAttemptStatus.Completed => WorkItemStatus.Completed,
                WorkPlanAttemptStatus.Cancelled => WorkItemStatus.Cancelled,
                _ => WorkItemStatus.Failed
            }, result, error, now);
            if (task.Version != version) updates.Add(new(task, version));
        }
        return await SaveObservedAsync(view, view.Plan with { Attempts = view.Plan.Attempts.Select(item => item.Id == attempt.Id ? changed : item).ToArray() },
            Guid.NewGuid(), WorkPlanAction.AdvanceExecution, WorkPlanDefinition.Digest(changed), observations, updates, token);
    }
}
