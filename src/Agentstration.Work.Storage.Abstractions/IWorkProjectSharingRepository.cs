using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Work.Storage.Abstractions;

public enum WorkSharingSaveStatus { Saved, Conflict, WorkChanged }

public interface IWorkProjectSharingRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<WorkProjectSharingState?> GetStateAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, CancellationToken cancellationToken);
    Task<bool> SaveStateAsync(WorkProjectSharingState state, long expectedVersion, CancellationToken cancellationToken);
    Task<WorkSharingOperation?> GetOperationAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId,
        WorkSharingOperationId operationId, CancellationToken cancellationToken);
    Task<WorkSharingSaveStatus> AdmitAsync(WorkSharingOperation operation, CancellationToken cancellationToken);
    Task<bool> UpdateOperationAsync(WorkSharingOperation operation, long expectedVersion, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkSharingOperation>> ListOperationsAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId,
        int skip, int take, CancellationToken cancellationToken);
    Task<WorkProjectId?> FindPlanProjectAsync(WorkspaceId workspaceId, Guid principalId, WorkPlanId planId, CancellationToken cancellationToken);
    Task<WorkTaskOperationId?> GetPlanOperationAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, WorkPlanId planId, CancellationToken cancellationToken);
    Task<bool> SetPlanOperationAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, WorkPlanId planId,
        WorkTaskOperationId operationId, CancellationToken cancellationToken);
    Task<bool> MapPlanAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, WorkPlanId planId, CancellationToken cancellationToken);
}
