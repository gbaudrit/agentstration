using Agentstration.Resources;
namespace Agentstration.Work;

public readonly record struct WorkEpicId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public sealed record WorkEpicDefinition(WorkEpicId Id, string DisplayName, string? Description, IReadOnlyList<WorkPlanId> PlanIds);
public sealed record WorkOrganizationDefinition(IReadOnlyList<WorkEpicDefinition> Epics)
{
    public Result<WorkOrganizationDefinition> Validate()
    {
        if (Epics is not { Count: <= 64 } || Epics.Any(epic => epic is null || epic.Id.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(epic.DisplayName) || epic.DisplayName.Length > 512 || epic.Description?.Length > 2000
            || epic.PlanIds is not { Count: <= WorkProjectDefinition.MaximumMembers } || epic.PlanIds.Any(id => id.Value == Guid.Empty))
            || Epics.Select(epic => epic.Id).Distinct().Count() != Epics.Count)
            return Result<WorkOrganizationDefinition>.Failure("work_organization_invalid");
        var members = Epics.SelectMany(epic => epic.PlanIds).ToArray();
        if (members.Length > WorkProjectDefinition.MaximumMembers || members.Distinct().Count() != members.Length)
            return Result<WorkOrganizationDefinition>.Failure("work_organization_membership_invalid");
        return Result<WorkOrganizationDefinition>.Success(this with
        {
            Epics = Epics.Select(epic => epic with { DisplayName = epic.DisplayName.Trim(), Description = epic.Description?.Trim(),
                PlanIds = epic.PlanIds.OrderBy(id => id.Value).ToArray() }).OrderBy(epic => epic.Id.Value).ToArray()
        });
    }
}

public sealed record WorkOrganizationCommandReceipt(Guid Id, string Digest, long Version, DateTimeOffset AppliedAt);
public sealed record WorkOrganizationSnapshot(WorkspaceId WorkspaceId, Guid PrincipalId, WorkProjectId ProjectId,
    WorkOrganizationDefinition Definition, long Version, long StorageVersion, DateTimeOffset UpdatedAt,
    IReadOnlyList<WorkOrganizationCommandReceipt> Commands);
