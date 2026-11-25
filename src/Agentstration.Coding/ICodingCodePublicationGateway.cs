using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Coding;

/// <summary>Publishes or recovers exact produced code through the captured protected repository binding.</summary>
public interface ICodingCodePublicationGateway
{
    Task<Result<WorkCodePublicationReceipt>> PublishAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
        WorkTaskDeliveryIntent intent, string operationKey, string intentDigest, bool recover, CancellationToken cancellationToken);
}
