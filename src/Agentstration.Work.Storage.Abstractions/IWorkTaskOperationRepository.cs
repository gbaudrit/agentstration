using Agentstration.Resources;

namespace Agentstration.Work.Storage.Abstractions;

public enum WorkTaskOperationSaveStatus { Saved, Conflict, WorkChanged }
public sealed record WorkTaskOperationAdmission(WorkTaskOperation Operation, long ProjectVersion, WorkProjectPlanObservation? Plan);

public interface IWorkTaskOperationRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<WorkTaskOperation?> GetAsync(WorkspaceId workspaceId, Guid principalId, WorkTaskOperationId id, CancellationToken cancellationToken);
    Task<WorkTaskOperationPage> ListAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, int skip, int take, CancellationToken cancellationToken);
    Task<WorkTaskOperationSaveStatus> TryCreateAsync(WorkTaskOperationAdmission admission, CancellationToken cancellationToken);
    Task<bool> TryUpdateAsync(WorkTaskOperation operation, long expectedVersion, CancellationToken cancellationToken);
}
