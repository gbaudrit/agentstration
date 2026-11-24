using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Work.Storage.Abstractions;

public enum WorkProjectSaveStatus { Saved, Replayed, Conflict, WorkChanged }
public sealed record WorkProjectPlanObservation(WorkPlanId PlanId, long Version);
public sealed record WorkProjectChange(WorkProjectSnapshot Project, long ExpectedVersion,
    WorkProjectCommandReceipt Receipt, IReadOnlyList<WorkProjectPlanObservation> Plans, IReadOnlyList<WorkPlanTaskObservation> Tasks);

public interface IWorkProjectRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<WorkProjectSnapshot?> GetAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, WorkProjectId id, CancellationToken cancellationToken);
    Task<WorkProjectSnapshot?> FindAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, ResourceNamespace resourceNamespace, string name, CancellationToken cancellationToken);
    Task<WorkProjectPage> ListAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, int skip, int take, CancellationToken cancellationToken);
    Task<WorkProjectSaveStatus> TrySaveAsync(WorkProjectChange change, CancellationToken cancellationToken);
}
