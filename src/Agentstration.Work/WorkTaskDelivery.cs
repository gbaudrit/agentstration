using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Work;

public readonly record struct WorkTaskDeliveryId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}
[JsonConverter(typeof(JsonStringEnumConverter<WorkTaskDeliveryStatus>))]
public enum WorkTaskDeliveryStatus { Pending, Uncertain, Delivered, Rejected, Failed, Cancelled }
[JsonConverter(typeof(JsonStringEnumConverter<WorkTaskDeliveryStage>))]
public enum WorkTaskDeliveryStage { Prepared, Evidence, CodePublication, PullRequest, Contribution, Usage, Finished }
[JsonConverter(typeof(JsonStringEnumConverter<WorkTaskDeliveryEffectStatus>))]
public enum WorkTaskDeliveryEffectStatus { Missing, Succeeded, Rejected, Uncertain, Conflict }

public sealed record WorkCodeFile(string Path, string? ContentBase64, bool Executable);
public sealed record WorkCodeCheck(string Name, int? ExitCode, bool Completed, string? Diagnostic);
public sealed record WorkTaskDeliverySource(Guid AttemptId, WorkItemId WorkItemId, string FlowRunId, Guid AssignmentId,
    Guid AssignmentAttemptId, Guid InvocationId, string Digest, WorkPlanAttemptStatus Outcome, string? Diagnostic);
public sealed record WorkTaskDeliveryIntent(WorkPlanId PlanId, long PlanVersion, long PlanRevision, string PlanDigest,
    WorkTaskId TaskId, long TaskVersion, long AdmittedTaskVersion, string TaskDigest, WorkProjectContextSnapshot ProjectContext,
    WorkSharingIssueReference Issue, long SharedPlanVersion, string SharedPlanDigest, WorkTaskDeliverySource Source,
    string Repository, string Provider, string RepositoryNativeId, Uri RepositoryCloneUrl, string BaseRevision, string BaseBranch, string HeadRef,
    string Title, string Body, IReadOnlyList<WorkCodeFile> Changes, IReadOnlyList<WorkCodeCheck> Checks)
{
    public IReadOnlyList<WorkTaskResultId> ResultIds { get; init; } = [];
    public IReadOnlyList<WorkTaskArtifactId> ArtifactIds { get; init; } = [];
    public Guid? ExistingEvidenceId { get; init; }
}
public sealed record WorkCodePublicationReceipt(string OperationKey, string IntentDigest, WorkTaskDeliveryEffectStatus Status,
    string RefName, string BaseRevision, string? Revision = null, string? CurrentRevision = null, string? ErrorCode = null);
public sealed record WorkPullRequestReceipt(string Provider, string Repository, string NativeId, string Number, Uri Url,
    string Head, string HeadRevision, string Base, string BaseRevision, bool Draft, bool Merged);
public sealed record WorkTaskDeliveryUsage(WorkUsageId Id, string Digest, WorkSharingOperationId? PublicationId = null);

/// <summary>Private functional delivery intent and effects. Technical publication is delegated to Coding/provider ports.</summary>
public sealed record WorkTaskDeliveryOperation(WorkTaskDeliveryId Id, WorkspaceId WorkspaceId, Guid OwnerPrincipalId,
    WorkProjectId ProjectId, long ProjectVersion, WorkTaskDeliveryIntent Intent, string IntentDigest,
    WorkTaskDeliveryStatus Status, WorkTaskDeliveryStage Stage, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public WorkCodePublicationReceipt? Code { get; init; }
    public WorkPullRequestReceipt? PullRequest { get; init; }
    public Guid? EvidenceId { get; init; }
    public WorkSharingOperationId? ContributionId { get; init; }
    public bool ContributionPublished { get; init; }
    public IReadOnlyList<WorkTaskDeliveryUsage> Usage { get; init; } = [];
    public string? ErrorCode { get; init; }
    public string? RejectionCode { get; init; }
}
public sealed record WorkTaskDeliveryPage(IReadOnlyList<WorkTaskDeliveryOperation> Items, bool HasMore);

public static class WorkTaskDeliveryFormat
{
    public const int MaximumIntentBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Digest(WorkTaskDeliveryIntent intent) => WorkPlanDefinition.Digest(intent);
    public static bool Valid(WorkTaskDeliveryOperation? value) => value is not null && value.Id.Value != Guid.Empty && value.WorkspaceId.Value != Guid.Empty
        && value.OwnerPrincipalId != Guid.Empty && value.ProjectId.Value != Guid.Empty && value.ProjectVersion >= 1 && value.Version >= 1
        && Enum.IsDefined(value.Status) && Enum.IsDefined(value.Stage) && value.CreatedAt != default && value.UpdatedAt >= value.CreatedAt
        && ValidIntent(value.Intent) && value.IntentDigest == Digest(value.Intent) && value.Intent.ProjectContext.ProjectId == value.ProjectId
        && (value.Code is null || Enum.IsDefined(value.Code.Status) && value.Code.RefName == value.Intent.HeadRef
            && value.Code.BaseRevision == value.Intent.BaseRevision && WorkSharingFormat.Digest(value.Code.OperationKey) && WorkSharingFormat.Digest(value.Code.IntentDigest)
            && (value.Code.Status != WorkTaskDeliveryEffectStatus.Succeeded || Revision(value.Code.Revision)))
        && (value.PullRequest is null || value.Code?.Status == WorkTaskDeliveryEffectStatus.Succeeded && value.PullRequest.HeadRevision == value.Code.Revision
            && value.PullRequest.BaseRevision == value.Intent.BaseRevision && value.PullRequest.Base == value.Intent.BaseBranch
            && value.PullRequest.Head == value.Intent.HeadRef["refs/heads/".Length..] && value.PullRequest.Repository == value.Intent.Repository
            && value.PullRequest.Provider == value.Intent.Provider && value.PullRequest.Draft && !value.PullRequest.Merged
            && value.PullRequest.Url is { IsAbsoluteUri: true } url && url.Scheme is "https" or "http" && string.IsNullOrEmpty(url.UserInfo)
            && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment))
        && value.Usage is { Count: <= 32 } && value.Usage.All(item => item is not null && item.Id.Value != Guid.Empty && WorkSharingFormat.Digest(item.Digest))
        && value.Usage.Select(item => item.Id).Distinct().Count() == value.Usage.Count && (value.ErrorCode is null || Code(value.ErrorCode))
        && (value.Code?.Status != WorkTaskDeliveryEffectStatus.Succeeded || value.Intent.Source.Outcome == WorkPlanAttemptStatus.Completed)
        && (value.Stage < WorkTaskDeliveryStage.CodePublication || value.EvidenceId is not null)
        && (value.Intent.Source.Outcome != WorkPlanAttemptStatus.Completed || value.RejectionCode is not null
            || value.Stage < WorkTaskDeliveryStage.PullRequest || value.Code?.Status == WorkTaskDeliveryEffectStatus.Succeeded)
        && (value.Intent.Source.Outcome != WorkPlanAttemptStatus.Completed || value.RejectionCode is not null
            || value.Stage < WorkTaskDeliveryStage.Contribution || value.PullRequest is not null)
        && (value.RejectionCode is null || Code(value.RejectionCode))
        && (!value.ContributionPublished || value.ContributionId is not null)
        && (value.Stage != WorkTaskDeliveryStage.Finished || value.Status is WorkTaskDeliveryStatus.Delivered or WorkTaskDeliveryStatus.Rejected or WorkTaskDeliveryStatus.Failed or WorkTaskDeliveryStatus.Cancelled)
        && (value.Status != WorkTaskDeliveryStatus.Delivered || value.Stage == WorkTaskDeliveryStage.Finished
            && value.RejectionCode is null && value.Intent.Source.Outcome == WorkPlanAttemptStatus.Completed && value.Code?.Status == WorkTaskDeliveryEffectStatus.Succeeded
            && value.PullRequest is not null && value.EvidenceId is not null && value.ContributionPublished
            && value.Usage.Count > 0 && value.Usage.All(item => item.PublicationId is not null));
    public static bool Preserves(WorkTaskDeliveryOperation previous, WorkTaskDeliveryOperation next) => Valid(next)
        && next.Id == previous.Id && next.WorkspaceId == previous.WorkspaceId && next.OwnerPrincipalId == previous.OwnerPrincipalId
        && next.ProjectId == previous.ProjectId && next.ProjectVersion == previous.ProjectVersion && next.IntentDigest == previous.IntentDigest
        && next.CreatedAt == previous.CreatedAt && next.Version == previous.Version + 1 && next.Stage >= previous.Stage
        && previous.Status is WorkTaskDeliveryStatus.Pending or WorkTaskDeliveryStatus.Uncertain
        && (previous.Code?.Status != WorkTaskDeliveryEffectStatus.Succeeded || previous.Code == next.Code)
        && (previous.PullRequest is null || previous.PullRequest == next.PullRequest)
        && (previous.EvidenceId is null || previous.EvidenceId == next.EvidenceId)
        && (previous.ContributionId is null || previous.ContributionId == next.ContributionId)
        && (!previous.ContributionPublished || next.ContributionPublished)
        && (previous.RejectionCode is null || previous.RejectionCode == next.RejectionCode)
        && previous.Usage.All(item => next.Usage.Any(value => value.Id == item.Id && value.Digest == item.Digest
            && (item.PublicationId is null || value.PublicationId == item.PublicationId)));

    public static bool ValidIntent(WorkTaskDeliveryIntent? value) => value is not null && value.PlanId.Value != Guid.Empty && value.PlanVersion >= 1
        && value.PlanRevision >= 1 && WorkSharingFormat.Digest(value.PlanDigest) && value.TaskId.Value != Guid.Empty
        && value.TaskVersion >= 1 && value.AdmittedTaskVersion >= 1 && WorkSharingFormat.Digest(value.TaskDigest)
        && value.ProjectContext is { Sharing: not null } && value.SharedPlanVersion >= 1 && WorkSharingFormat.Digest(value.SharedPlanDigest)
        && value.Issue is not null && WorkSharingContent.ValidIssue(value.Issue) && value.Issue.Repository == value.Repository
        && value.Source is { } source && source.AttemptId != Guid.Empty && source.WorkItemId.Value != Guid.Empty
        && Text(source.FlowRunId, 200) && source.AssignmentId != Guid.Empty && source.AssignmentAttemptId != Guid.Empty && source.InvocationId != Guid.Empty
        && WorkSharingFormat.Digest(source.Digest) && source.Outcome is WorkPlanAttemptStatus.Completed or WorkPlanAttemptStatus.Failed or WorkPlanAttemptStatus.Cancelled
        && (source.Diagnostic is null || Code(source.Diagnostic)) && Text(value.Repository, 512) && Text(value.Provider, 64) && Text(value.RepositoryNativeId, 256)
        && value.RepositoryCloneUrl is { IsAbsoluteUri: true } clone && clone.Scheme is "https" or "http" && string.IsNullOrEmpty(clone.UserInfo)
        && string.IsNullOrEmpty(clone.Query) && string.IsNullOrEmpty(clone.Fragment) && Revision(value.BaseRevision) && Text(value.BaseBranch, 256) && value.HeadRef is { Length: <= 256 } && value.HeadRef.StartsWith("refs/heads/", StringComparison.Ordinal)
        && Text(value.Title, 512) && value.Body is { Length: <= 16_000 } && value.Changes is { Count: <= 32 } && value.Checks is { Count: <= 16 }
        && value.Changes.All(file => file is not null && Text(file.Path, 256) && file.ContentBase64?.Length is not > 32 * 1024)
        && value.Checks.All(check => check is not null && Text(check.Name, 128) && (check.Diagnostic is null || Code(check.Diagnostic)))
        && value.ResultIds is { Count: <= 64 } && value.ArtifactIds is { Count: <= 64 }
        && value.ResultIds.All(id => id.Value != Guid.Empty) && value.ArtifactIds.All(id => id.Value != Guid.Empty)
        && value.ResultIds.Distinct().Count() == value.ResultIds.Count && value.ArtifactIds.Distinct().Count() == value.ArtifactIds.Count
        && value.ExistingEvidenceId != Guid.Empty && JsonSerializer.SerializeToUtf8Bytes(value, Json).Length <= MaximumIntentBytes;
    public static bool Revision(string? value) => value is { Length: 40 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Text(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool Code(string? value) => value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
}
