using Agentstration.Application.Work;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Web.Hosting;

public sealed partial class WorkspaceSeedService
{
    internal async Task<WorkspaceSeedHandover> HandoverAsync(WorkspaceSeedSelection selection,
        BootstrapApplicationTarget target, Guid actor, WorkProjectService projects, WorkProjectSharingService sharing,
        Func<WorkspaceSeedHandover, CancellationToken, Task> persist, CancellationToken token)
    {
        var receipt = await GetAsync(selection.Id, target, actor, token);
        if (receipt.Provenance.ArchiveDigest != selection.ArchiveDigest) throw Invalid("seed_integrity_mismatch");
        var workspace = new WorkspaceId(target.WorkspaceId!.Value);
        var project = await projects.FindAsync(workspace, receipt.Manifest.Project.Namespace, receipt.Manifest.Project.Name, token);
        if (!project.IsSuccess) throw Invalid("seed_project_unavailable");
        var handover = new WorkspaceSeedHandover(selection.Id, WorkspaceSeedHandoverStatus.Pending, project.Value!.Id.Value,
            project.Value.Name, project.Value.Namespace, null, [], clock.GetUtcNow());
        try
        {
            await persist(handover, token);
            var pulled = await sharing.PullAsync(workspace, project.Value.Id, null, token);
            if (!pulled.IsSuccess) throw Invalid("seed_first_sync_failed");
            handover = handover with { Revision = pulled.Value!.Revision, UpdatedAt = clock.GetUtcNow() };
            await persist(handover, token);
            foreach (var import in receipt.Manifest.ImportPlans)
            {
                var result = await sharing.ImportPlanAsync(workspace, project.Value.Id, new(import.PlanId), import.ExpectedDigest, token);
                if (!result.IsSuccess) throw Invalid("seed_plan_import_failed");
                handover = handover with { ImportedPlans = [.. handover.ImportedPlans, import.PlanId], UpdatedAt = clock.GetUtcNow() };
                await persist(handover, token);
            }
            handover = handover with { Status = WorkspaceSeedHandoverStatus.Succeeded, UpdatedAt = clock.GetUtcNow() };
            await persist(handover, token);
            return handover;
        }
        catch (Exception exception)
        {
            handover = handover with
            {
                Status = WorkspaceSeedHandoverStatus.Failed,
                UpdatedAt = clock.GetUtcNow(),
                ErrorCode = exception is OperationCanceledException ? "seed_handover_cancelled"
                    : exception.Message is "seed_first_sync_failed" or "seed_plan_import_failed" ? exception.Message : "seed_handover_failed"
            };
            await persist(handover, CancellationToken.None);
            throw;
        }
    }
}
