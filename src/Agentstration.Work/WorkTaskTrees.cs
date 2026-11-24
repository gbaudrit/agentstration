using System.Text.Json.Serialization;

namespace Agentstration.Work;

public enum WorkTaskKind { Atomic, Composite }

public sealed record Result<T>(T? Value, string? ErrorCode) where T : class
{
    public bool IsSuccess => ErrorCode is null;
    public static Result<T> Success(T value) => new(value, null);
    public static Result<T> Failure(string code) => new(null, code);
}

public sealed record WorkTaskDefinition(
    WorkTaskId Id,
    WorkTaskKind Kind,
    string Title,
    string Instruction,
    WorkTaskId? ParentTaskId = null,
    string? Description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsOptional = false);

public sealed record WorkTaskHierarchy(WorkTaskId TreeId, WorkTaskKind Kind, WorkTaskId? ParentTaskId, string CreationDigest,
    bool IsOptional = false);

public sealed record WorkTaskTreeDefinition(WorkTaskId RootTaskId, IReadOnlyList<WorkTaskDefinition> Tasks)
{
    public const int MaximumTasks = 100;
    public const int MaximumDepth = 8;
    public const int MaximumTextLength = 1_000_000;

    public Result<WorkTaskTreeDefinition> Validate()
    {
        if (RootTaskId.Value == Guid.Empty || Tasks is null || Tasks.Count is < 1 or > MaximumTasks)
            return Result<WorkTaskTreeDefinition>.Failure("task_tree_size_invalid");

        var nodes = new Dictionary<WorkTaskId, WorkTaskDefinition>();
        long textLength = 0;
        foreach (var task in Tasks)
        {
            if (task is null || task.Id.Value == Guid.Empty || !Enum.IsDefined(task.Kind)
                || string.IsNullOrWhiteSpace(task.Title) || task.Title.Length > 512
                || string.IsNullOrWhiteSpace(task.Instruction) || task.Instruction.Length > 100_000
                || task.Description?.Length > 2_000)
                return Result<WorkTaskTreeDefinition>.Failure("task_definition_invalid");
            if (!nodes.TryAdd(task.Id, task with
            {
                Title = task.Title.Trim(),
                Instruction = task.Instruction.Trim(),
                Description = string.IsNullOrWhiteSpace(task.Description) ? null : task.Description.Trim()
            }))
                return Result<WorkTaskTreeDefinition>.Failure("task_id_duplicate");
            textLength += task.Title.Length + task.Instruction.Length + (task.Description?.Length ?? 0);
        }
        if (textLength > MaximumTextLength)
            return Result<WorkTaskTreeDefinition>.Failure("task_tree_text_too_large");
        if (!nodes.TryGetValue(RootTaskId, out var root) || root.ParentTaskId is not null)
            return Result<WorkTaskTreeDefinition>.Failure("task_tree_root_invalid");

        foreach (var task in nodes.Values)
        {
            if (task.Id != RootTaskId && task.ParentTaskId is null)
                return Result<WorkTaskTreeDefinition>.Failure("task_tree_root_invalid");
            var visited = new HashSet<WorkTaskId> { task.Id };
            var current = task;
            var depth = 0;
            while (current.ParentTaskId is { } parentId)
            {
                if (!visited.Add(parentId))
                    return Result<WorkTaskTreeDefinition>.Failure("task_tree_cycle");
                if (!nodes.TryGetValue(parentId, out var parent))
                    return Result<WorkTaskTreeDefinition>.Failure("task_parent_not_found");
                if (parent.Kind != WorkTaskKind.Composite)
                    return Result<WorkTaskTreeDefinition>.Failure("atomic_task_cannot_have_children");
                if (++depth > MaximumDepth)
                    return Result<WorkTaskTreeDefinition>.Failure("task_tree_depth_exceeded");
                current = parent;
            }
        }
        return Result<WorkTaskTreeDefinition>.Success(this with { Tasks = nodes.Values.OrderBy(task => task.Id.Value).ToArray() });
    }
}

public sealed record WorkTaskTree(WorkTaskId RootTaskId, IReadOnlyList<WorkTaskTreeNode> Tasks);
public sealed record WorkTaskTreeNode(
    WorkTaskDefinition Definition, WorkItemStatus Status, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    WorkTaskAcceptance? Acceptance = null);
