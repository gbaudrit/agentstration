using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Application.Work;

public interface IWorkProjectDataGateway
{
    Task<Result<WorkDataPackage>> ReadDataAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
        WorkProjectSharingConnection connection, string? revision, CancellationToken cancellationToken);
}
