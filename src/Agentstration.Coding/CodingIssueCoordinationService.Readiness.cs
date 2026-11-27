using Agentstration.Application.Work;
using Agentstration.Coding.Contracts;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Coding;

public sealed partial class CodingIssueCoordinationService
{
    public async Task<Result<CodingIssueReadiness>> ReadinessAsync(WorkspaceId workspaceId, WorkProjectId projectId, WorkPlanId planId, CancellationToken token)
    {
        var project = await projects.GetAsync(workspaceId, projectId, token);
        if (!project.IsSuccess) return Result<CodingIssueReadiness>.Failure(project.ErrorCode!);
        if (!project.Value!.Definition.PlanIds.Contains(planId)) return Result<CodingIssueReadiness>.Failure("work_project_plan_not_associated");
        var local = await plans.GetAsync(workspaceId, planId, token);
        if (!local.IsSuccess) return Result<CodingIssueReadiness>.Failure(local.ErrorCode!);
        if (project.Value.Definition.Sharing is null) return Readiness(projectId, planId, null, null, []);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { return await ObserveAsync(project.Value, planId, linked.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Readiness(projectId, planId, null, null, [new("", "", null, "authority_timeout", null, null, null)]);
        }
    }
    private async Task<Result<CodingIssueReadiness>> ObserveAsync(WorkProjectSnapshot project, WorkPlanId planId, CancellationToken token)
    {
        var pulled = await sharing.PullAsync(project.WorkspaceId, project.Id, null, token);
        if (!pulled.IsSuccess) return Readiness(project.Id, planId, null, null, [new("", "", null, pulled.ErrorCode!, null, null, null)]);
        var shared = SharedPlans(pulled.Value!);
        var target = shared.SingleOrDefault(value => value.Plan.PlanId == planId).Plan;
        if (target is null) return Readiness(project.Id, planId, null, null, [new("", "", null, "shared_plan_missing", null, null, null)]);
        if (target.Issue is not { } issue)
            return !(await sharing.GetPlanOperationAsync(project.WorkspaceId, project.Id, planId, token)).IsSuccess
                ? Readiness(project.Id, planId, null, null, [])
                : Readiness(project.Id, planId, null, null, [new("", "", planId.Value, "issue_association_missing", null, null, null)]);
        if (shared.Count(value => SameIssue(value.Plan.Issue, issue)) != 1)
            return Readiness(project.Id, planId, issue, null, [new(issue.Repository, issue.TaskId, planId.Value, "issue_plan_conflict", null, null, null)]);
        var connected = await ConnectAsync(project.WorkspaceId, project.Id, issue, token);
        if (!connected.IsSuccess) return Readiness(project.Id, planId, issue, null, [new(issue.Repository, issue.TaskId, planId.Value, connected.ErrorCode!, null, null, null)]);
        var connection = connected.Value!;
        if (!SharedConnectionMatches(connection.Project, pulled.Value!, issue))
            return Readiness(project.Id, planId, issue, null, [new(issue.Repository, issue.TaskId, planId.Value, "coding_issue_shared_connection_mismatch", null, null, null)]);
        var native = await ReadIssueAsync(connection, issue, token);
        if (!native.IsSuccess) return Readiness(project.Id, planId, issue, null, [new(issue.Repository, issue.TaskId, planId.Value, native.ErrorCode!, null, null, null)]);
        var source = native.Value!; var label = (Valid: source.AssociationValid, Id: source.PlanId);
        var acknowledgmentId = (await sharing.GetPlanOperationAsync(project.WorkspaceId, project.Id, planId, token)).Value?.OperationId;
        var acknowledgment = acknowledgmentId is { } operationId ? await operations.GetAsync(project.WorkspaceId, project.Id, operationId, token) : null;
        var waits = new List<CodingIssueWait>();
        var prerequisites = new List<CodingIssuePrerequisite>();
        if (acknowledgment is not { IsSuccess: true, Value.Status: WorkTaskOperationStatus.Succeeded, Value: { } receipt }
            || receipt.PlanId != planId || !SameRoute(receipt.Route, connection.Route)
            || WorkPlanDefinition.Digest(receipt.Input) != WorkPlanDefinition.Digest(bindings.Acknowledgment(issue, planId, project.Definition.Sharing!.ParticipantId, receipt.Input.ExpectedRevision!)))
            waits.Add(new(issue.Repository, issue.TaskId, planId.Value, "intake_publication_pending", source.State, source.CompletionReason, source.Revision));
        if (!label.Valid || label.Id != planId) waits.Add(new(issue.Repository, issue.TaskId, label.Id?.Value, "issue_plan_label_conflict", source.State, source.CompletionReason, source.Revision));
        if (!source.Acknowledged)
            waits.Add(new(issue.Repository, issue.TaskId, planId.Value, "participant_acknowledgment_missing", source.State, source.CompletionReason, source.Revision));
        if (!source.Open) waits.Add(new(issue.Repository, issue.TaskId, planId.Value, "issue_not_open", source.State, source.CompletionReason, source.Revision));
        var dependencies = source.Dependencies;
        if (dependencies.Count > 16) waits.Add(new(issue.Repository, issue.TaskId, planId.Value, "dependency_observation_limit", source.State, source.CompletionReason, source.Revision));
        else foreach (var dependency in dependencies)
        {
            // Native hierarchy is deliberately not converted into a dependency edge.
            var reference = issue with { Tracking = new(dependency.ProviderId, dependency.TargetId), TaskId = dependency.ItemId };
            var upstream = shared.Where(value => SameIssue(value.Plan.Issue, reference)).ToArray();
            WorkPlanId? upstreamId = upstream.Length == 1 ? upstream[0].Plan.PlanId : null;
            var reason = upstream.Length == 0 ? "upstream_plan_missing" : upstream.Length > 1 ? "upstream_plan_conflict"
                : upstreamId == planId ? "upstream_plan_not_distinct" : null;
            if (reason is not null)
            {
                waits.Add(new(reference.Repository, dependency.ItemId, upstreamId?.Value, reason, null, null, null)); continue;
            }
            var upstreamIssue = upstream[0].Plan.Issue!;
            // Every upstream repository must independently satisfy the protected allowlist and capability policy.
            var access = await ConnectAsync(project.WorkspaceId, project.Id, upstreamIssue, token);
            if (!access.IsSuccess)
            {
                waits.Add(new(reference.Repository, dependency.ItemId, upstreamId?.Value, access.ErrorCode!, null, null, null)); continue;
            }
            var observation = await ReadIssueAsync(access.Value!, upstreamIssue, token);
            if (!observation.IsSuccess)
            {
                waits.Add(new(reference.Repository, dependency.ItemId, upstreamId?.Value, observation.ErrorCode!, null, null, null)); continue;
            }
            var observed = observation.Value!; var upstreamLabel = (Valid: observed.AssociationValid, Id: observed.PlanId);
            reason = !upstreamLabel.Valid || upstreamLabel.Id != upstreamId ? "upstream_plan_label_conflict"
                : observed.Open ? "upstream_issue_open"
                : !observed.Completed ? "upstream_completion_unconfirmed" : null;
            prerequisites.Add(new(reference.Repository, dependency.ItemId, upstreamId!.Value.Value, observed.State, observed.CompletionReason, observed.Revision, reason is null));
            if (reason is not null) waits.Add(new(reference.Repository, dependency.ItemId, upstreamId?.Value, reason, observed.State, observed.CompletionReason, observed.Revision));
        }
        // Eligibility is an observation. It never cancels or retries an already admitted local attempt.
        return Readiness(project.Id, planId, issue, source.Revision, waits, prerequisites, WorkPlanDefinition.Digest(connection.Route));
    }
    private Result<CodingIssueReadiness> Readiness(WorkProjectId projectId, WorkPlanId planId, WorkSharingIssueReference? issue,
        string? revision, IReadOnlyList<CodingIssueWait> waits, IReadOnlyList<CodingIssuePrerequisite>? prerequisites = null, string? connectionDigest = null) => Result<CodingIssueReadiness>.Success(new(projectId.Value, planId.Value,
            waits.Count == 0, issue?.Repository, issue?.TaskId, revision, waits, clock.GetUtcNow())
        { Prerequisites = prerequisites ?? [], ConnectionDigest = connectionDigest });
    private static bool SameRoute(WorkTaskProviderRoute retained, WorkTaskProviderRoute current) =>
        WorkPlanDefinition.Digest(retained with { Tracking = retained.Tracking is { } original ? original with { WireIntentDigest = null } : null })
            == WorkPlanDefinition.Digest(current with { Tracking = current.Tracking is { } fresh ? fresh with { WireIntentDigest = null } : null });
}

public sealed class CodingIssueAdmissionGate(CodingIssueCoordinationService issues) : IWorkPlanAdmissionGate
{
    public async Task<Result<IReadOnlyList<WorkPlanAdmissionEvidence>>> ObserveAsync(FlowRunScope scope, WorkPlanSnapshot plan,
        WorkPlanRevision revision, WorkTaskId taskId, CancellationToken cancellationToken)
    {
        if (plan.Execution?.ProjectId is not { } project) return Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Success([]);
        var result = await issues.ReadinessAsync(scope.WorkspaceId, project, plan.Id, cancellationToken);
        if (!result.IsSuccess) return Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Failure(result.ErrorCode!);
        var readiness = result.Value!;
        if (!readiness.Eligible) return Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Failure("coding_issue_dependencies_blocked");
        return Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Success(readiness.IssueId is null ? [] : [new("repository-issue",
            $"{readiness.Repository}#{readiness.IssueId}:{plan.Id}", WorkPlanDefinition.Digest(new { readiness.IssueRevision, readiness.Prerequisites, readiness.ConnectionDigest }), readiness.ObservedAt)]);
    }
}
