using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Work.Storage.Abstractions;

public interface IWorkDataRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<WorkDataImport?> GetImportAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId,
        WorkDataImportId id, CancellationToken cancellationToken);
    Task<bool> SaveImportAsync(WorkDataImport operation, long expectedVersion, CancellationToken cancellationToken);
}
