using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Application.Work;

public sealed partial class WorkProjectSharingService
{
    /// <summary>Applies only portable declarations. Local assignments and admitted execution remain authoritative.</summary>
    public async Task<Result<WorkProjectSnapshot>> ImportConnectionsAsync(WorkspaceId workspaceId, WorkProjectId projectId,
        long expectedProjectVersion, string expectedDigest, CancellationToken token)
    {
        var project = await projects.GetAsync(workspaceId, projectId, token);
        if (!project.IsSuccess) return project;
        if (expectedProjectVersion < 1 || !WorkSharingFormat.Digest(expectedDigest))
            return Result<WorkProjectSnapshot>.Failure("sharing_command_invalid");
        var commandId = new Guid(Convert.FromHexString(WorkPlanDefinition.Digest(new
        { ImportConnections = projectId, expectedProjectVersion, expectedDigest })).AsSpan(0, 16));
        // A receipt acknowledges this exact application; retries must never undo a later local edit.
        if (project.Value!.Commands.Any(receipt => receipt.CommandId == commandId)) return project;
        if (project.Value.Version != expectedProjectVersion) return Result<WorkProjectSnapshot>.Failure("work_project_conflict");
        var pulled = await PullAsync(workspaceId, projectId, null, token);
        if (!pulled.IsSuccess) return Result<WorkProjectSnapshot>.Failure(pulled.ErrorCode!);
        if (pulled.Value!.ProjectVersion != project.Value.Version || pulled.Value.Connection != project.Value.Definition.Sharing)
            return Result<WorkProjectSnapshot>.Failure("sharing_connection_changed");
        var record = pulled.Value.Records.SingleOrDefault(value => value.Key == WorkSharingProjectMetadata.Key);
        if (record is null || record.Digest != expectedDigest) return Result<WorkProjectSnapshot>.Failure("sharing_record_conflict");
        var requirements = record.Content.Deserialize<WorkSharingProjectMetadata>(Json)!.ConnectionRequirements;
        return await projects.UpdateAsync(workspaceId, projectId, new(commandId, expectedProjectVersion,
            project.Value.Definition with { ConnectionRequirements = requirements }), token);
    }

    private static string? ValidateConnections(IEnumerable<WorkSharingRecord> records)
    {
        var values = records.ToArray();
        var requirements = values.SingleOrDefault(value => value.Key == WorkSharingProjectMetadata.Key)?
            .Content.Deserialize<WorkSharingProjectMetadata>(Json)?.ConnectionRequirements ?? [];
        var plans = values.Where(value => value.Key.Domain == WorkSharingDomain.Plan).Select(value => value.Content.Deserialize<WorkSharingPlan>(Json)!).ToArray();
        var names = plans.SelectMany(value => value.Definition.TaskConnections).SelectMany(value => value.Names)
            .Concat(plans.Where(value => value.Issue is not null).Select(value => value.Issue!.ConnectionName)).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Any(name => !requirements.Any(need => need.Name == name))) return "sharing_connection_requirement_missing";
        if (names.Any(name => requirements.Single(need => need.Name == name).Target is null)) return "sharing_connection_target_required";
        return plans.Where(value => value.Issue is not null).Any(value =>
        {
            var requirement = requirements.Single(need => need.Name == value.Issue!.ConnectionName);
            return requirement.Contract != WorkProjectConnectionRules.Tracking || requirement.Target != value.Issue!.Tracking;
        }) ? "sharing_connection_requirement_mismatch" : null;
    }

    private static string? ValidateLocalConnections(WorkProjectSnapshot project, WorkSharingSnapshot snapshot,
        WorkSharingPlan plan, WorkTaskId taskId, WorkPlanSnapshot local)
    {
        var names = plan.Definition.TaskConnections.SingleOrDefault(value => value.TaskId == taskId)?.Names ?? [];
        var selected = local.Execution?.Targets.SingleOrDefault(value => value.TaskId == taskId)?.Connections ?? [];
        if (!selected.Order(StringComparer.Ordinal).SequenceEqual(names)) return "sharing_task_connections_mismatch";
        var requirements = snapshot.Records.SingleOrDefault(value => value.Key == WorkSharingProjectMetadata.Key)?
            .Content.Deserialize<WorkSharingProjectMetadata>(Json)?.ConnectionRequirements ?? [];
        foreach (var name in names)
        {
            var shared = requirements.Single(value => value.Name == name);
            var own = project.Definition.ConnectionRequirements.SingleOrDefault(value => value.Name == name);
            if (own is null || WorkPlanDefinition.Digest(shared) != WorkPlanDefinition.Digest(own))
                return "sharing_connection_requirement_mismatch";
        }
        return null;
    }
}
