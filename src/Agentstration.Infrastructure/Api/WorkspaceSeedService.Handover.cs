using System.Text.Json;
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
            var publicationId = new WorkSharingOperationId(new Guid(Convert.FromHexString(WorkPlanDefinition.Digest(new
            { Seed = selection.Id, Project = project.Value.Id, Phase = "connection-requirements" })).AsSpan(0, 16)));
            if (project.Value.Definition.ConnectionRequirements.Count > 0)
            {
                var existing = await sharing.GetOperationAsync(workspace, project.Value.Id, publicationId, token);
                if (existing.IsSuccess)
                {
                    handover = handover with { RequirementsPublicationId = publicationId.Value, UpdatedAt = clock.GetUtcNow() };
                    await persist(handover, token);
                    var recovered = await sharing.ResumeAsync(workspace, project.Value.Id, publicationId, token);
                    if (!recovered.IsSuccess || recovered.Value!.Status != WorkSharingOperationStatus.Succeeded) throw Invalid("seed_connection_publication_failed");
                }
                else if (existing.ErrorCode != "sharing_operation_not_found") throw Invalid("seed_connection_publication_failed");
            }
            var pulled = await sharing.PullAsync(workspace, project.Value.Id, null, token);
            if (!pulled.IsSuccess) throw Invalid("seed_first_sync_failed");
            if (project.Value.Definition.ConnectionRequirements.Count > 0)
            {
                var metadata = pulled.Value!.Records.SingleOrDefault(record => record.Key == WorkSharingProjectMetadata.Key);
                if (metadata is null)
                {
                    var content = new WorkSharingProjectMetadata(receipt.Manifest.Project.Name, null,
                        [project.Value.Definition.Sharing!.ParticipantId])
                    { ConnectionRequirements = project.Value.Definition.ConnectionRequirements };
                    handover = handover with { RequirementsPublicationId = publicationId.Value, UpdatedAt = clock.GetUtcNow() };
                    await persist(handover, token);
                    var published = await sharing.PublishAsync(workspace, project.Value.Id, new(publicationId, project.Value.Version,
                        [new(WorkSharingProjectMetadata.Key, 0, JsonSerializer.SerializeToElement(content, Json))]), token);
                    if (!published.IsSuccess || published.Value!.Status != WorkSharingOperationStatus.Succeeded) throw Invalid("seed_connection_publication_failed");
                    pulled = await sharing.PullAsync(workspace, project.Value.Id, null, token);
                    if (!pulled.IsSuccess) throw Invalid("seed_first_sync_failed");
                    metadata = pulled.Value!.Records.SingleOrDefault(record => record.Key == WorkSharingProjectMetadata.Key);
                }
                if (metadata is null || WorkPlanDefinition.Digest(metadata.Content.Deserialize<WorkSharingProjectMetadata>(Json)!.ConnectionRequirements)
                    != WorkPlanDefinition.Digest(project.Value.Definition.ConnectionRequirements)) throw Invalid("seed_connection_requirements_mismatch");
                handover = handover with { RequirementsDigest = metadata.Digest, UpdatedAt = clock.GetUtcNow() };
            }
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
                    : exception.Message is "seed_first_sync_failed" or "seed_plan_import_failed" or "seed_connection_publication_failed" or "seed_connection_requirements_mismatch" ? exception.Message : "seed_handover_failed"
            };
            await persist(handover, CancellationToken.None);
            throw;
        }
    }
}
