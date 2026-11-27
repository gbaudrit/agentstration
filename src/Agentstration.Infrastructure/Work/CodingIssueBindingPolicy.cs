using Agentstration.Application.Work;
using Agentstration.Coding;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

// GitHub issue-realization policy is an adopter of ATC, not part of the tracking contract.
public sealed class CodingIssueBindingPolicy(IWorkTaskProviderGateway gateway, IResourceStore resources) : ICodingIssueBindingPolicy
{
    public async Task<Result<CodingIssueBinding>> ResolveAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectContextBinding binding, WorkSharingIssueReference issue, CancellationToken token)
    {
        if (!WorkSharingContent.ValidIssue(issue) || issue.TaskId[0] is not (>= '1' and <= '9') || !issue.TaskId.All(char.IsAsciiDigit)
            || binding.Connection is null || binding.Purpose != WorkProjectBindingPurpose.Tracking
            || binding.Name != issue.ConnectionName || issue.Tracking != binding.Connection.Requirement.Target || issue.Provider != "github")
            return Failure("coding_issue_connection_invalid");
        var resolved = await gateway.ResolveAsync(workspaceId, principalId, binding, ExternalTaskOperationKind.Update, token);
        if (!resolved.IsSuccess) return Failure(resolved.ErrorCode!);
        var route = resolved.Value!; var tracking = route.Tracking;
        if (tracking?.Connection.Contract?.ImplementationId != "Agentstration.Extensions.GitHub"
            || tracking.Connection.Service is not { } evidence || route.Binding.Contract is not { } target
            || issue.Tracking != new WorkTrackingTarget(target.ProviderId, target.TargetId)
            || !tracking.Capabilities.Operations.Contains("read") || !tracking.Capabilities.Operations.Contains("update")
            || !tracking.Capabilities.UpdateFields.Contains("classifications.add")
            || !new[] { WorkTrackingRelationKind.Parent, WorkTrackingRelationKind.Child, WorkTrackingRelationKind.BlockedBy }.All(tracking.Capabilities.RelationKinds.Contains)
            || tracking.Capabilities.Recovery == WorkTrackingRecovery.None || tracking.Capabilities.Concurrency == WorkTrackingConcurrency.None)
            return Failure("coding_issue_capability_missing");
        // Resolve authorized standard configuration only. Secret values remain behind the existing protected transport.
        var service = (await resources.GetExactAsync<ConnectedServiceResource>(evidence.Service.Address, token))?.Value;
        if (service is null || service.Uid != evidence.Service.Uid || service.Generation != evidence.Service.Generation
            || !service.Definition.Options.TryGetProperty("repository", out var repository) || repository.ValueKind != System.Text.Json.JsonValueKind.String
            || !string.Equals(repository.GetString(), issue.Repository, StringComparison.OrdinalIgnoreCase)
            || !service.Definition.Options.TryGetProperty("managedLabelPrefix", out var label) || label.ValueKind != System.Text.Json.JsonValueKind.String
            || label.GetString() is not { Length: > 0 } prefix
            || !issue.PlanLabelPrefix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !issue.ParticipantLabelPrefix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return Failure("coding_issue_service_policy_mismatch");
        return Result<CodingIssueBinding>.Success(new(issue.Provider, route));
    }

    public Result<CodingIssueObservation> Observe(ExternalTaskSnapshot snapshot, WorkSharingIssueReference issue, WorkParticipantId participant)
        => GitHubIssueObservation(snapshot, issue, participant);

    public ExternalTaskOperationInput Acknowledgment(WorkSharingIssueReference issue, WorkPlanId plan, WorkParticipantId participant, string revision)
        => GitHubAcknowledgment(issue, plan, participant, revision);

    public static ExternalTaskOperationInput GitHubAcknowledgment(WorkSharingIssueReference issue, WorkPlanId plan, WorkParticipantId participant, string revision) =>
        new(ExternalTaskOperationKind.Update, TaskId: issue.TaskId,
            Labels: [issue.PlanLabelPrefix + plan, issue.ParticipantLabelPrefix + participant], ExpectedRevision: revision)
        { Tracking = issue.Tracking };

    public static Result<CodingIssueObservation> GitHubIssueObservation(ExternalTaskSnapshot snapshot, WorkSharingIssueReference issue, WorkParticipantId participant)
    {
        if (snapshot.Tracking is not { RelationsComplete: true } tracking
            || tracking.Identity != new WorkTrackingItem(issue.Tracking!.ProviderId, issue.Tracking.TargetId, issue.TaskId))
            return Result<CodingIssueObservation>.Failure("coding_issue_authority_incomplete");
        var labels = snapshot.Labels.Where(label => label.StartsWith(issue.PlanLabelPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        var valid = labels.Length == 0 || labels.Length == 1 && Guid.TryParseExact(labels[0][issue.PlanLabelPrefix.Length..], "D", out var parsed) && parsed != Guid.Empty;
        WorkPlanId? plan = valid && labels.Length == 1 ? new(Guid.Parse(labels[0][issue.PlanLabelPrefix.Length..])) : null;
        var dependencies = tracking.Relations.Where(value => value.Kind == WorkTrackingRelationKind.BlockedBy).Select(value => value.Item).Distinct().ToArray();
        return Result<CodingIssueObservation>.Success(new(snapshot.Revision, valid, plan,
            snapshot.Labels.Contains(issue.ParticipantLabelPrefix + participant, StringComparer.OrdinalIgnoreCase),
            snapshot.State == "open", snapshot.State == "closed" && snapshot.CompletionReason == "completed", snapshot.State, snapshot.CompletionReason, dependencies));
    }
    private static Result<CodingIssueBinding> Failure(string code) => Result<CodingIssueBinding>.Failure(code);
}
