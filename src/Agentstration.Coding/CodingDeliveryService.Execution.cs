using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Coding.Contracts;
using Agentstration.Resources;
using Agentstration.Work;
using Microsoft.Extensions.Logging;

namespace Agentstration.Coding;

public sealed partial class CodingDeliveryService
{
    public async Task<Result<WorkTaskDeliveryOperation>> ResumeAsync(WorkspaceId workspace, WorkProjectId project, WorkTaskDeliveryId id, CancellationToken token)
    {
        var loaded = await GetAsync(workspace, project, id, token);
        if (!loaded.IsSuccess || Terminal(loaded.Value!)) return loaded;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var activity = WorkItemService.ActivitySource.StartActivity("work.coding-delivery.resume");
        activity?.SetTag("workspace.id", workspace.ToString()); activity?.SetTag("work.project.id", project.ToString());
        activity?.SetTag("work.delivery.id", id.ToString()); activity?.SetTag("flow.run.id", loaded.Value!.Intent.Source.FlowRunId);
        try { return await RunAsync(loaded.Value!, linked.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            var retained = await GetAsync(workspace, project, id, token);
            return !retained.IsSuccess || Terminal(retained.Value!) ? retained
                : await UncertainAsync(retained.Value!, "coding_delivery_timeout", token);
        }
    }
    private async Task<Result<WorkTaskDeliveryOperation>> RunAsync(WorkTaskDeliveryOperation operation, CancellationToken token)
    {
        for (var iteration = 0; iteration < 12; iteration++)
        {
            token.ThrowIfCancellationRequested();
            if (Terminal(operation)) return Result<WorkTaskDeliveryOperation>.Success(operation);
            var owned = await projects.GetAsync(operation.WorkspaceId, operation.ProjectId, token);
            if (!owned.IsSuccess) return Failure(owned.ErrorCode!);
            if (owned.Value!.Version != operation.ProjectVersion || !SameBindings(owned.Value, operation.Intent.ProjectContext))
                return Failure("coding_delivery_connection_changed");
            var intent = operation.Intent; Result<WorkTaskDeliveryOperation> saved;
            switch (operation.Stage)
            {
                case WorkTaskDeliveryStage.Prepared:
                    saved = await SaveAsync(operation, operation with { Stage = WorkTaskDeliveryStage.Evidence }, token);
                    break;
                case WorkTaskDeliveryStage.Evidence:
                    if (intent.ExistingEvidenceId is { } existing)
                    {
                        var plan = await plans.GetAsync(operation.WorkspaceId, intent.PlanId, token);
                        if (!plan.IsSuccess || plan.Value!.Plan.Attempts.SingleOrDefault(value => value.Id == intent.Source.AttemptId)?.Evidence?.EventId != existing)
                            return await UncertainAsync(operation, "coding_delivery_evidence_changed", token);
                        saved = await SaveAsync(operation, operation with { EvidenceId = existing, Stage = NextAfterEvidence(intent) }, token);
                    }
                    else
                    {
                        var eventId = Identifier(operation, "delivery-evidence/1.0");
                        var recorded = await evidence.RecordAsync(operation.WorkspaceId, intent.PlanId, intent.TaskId,
                            new(eventId, intent.Source.AttemptId, intent.PlanVersion, intent.TaskVersion, intent.AdmittedTaskVersion,
                                intent.TaskDigest, intent.Title, intent.ResultIds, intent.ArtifactIds), token);
                        if (!recorded.IsSuccess) return await UncertainAsync(operation, recorded.ErrorCode!, token);
                        saved = await SaveAsync(operation, operation with { EvidenceId = eventId, Stage = NextAfterEvidence(intent) }, token);
                    }
                    break;
                case WorkTaskDeliveryStage.CodePublication:
                    if (operation.Code?.Status == WorkTaskDeliveryEffectStatus.Succeeded)
                    { saved = await SaveAsync(operation, operation with { Stage = WorkTaskDeliveryStage.PullRequest }, token); break; }
                    var binding = Binding(intent.ProjectContext, WorkProjectBindingPurpose.CodePublication)!;
                    var codeKey = Key(operation, "delivery-code/1.0");
                    var codeDigest = WorkPlanDefinition.Digest(new { Kind = "delivery-code/1.0", operation.IntentDigest });
                    // Recover even on the first call. Missing is the only permission to submit the frozen intent.
                    var published = await code.PublishAsync(operation.WorkspaceId, operation.OwnerPrincipalId, binding, intent, codeKey, codeDigest, true, token);
                    if (!published.IsSuccess) return await UncertainAsync(operation, published.ErrorCode!, token);
                    if (published.Value!.Status == WorkTaskDeliveryEffectStatus.Missing)
                    {
                        if (operation.Code?.Revision is not null) return await UncertainAsync(operation, "coding_delivery_publication_receipt_missing", token);
                        var current = await NewMutationAuthorityAsync(operation, token);
                        if (current is not null)
                        { saved = await AuthorityFailureAsync(operation, current, token); if (saved.Value?.Status == WorkTaskDeliveryStatus.Uncertain) return saved; break; }
                        published = await code.PublishAsync(operation.WorkspaceId, operation.OwnerPrincipalId, binding, intent, codeKey, codeDigest, false, token);
                        if (!published.IsSuccess) return await UncertainAsync(operation, published.ErrorCode!, token);
                    }
                    var codeReceipt = published.Value!;
                    if (codeReceipt.Status is WorkTaskDeliveryEffectStatus.Rejected or WorkTaskDeliveryEffectStatus.Conflict)
                    { saved = await RejectAsync(operation with { Code = codeReceipt }, codeReceipt.ErrorCode ?? "coding_delivery_code_rejected", token); break; }
                    if (codeReceipt.Status != WorkTaskDeliveryEffectStatus.Succeeded)
                        return await UncertainAsync(operation with { Code = codeReceipt }, codeReceipt.ErrorCode ?? "coding_delivery_publication_uncertain", token);
                    saved = await SaveAsync(operation, operation with { Code = codeReceipt, Stage = WorkTaskDeliveryStage.PullRequest }, token);
                    break;
                case WorkTaskDeliveryStage.PullRequest:
                    if (operation.PullRequest is not null)
                    { saved = await SaveAsync(operation, operation with { Stage = WorkTaskDeliveryStage.Contribution }, token); break; }
                    var input = new CodingPullRequestInput(intent.Repository, Title: intent.Title, Body: intent.Body,
                        Head: intent.HeadRef["refs/heads/".Length..], Base: intent.BaseBranch, ExpectedHeadRevision: operation.Code!.Revision)
                    { ExpectedBaseRevision = intent.BaseRevision };
                    var command = new InvokeCodingPullRequestRequest(Identifier(operation, "delivery-pull-request/1.0"), intent.TaskId.Value, intent.TaskVersion,
                        CodingProviderProtocol.CreatePullRequest, input, Binding(intent.ProjectContext, WorkProjectBindingPurpose.Repository)!.Target, operation.ProjectVersion, true);
                    var pull = await repositories.InvokePullRequestAsync(operation.WorkspaceId, operation.ProjectId, command, token);
                    if (!pull.IsSuccess) return await UncertainAsync(operation, pull.ErrorCode!, token);
                    if (pull.Value!.Result.Status == CodingProviderStatus.Missing)
                    {
                        var current = await NewMutationAuthorityAsync(operation, token);
                        if (current is not null)
                        { saved = await AuthorityFailureAsync(operation, current, token); if (saved.Value?.Status == WorkTaskDeliveryStatus.Uncertain) return saved; break; }
                        pull = await repositories.InvokePullRequestAsync(operation.WorkspaceId, operation.ProjectId, command with { Recover = false }, token);
                        if (!pull.IsSuccess) return await UncertainAsync(operation, pull.ErrorCode!, token);
                    }
                    if (pull.Value!.Result.Status == CodingProviderStatus.Rejected)
                    { saved = await RejectAsync(operation, pull.Value.Result.ErrorCode ?? "coding_delivery_pull_request_rejected", token); break; }
                    if (pull.Value.Result.Status != CodingProviderStatus.Succeeded || pull.Value.Result.PullRequest is not { } pr)
                        return await UncertainAsync(operation, pull.Value.Result.ErrorCode ?? "coding_delivery_pull_request_uncertain", token);
                    var receipt = new WorkPullRequestReceipt(pr.Provider, pr.Repository, pr.NativeId, pr.Number, pr.Url, pr.Head, pr.HeadRevision, pr.Base, pr.BaseRevision, pr.Draft, pr.Merged);
                    var withPull = operation with { PullRequest = receipt, Stage = WorkTaskDeliveryStage.Contribution };
                    if (!WorkTaskDeliveryFormat.Valid(withPull)) return await UncertainAsync(operation, "coding_delivery_pull_request_response_invalid", token);
                    saved = await SaveAsync(operation, withPull, token);
                    break;
                case WorkTaskDeliveryStage.Contribution:
                    var contribution = Contribution(operation);
                    var contributionId = new WorkSharingOperationId(Identifier(operation, "delivery-contribution-publication/1.0"));
                    var progress = await sharing.PublishAsync(operation.WorkspaceId, operation.ProjectId, new(contributionId, operation.ProjectVersion,
                        [new(new(WorkSharingDomain.Contribution, contribution.Id.ToString("D")), 0, JsonSerializer.SerializeToElement(contribution, Json))]), token);
                    if (!progress.IsSuccess) return await UncertainAsync(operation, progress.ErrorCode!, token);
                    if (progress.Value!.Status != WorkSharingOperationStatus.Succeeded)
                        return await UncertainAsync(operation, progress.Value.ErrorCode ?? "coding_delivery_progress_uncertain", token);
                    saved = await SaveAsync(operation, operation with { ContributionId = contributionId, ContributionPublished = true, Stage = WorkTaskDeliveryStage.Usage }, token);
                    break;
                case WorkTaskDeliveryStage.Usage:
                    if (operation.Usage.Count == 0)
                    {
                        var captured = await usage.CaptureAsync(operation.WorkspaceId, operation.ProjectId, intent.PlanId, intent.Source.AttemptId, token);
                        if (!captured.IsSuccess) return await UncertainAsync(operation, captured.ErrorCode!, token);
                        saved = await SaveAsync(operation, operation with { Usage = captured.Value!.Items.Select(value => new WorkTaskDeliveryUsage(value.Record.Id, value.Digest)).ToArray() }, token);
                        break;
                    }
                    var remaining = operation.Usage.FirstOrDefault(value => value.PublicationId is null);
                    if (remaining is not null)
                    {
                        var measured = await usage.PublishAsync(operation.WorkspaceId, operation.ProjectId, remaining.Id, token);
                        if (!measured.IsSuccess) return await UncertainAsync(operation, measured.ErrorCode!, token);
                        if (measured.Value!.Status != WorkSharingOperationStatus.Succeeded)
                            return await UncertainAsync(operation, measured.Value.ErrorCode ?? "coding_delivery_usage_uncertain", token);
                        saved = await SaveAsync(operation, operation with { Usage = operation.Usage.Select(value => value.Id == remaining.Id ? value with { PublicationId = measured.Value.Id } : value).ToArray() }, token);
                        break;
                    }
                    var status = operation.RejectionCode is not null ? WorkTaskDeliveryStatus.Rejected : intent.Source.Outcome switch
                    {
                        WorkPlanAttemptStatus.Completed => WorkTaskDeliveryStatus.Delivered,
                        WorkPlanAttemptStatus.Cancelled => WorkTaskDeliveryStatus.Cancelled,
                        _ => WorkTaskDeliveryStatus.Failed
                    };
                    saved = await SaveAsync(operation, operation with { Stage = WorkTaskDeliveryStage.Finished, Status = status, ErrorCode = operation.RejectionCode ?? intent.Source.Diagnostic }, token);
                    if (saved.IsSuccess && logger.IsEnabled(LogLevel.Information)) logger.LogInformation("Retained delivery {DeliveryId} for Project {ProjectId}, Plan {PlanId}, Task {TaskId}, attempt {AttemptId}, Flow Run {FlowRunId} in Workspace {WorkspaceId}: {DeliveryStatus}",
                        operation.Id.Value, operation.ProjectId.Value, intent.PlanId.Value, intent.TaskId.Value, intent.Source.AttemptId, intent.Source.FlowRunId, operation.WorkspaceId.Value, saved.Value!.Status);
                    break;
                default: return Failure("coding_delivery_state_invalid");
            }
            if (!saved.IsSuccess) return saved;
            operation = saved.Value!;
        }
        return Result<WorkTaskDeliveryOperation>.Success(operation);
    }
    private async Task<string?> NewMutationAuthorityAsync(WorkTaskDeliveryOperation operation, CancellationToken token)
    {
        var intent = operation.Intent;
        var project = await projects.GetAsync(operation.WorkspaceId, operation.ProjectId, token);
        if (!project.IsSuccess || project.Value!.Version != operation.ProjectVersion || !SameBindings(project.Value, intent.ProjectContext)) return "coding_delivery_connection_changed";
        var plan = await plans.GetAsync(operation.WorkspaceId, intent.PlanId, token);
        if (!plan.IsSuccess || plan.Value!.Revision.Number != intent.PlanRevision || plan.Value.Revision.Digest != intent.PlanDigest) return "coding_delivery_review_stale";
        var task = (await work.GetAsync(operation.WorkspaceId, operation.OwnerPrincipalId, intent.TaskId.ToWorkItemId(), token))?.Value;
        if (task is null || task.Version != intent.TaskVersion || task.CurrentExecutionId != new WorkExecutionId(intent.Source.AttemptId)) return "coding_delivery_task_stale";
        var pulled = await sharing.PullAsync(operation.WorkspaceId, operation.ProjectId, null, token);
        if (!pulled.IsSuccess) return pulled.ErrorCode;
        var record = pulled.Value!.Records.SingleOrDefault(value => value.Key == new WorkSharingRecordKey(WorkSharingDomain.Plan, intent.PlanId.ToString()));
        if (record?.Version != intent.SharedPlanVersion || record.Digest != intent.SharedPlanDigest) return "coding_delivery_shared_plan_stale";
        var readiness = await issues.ReadinessAsync(operation.WorkspaceId, operation.ProjectId, intent.PlanId, token);
        if (!readiness.IsSuccess) return readiness.ErrorCode;
        if (!readiness.Value!.Eligible || readiness.Value.Repository != intent.Issue.Repository || readiness.Value.IssueId != intent.Issue.TaskId) return "coding_issue_dependencies_blocked";
        var resolved = await repositories.ResolveAsync(operation.WorkspaceId, operation.ProjectId, new(intent.TaskId.Value, [intent.Repository], intent.TaskVersion), token);
        if (!resolved.IsSuccess) return resolved.ErrorCode;
        var identity = resolved.Value!.Repositories.Single();
        if (identity.Provider != intent.Provider || identity.NativeId != intent.RepositoryNativeId || identity.CloneUrl != intent.RepositoryCloneUrl
            || identity.DefaultBranch != intent.BaseBranch) return "coding_delivery_repository_changed";
        return identity.Revision == intent.BaseRevision ? null : "coding_delivery_base_stale";
    }
    private async Task<Result<WorkTaskDeliveryOperation>> SaveAsync(WorkTaskDeliveryOperation previous, WorkTaskDeliveryOperation next, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        next = next with { Version = previous.Version + 1, UpdatedAt = now < previous.UpdatedAt ? previous.UpdatedAt : now };
        if (!WorkTaskDeliveryFormat.Valid(next) || !WorkTaskDeliveryFormat.Preserves(previous, next))
            throw new InvalidOperationException("The delivery transition is invalid.");
        if (await repository.UpdateAsync(next, previous.Version, token)) return Result<WorkTaskDeliveryOperation>.Success(next);
        var current = await repository.GetAsync(previous.WorkspaceId, previous.OwnerPrincipalId, previous.ProjectId, previous.Id, token);
        return current is null ? Failure("coding_delivery_state_conflict") : Result<WorkTaskDeliveryOperation>.Success(current);
    }
    private Task<Result<WorkTaskDeliveryOperation>> UncertainAsync(WorkTaskDeliveryOperation operation, string error, CancellationToken token) =>
        SaveAsync(operation, operation with { Status = WorkTaskDeliveryStatus.Uncertain, ErrorCode = SafeCode(error) }, token);
    private Task<Result<WorkTaskDeliveryOperation>> AuthorityFailureAsync(WorkTaskDeliveryOperation operation, string error, CancellationToken token) =>
        error is "coding_delivery_base_stale" or "coding_delivery_review_stale" or "coding_delivery_task_stale" or "coding_delivery_shared_plan_stale"
            or "coding_delivery_repository_changed" ? RejectAsync(operation, error, token) : UncertainAsync(operation, error, token);
    private Task<Result<WorkTaskDeliveryOperation>> RejectAsync(WorkTaskDeliveryOperation operation, string error, CancellationToken token) =>
        SaveAsync(operation, operation with
        {
            Status = WorkTaskDeliveryStatus.Pending,
            Stage = WorkTaskDeliveryStage.Contribution,
            RejectionCode = SafeCode(error),
            ErrorCode = SafeCode(error)
        }, token);
    private static WorkTaskDeliveryStage NextAfterEvidence(WorkTaskDeliveryIntent intent) => intent.Source.Outcome == WorkPlanAttemptStatus.Completed
        ? WorkTaskDeliveryStage.CodePublication : WorkTaskDeliveryStage.Contribution;
    private static bool Terminal(WorkTaskDeliveryOperation operation) => operation.Status is WorkTaskDeliveryStatus.Delivered or WorkTaskDeliveryStatus.Rejected
        or WorkTaskDeliveryStatus.Failed or WorkTaskDeliveryStatus.Cancelled;
    private static string SafeCode(string code) => code is { Length: > 0 and <= 128 }
        && code.All(value => char.IsAsciiLetterOrDigit(value) || value is '_' or '-' or '.') ? code : "coding_delivery_provider_unavailable";
    private static WorkSharingContribution Contribution(WorkTaskDeliveryOperation operation)
    {
        var intent = operation.Intent;
        var identity = WorkPlanDefinition.Digest(new
        {
            Kind = "coding-delivery/1.0",
            intent.ProjectContext.Sharing!.ProjectId,
            intent.ProjectContext.Sharing.ParticipantId,
            intent.PlanId,
            intent.TaskId,
            intent.Source.AttemptId,
            operation.Id
        });
        var id = new Guid(Convert.FromHexString(identity).AsSpan(0, 16));
        var data = JsonSerializer.Serialize(new
        {
            formatVersion = "1.0",
            deliveryId = operation.Id,
            issue = intent.Issue,
            planId = intent.PlanId,
            taskId = intent.TaskId,
            planRevision = intent.PlanRevision,
            planDigest = intent.PlanDigest,
            taskVersion = intent.AdmittedTaskVersion,
            taskDigest = intent.TaskDigest,
            sharedPlanVersion = intent.SharedPlanVersion,
            sharedPlanDigest = intent.SharedPlanDigest,
            attemptId = intent.Source.AttemptId,
            participantId = intent.ProjectContext.Sharing.ParticipantId,
            title = intent.Title,
            outcome = operation.RejectionCode is not null ? "publication-rejected" : intent.Source.Outcome == WorkPlanAttemptStatus.Completed ? "code-published-for-review"
                : intent.Source.Outcome == WorkPlanAttemptStatus.Cancelled ? "execution-cancelled" : "execution-failed",
            taskAccepted = false,
            sourceDigest = intent.Source.Digest,
            flowRunId = intent.Source.FlowRunId,
            checks = intent.Checks,
            codeRevision = operation.Code?.Status == WorkTaskDeliveryEffectStatus.Succeeded ? operation.Code.Revision : null,
            baseRevision = intent.BaseRevision,
            pullRequest = operation.PullRequest,
            evidenceId = operation.EvidenceId,
            diagnostic = operation.RejectionCode ?? intent.Source.Diagnostic
        }, Json);
        var references = new List<string> { "flow:" + intent.Source.FlowRunId };
        if (operation.Code?.Status == WorkTaskDeliveryEffectStatus.Succeeded) references.Add($"git:{intent.Repository}@{operation.Code.Revision}");
        if (operation.PullRequest is { } pull) references.Add($"{pull.Provider}:{pull.Repository}!{pull.Number}");
        return new(id, intent.PlanId, intent.TaskId, "coding-delivery", data, references);
    }
}
