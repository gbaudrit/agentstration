using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Application.Work;

public sealed partial class WorkTaskTreeService(
    IWorkItemRepository repository, IWorkplaceContext context, TimeProvider timeProvider, ILogger<WorkTaskTreeService> logger)
{
    public async Task<Result<WorkTaskTree>> CreateAsync(WorkspaceId workspaceId, WorkTaskTreeDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (workspaceId.Value == Guid.Empty || workspaceId != context.WorkspaceId || context.PrincipalId == Guid.Empty)
            return Result<WorkTaskTree>.Failure("work_task_not_found");
        var validated = definition.Validate();
        if (!validated.IsSuccess) return Result<WorkTaskTree>.Failure(validated.ErrorCode!);
        definition = validated.Value!;
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(definition)));
        var now = timeProvider.GetUtcNow();
        var items = definition.Tasks.Select(task => WorkItem.CreateTask(task, workspaceId, context.PrincipalId,
            definition.RootTaskId, digest, now)).ToArray();
        using var activity = WorkItemService.ActivitySource.StartActivity("work.task-tree.create");
        activity?.SetTag("workspace.id", workspaceId.ToString());
        activity?.SetTag("work.task.id", definition.RootTaskId.ToString());

        var created = await repository.TryCreateTaskTreeAsync(items, cancellationToken);
        if (!created)
        {
            var root = await repository.GetAsync(workspaceId, context.PrincipalId, definition.RootTaskId.ToWorkItemId(), cancellationToken);
            if (root?.Value.TaskHierarchy?.CreationDigest != digest)
                return Result<WorkTaskTree>.Failure("task_tree_conflict");
        }
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Resolved task tree {TaskId} in Workspace {WorkspaceId} with {TaskCount} tasks; created {Created}",
                definition.RootTaskId.Value, workspaceId.Value, items.Length, created);
        return await GetAsync(workspaceId, definition.RootTaskId, cancellationToken);
    }

    public async Task<Result<WorkTaskTree>> GetAsync(WorkspaceId workspaceId, WorkTaskId taskId,
        CancellationToken cancellationToken)
    {
        if (workspaceId.Value == Guid.Empty || workspaceId != context.WorkspaceId || context.PrincipalId == Guid.Empty)
            return Result<WorkTaskTree>.Failure("work_task_not_found");
        var anchor = await repository.GetAsync(workspaceId, context.PrincipalId, taskId.ToWorkItemId(), cancellationToken);
        if (anchor is null || !WorkplaceTaskIdentity.IsTask(anchor.Value)
            || anchor.Value.Metadata.ContainsKey(WorkplaceTaskIdentity.AnchorTaskMetadata))
            return Result<WorkTaskTree>.Failure("work_task_not_found");
        if (anchor.Value.TaskHierarchy is not { } hierarchy)
        {
            var node = ToNode(anchor.Value);
            var continuations = await repository.ListLatestContinuationsAsync(workspaceId, context.PrincipalId, [taskId], cancellationToken);
            if (continuations.TryGetValue(taskId, out var latest))
                node = node with { Status = latest.Value.Status, Version = latest.Value.Version, UpdatedAt = latest.Value.UpdatedAt };
            return Result<WorkTaskTree>.Success(new(taskId, [node]));
        }

        var family = await repository.ListTaskTreeAsync(workspaceId, context.PrincipalId, hierarchy.TreeId, cancellationToken);
        if (family.Count > WorkTaskTreeDefinition.MaximumTasks)
            return Result<WorkTaskTree>.Failure("task_tree_size_invalid");
        var nodes = family.Select(item => ToNode(item.Value)).ToArray();
        var selected = new List<WorkTaskTreeNode>();
        var frontier = new HashSet<WorkTaskId> { taskId };
        for (var depth = 0; frontier.Count > 0; depth++)
        {
            if (depth > WorkTaskTreeDefinition.MaximumDepth)
                return Result<WorkTaskTree>.Failure("task_tree_depth_exceeded");
            selected.AddRange(nodes.Where(node => frontier.Contains(node.Definition.Id)));
            frontier = nodes.Where(node => node.Definition.ParentTaskId is { } parent && frontier.Contains(parent))
                .Select(node => node.Definition.Id).ToHashSet();
        }
        return Result<WorkTaskTree>.Success(new(taskId, selected));
    }

    private static WorkTaskTreeNode ToNode(WorkItem item) => new(
        new(WorkTaskId.FromWorkItem(item.Id), item.TaskHierarchy?.Kind ?? WorkTaskKind.Atomic,
            item.Title ?? item.Instruction, item.Instruction, item.TaskHierarchy?.ParentTaskId, item.Description,
            item.TaskHierarchy?.IsOptional ?? false),
        item.Status, item.Version, item.CreatedAt, item.UpdatedAt, item.TaskAcceptance);
}
