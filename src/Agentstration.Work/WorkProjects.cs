using Agentstration.Resources;

namespace Agentstration.Work;

public readonly record struct WorkProjectId(Guid Value)
{
    public static WorkProjectId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public sealed record WorkProjectDefinition(string DisplayName, string? Description,
    IReadOnlyList<WorkPlanId> PlanIds, IReadOnlyList<WorkTaskId> TaskIds)
{
    public IReadOnlyList<WorkProjectContextBinding> Bindings { get; init; } = [];
    public const int MaximumMembers = 100;
    public const int MaximumCommands = 256;

    public Result<WorkProjectDefinition> Validate()
    {
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 512 || Description?.Length > 2_000
            || PlanIds is null || TaskIds is null || PlanIds.Count > MaximumMembers || TaskIds.Count > MaximumMembers
            || PlanIds.Any(id => id.Value == Guid.Empty) || TaskIds.Any(id => id.Value == Guid.Empty)
            || PlanIds.Distinct().Count() != PlanIds.Count || TaskIds.Distinct().Count() != TaskIds.Count
            || !WorkProjectContextRules.Valid(Bindings))
            return Result<WorkProjectDefinition>.Failure("work_project_definition_invalid");
        return Result<WorkProjectDefinition>.Success(this with { DisplayName = DisplayName.Trim(), Description = Description?.Trim(),
            PlanIds = PlanIds.OrderBy(id => id.Value).ToArray(), TaskIds = TaskIds.OrderBy(id => id.Value).ToArray(),
            Bindings = Bindings.OrderBy(binding => binding.Name, StringComparer.Ordinal).ToArray() });
    }

    public static bool ValidName(string? name) => name is { Length: >= 1 and <= 128 }
        && char.IsAsciiLetterOrDigit(name[0]) && char.IsAsciiLetterOrDigit(name[^1])
        && name.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '.');
}

public sealed record WorkProjectCommandReceipt(Guid CommandId, string IntentDigest, long Version, DateTimeOffset AppliedAt);
public sealed record WorkProjectSnapshot(WorkProjectId Id, WorkspaceId WorkspaceId, Guid OwnerPrincipalId, string Name,
    ResourceNamespace Namespace, WorkProjectDefinition Definition, long Version, string CreationDigest,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<WorkProjectCommandReceipt> Commands);
public sealed record WorkProjectPage(IReadOnlyList<WorkProjectSnapshot> Items, bool HasMore);
