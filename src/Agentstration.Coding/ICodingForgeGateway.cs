using Agentstration.Coding.Contracts;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Coding;

/// <summary>Reviews and invokes one explicit, captured Forge connection through the governed provider pipeline.</summary>
public interface ICodingForgeGateway
{
    Task<Result<WorkForgeDeliveryRoute>> InspectAsync(WorkspaceId workspace, Guid principal,
        WorkProjectResolvedBinding connection, CodingRepositoryIdentity repository, CancellationToken token);
    Result<string> Prepare(WorkTaskDeliveryIntent intent, WorkCodePublicationReceipt code);
    Task<Result<WorkForgeDeliveryResponse>> InvokeAsync(WorkspaceId workspace, Guid principal,
        WorkTaskDeliveryOperation operation, string operationKey, bool recover, CancellationToken token);
}
