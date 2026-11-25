using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Work.Storage.Abstractions;

public enum WorkUsageSaveStatus { Saved, Replayed, Conflict }

public interface IWorkUsageRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<WorkUsageEntry?> GetAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, WorkUsageId id, CancellationToken cancellationToken);
    Task<WorkUsagePage> ListAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, int skip, int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkUsageEntry>> ReadProjectAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId,
        IReadOnlyList<WorkPlanId> planIds, int maximumRecords, CancellationToken cancellationToken);
    Task<WorkUsageSaveStatus> CaptureAsync(IReadOnlyList<WorkUsageEntry> originals, CancellationToken cancellationToken);
    Task<WorkUsageSaveStatus> AppendCorrectionAsync(WorkUsageEntry correction, CancellationToken cancellationToken);
}
