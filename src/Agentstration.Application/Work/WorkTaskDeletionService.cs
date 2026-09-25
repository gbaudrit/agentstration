using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed class WorkTaskDeletionService(IWorkItemRepository repository, IArtifactStore artifacts, IWorkplaceContext context)
{
    public async Task DeleteAsync(
        WorkspaceId workspaceId,
        WorkTaskId taskId,
        string expectedETag,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag);
        var deleted = await repository.DeleteTaskAsync(workspaceId, context.PrincipalId, taskId, expectedETag, cancellationToken);
        foreach (var artifact in deleted.Artifacts)
            await artifacts.DeleteAsync(workspaceId, artifact, cancellationToken);
    }
}
