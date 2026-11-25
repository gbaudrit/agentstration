using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Application.Work;

public sealed record WorkSharingProviderCapabilities(string Version, bool Read, bool Write, bool ExactRevisions,
    bool CompareAndSwap, bool History, bool IdempotentOperations, bool Recovery);
public enum WorkSharingProviderStatus { Missing, Succeeded, Conflict, Rejected, Uncertain }
public sealed record WorkSharingProviderResult(WorkSharingProviderStatus Status, string OperationKey, string IntentDigest,
    string? Revision = null, string? ErrorCode = null);

public interface IWorkProjectSharingGateway
{
    Task<Result<WorkSharingProviderCapabilities>> DiscoverAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectContextBinding binding, WorkProjectSharingConnection connection, CancellationToken cancellationToken);
    Task<Result<WorkSharingSnapshot>> ReadAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
        WorkProjectSharingConnection connection, string? revision, CancellationToken cancellationToken);
    Task<WorkSharingProviderResult> PublishAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
        WorkProjectSharingConnection connection, WorkSharingTransportAttempt attempt, bool recover, CancellationToken cancellationToken);
}
