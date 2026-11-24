using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Work.Storage.Abstractions;

public enum WorkPlanSaveStatus { Saved, Replayed, Conflict, TasksActive, TaskChanged, ProjectChanged }
public sealed record WorkPlanProjectObservation(WorkProjectId ProjectId, long Version);
public sealed record WorkPlanChange(WorkPlanSnapshot Plan, long ExpectedVersion, WorkPlanRevision? NewRevision,
    WorkPlanCommandReceipt Receipt, bool RequireInactiveTasks)
{
    public IReadOnlyList<WorkTaskTreeUpdate> TaskUpdates { get; init; } = [];
    public WorkPlanProjectObservation? Project { get; init; }
}

public interface IWorkPlanRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<WorkPlanSnapshot?> GetAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, WorkPlanId id, CancellationToken cancellationToken);
    Task<WorkPlanRevision?> GetRevisionAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, WorkPlanId id, long revision,
        CancellationToken cancellationToken);
    Task<WorkPlanCommandReceipt?> GetReceiptAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, WorkPlanId id, Guid commandId,
        CancellationToken cancellationToken);
    Task<WorkPlanSaveStatus> TrySaveAsync(WorkPlanChange change, CancellationToken cancellationToken);
    // Trusted scheduler enumeration. User-facing access always requires Workspace and Principal.
    Task<IReadOnlyList<WorkPlanSnapshot>> ListExecutionsAsync(int skip, int take, CancellationToken cancellationToken);
}
