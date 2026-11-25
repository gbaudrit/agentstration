using Agentstration.Coding;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed partial class GitWorkProjectSharingGateway
{
    public async Task<Result<WorkCodePublicationReceipt>> PublishAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
        WorkTaskDeliveryIntent intent, string operationKey, string intentDigest, bool recover, CancellationToken token)
    {
        if (!WorkTaskDeliveryFormat.ValidIntent(intent) || !WorkSharingFormat.Digest(operationKey) || !WorkSharingFormat.Digest(intentDigest)
            || !CodingCodeChanges.Valid(intent.Changes.Select(change => new Agentstration.Coding.Contracts.CodingFileChange(change.Path, change.ContentBase64, change.Executable)).ToArray()))
            return Result<WorkCodePublicationReceipt>.Failure("coding_delivery_code_invalid");
        var route = await ResolveGitAsync(workspaceId, principalId, binding, WorkProjectBindingPurpose.CodePublication, intent.HeadRef,
            intent.Changes.Select(change => change.Path).ToArray(), token);
        if (!route.IsSuccess) return Result<WorkCodePublicationReceipt>.Failure(route.ErrorCode!);
        if (!Uri.TryCreate(route.Value!.Repository, UriKind.Absolute, out var repository) || repository != intent.RepositoryCloneUrl)
            return Result<WorkCodePublicationReceipt>.Failure("coding_delivery_repository_mismatch");
        var input = new
        {
            refName = intent.HeadRef,
            baseRevision = intent.BaseRevision,
            expectedRefRevision = (string?)null,
            changes = intent.Changes.Select(change => new { path = change.Path, contentBase64 = change.ContentBase64, executable = change.Executable }).ToArray(),
            message = $"Deliver issue {intent.Issue.TaskId} for Plan {intent.PlanId}"
        };
        var result = await ExecuteGitAsync(workspaceId, principalId, route.Value, "publish", recover ? "recover" : "execute", operationKey, intentDigest, input, token);
        if (!result.IsSuccess) return Result<WorkCodePublicationReceipt>.Failure(result.ErrorCode!);
        var response = result.Value!;
        if (response.Status == "Succeeded" && (response.BaseRevision != intent.BaseRevision || response.Revision is null))
            return Result<WorkCodePublicationReceipt>.Failure("coding_delivery_publication_invalid");
        return Result<WorkCodePublicationReceipt>.Success(new(operationKey, intentDigest, Enum.Parse<WorkTaskDeliveryEffectStatus>(response.Status),
            intent.HeadRef, intent.BaseRevision, response.Revision, response.CurrentRevision, response.ErrorCode));
    }
}
