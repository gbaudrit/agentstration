using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Work.Storage.Abstractions;

public enum WorkTaskDeliverySaveStatus { Saved, Replayed, Conflict, WorkChanged }

public interface IWorkTaskDeliveryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<WorkTaskDeliveryOperation?> GetAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, WorkTaskDeliveryId id, CancellationToken cancellationToken);
    Task<WorkTaskDeliveryOperation?> GetAttemptAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, Guid attemptId, CancellationToken cancellationToken);
    Task<WorkTaskDeliveryPage> ListAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, int skip, int take, CancellationToken cancellationToken);
    Task<WorkTaskDeliverySaveStatus> AdmitAsync(WorkTaskDeliveryOperation operation, CancellationToken cancellationToken);
    Task<bool> UpdateAsync(WorkTaskDeliveryOperation operation, long expectedVersion, CancellationToken cancellationToken);
}
