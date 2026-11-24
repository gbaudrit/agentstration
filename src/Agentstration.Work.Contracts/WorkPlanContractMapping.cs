namespace Agentstration.Work.Contracts;

public static class WorkPlanContractMapping
{
    public static WorkPlanDefinition ToPlanDefinition(WorkPlanDefinitionRequest? request) => request is null ? null!
        : new(request.Intent, request.TaskIds?.Select(id => new WorkTaskId(id)).ToArray() ?? [],
            request.Dependencies?.Select(edge => edge is null ? null! : new WorkTaskDependency(new(edge.TaskId), new(edge.DependsOnTaskId))).ToArray() ?? []);

    public static WorkPlanRevisionResponse ToPlanRevisionResponse(WorkPlanRevision revision) => new(revision.PlanId.Value, revision.Number,
        revision.Definition.Intent, revision.Definition.TaskIds.Select(id => id.Value).ToArray(),
        revision.Definition.Dependencies.Select(edge => new WorkTaskDependencyRequest(edge.TaskId.Value, edge.DependsOnTaskId.Value)).ToArray(),
        revision.Tasks.Select(task => new WorkPlanTaskReferenceResponse(task.TaskId.Value, task.CreationDigest)).ToArray(), revision.Digest, revision.CreatedAt);

    public static WorkPlanResponse ToPlanResponse(WorkPlanView view) => new(view.Plan.Id.Value, view.Plan.WorkspaceId.Value, view.Plan.Status,
        view.Plan.Version, view.Plan.CurrentRevision, view.Plan.ValidatedRevision, view.Plan.ValidatedDigest, view.Plan.CreatedAt, view.Plan.UpdatedAt,
        ToPlanRevisionResponse(view.Revision), view.Tasks.Select(task => new WorkPlanTaskStateResponse(task.TaskId.Value, task.Version, task.Outcome,
            task.Readiness, task.Reasons, task.UnmetDependencies.Select(id => id.Value).ToArray())).ToArray())
        {
            Execution = view.Plan.Execution is { } execution ? new(execution.Revision, execution.RevisionDigest, execution.Enabled) { ProjectId = execution.ProjectId?.Value } : null,
            Attempts = view.Plan.Attempts.Select(attempt => new WorkPlanAttemptResponse(attempt.Id, attempt.TaskId.Value, attempt.PlanRevision,
                attempt.PlanDigest, attempt.TaskVersion, attempt.TaskDigest, attempt.WorkItemId.Value, attempt.FlowRunId, attempt.Flow,
                attempt.Dependencies.Select(item => new WorkPlanTaskObservationResponse(item.TaskId.Value, item.Version)).ToArray(),
                attempt.Status, attempt.CancellationRequested, attempt.AdmittedAt, attempt.UpdatedAt, attempt.Diagnostic, attempt.AdmissionEvidence)
            {
                ProjectContext = attempt.ProjectContext is { } context ? new(context.ProjectId.Value, context.ProjectVersion, context.Digest, context.Bindings, context.CapturedAt) : null
            }).ToArray()
        };
}
