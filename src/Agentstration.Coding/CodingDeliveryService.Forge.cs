using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Coding;

public sealed partial class CodingDeliveryService
{
    private async Task<Result<WorkTaskDeliveryOperation>> AdvanceForgeAsync(WorkTaskDeliveryOperation operation, CancellationToken token)
    {
        if (operation.PullRequest is not null)
            return await SaveAsync(operation, operation with { Stage = WorkTaskDeliveryStage.Contribution, Status = WorkTaskDeliveryStatus.Pending, ErrorCode = null }, token);
        if (operation.ForgeWireIntentDigest is null)
        {
            var prepared = forge.Prepare(operation.Intent, operation.Code!);
            return !prepared.IsSuccess ? await UncertainAsync(operation, prepared.ErrorCode!, token)
                : await SaveAsync(operation, operation with { ForgeWireIntentDigest = prepared.Value, Status = WorkTaskDeliveryStatus.Pending, ErrorCode = null }, token);
        }
        var key = Key(operation, "delivery-forge/1.0");
        var response = await forge.InvokeAsync(operation.WorkspaceId, operation.OwnerPrincipalId, operation, key, true, token);
        if (!response.IsSuccess) return await UncertainAsync(operation, response.ErrorCode!, token);
        var receipt = response.Value!;
        if (!Matches(receipt)) return await UncertainAsync(operation, "coding_delivery_forge_response_invalid", token);
        if (receipt.Status == WorkTaskDeliveryEffectStatus.Missing)
        {
            var route = operation.Intent.Forge!;
            if (operation.ForgeSubmission is { } submitted && (route.Recovery != WorkForgeRecovery.Durable
                || clock.GetUtcNow() >= submitted.StartedAt.AddSeconds(route.ReceiptWindowSeconds)))
                return await UncertainAsync(operation, "coding_delivery_forge_recovery_unavailable", token);
            var authority = await NewMutationAuthorityAsync(operation, token);
            if (authority is not null) return await AuthorityFailureAsync(operation, authority, token);
            if (operation.ForgeSubmission is null)
            {
                // Only the winner of this durable reservation may perform the first submit. A
                // process-scoped Missing after a crash can never prove that submit did not happen.
                var startedAt = clock.GetUtcNow();
                var claim = new WorkForgeSubmission(Guid.NewGuid(), startedAt < operation.UpdatedAt ? operation.UpdatedAt : startedAt);
                var reserved = await SaveAsync(operation, operation with { ForgeSubmission = claim }, token);
                if (!reserved.IsSuccess || reserved.Value!.ForgeSubmission?.Id != claim.Id
                    || reserved.Value.Stage != WorkTaskDeliveryStage.PullRequest || Terminal(reserved.Value)) return reserved;
                operation = reserved.Value;
            }
            response = await forge.InvokeAsync(operation.WorkspaceId, operation.OwnerPrincipalId, operation, key, false, token);
            if (!response.IsSuccess) return await UncertainAsync(operation, response.ErrorCode!, token);
            receipt = response.Value!;
            if (!Matches(receipt)) return await UncertainAsync(operation, "coding_delivery_forge_response_invalid", token);
        }
        if (receipt.Status is WorkTaskDeliveryEffectStatus.Rejected or WorkTaskDeliveryEffectStatus.Conflict)
            return await RejectAsync(operation, receipt.ErrorCode ?? "coding_delivery_pull_request_rejected", token);
        if (receipt.Status != WorkTaskDeliveryEffectStatus.Succeeded || receipt.Review is null)
            return await UncertainAsync(operation, receipt.ErrorCode ?? "coding_delivery_pull_request_uncertain", token);
        var next = operation with { PullRequest = receipt.Review, Stage = WorkTaskDeliveryStage.Contribution, Status = WorkTaskDeliveryStatus.Pending, ErrorCode = null };
        if (!WorkTaskDeliveryFormat.Valid(next)) return await UncertainAsync(operation, "coding_delivery_forge_response_invalid", token);
        return await SaveAsync(operation, next, token);

        bool Matches(WorkForgeDeliveryResponse value) => value.OperationKey == key && value.WireIntentDigest == operation.ForgeWireIntentDigest;
    }
}
