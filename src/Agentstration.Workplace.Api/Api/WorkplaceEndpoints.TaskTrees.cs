using Agentstration.Application.Work;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web;

public static partial class WorkplaceEndpoints
{
    private static async Task<IResult> CreateTaskTreeAsync(string workspaceName, CreateWorkTaskTreeRequest request,
        WorkTaskTreeService service, CancellationToken token)
    {
        var definition = new WorkTaskTreeDefinition(new(request.RootTaskId), request.Tasks?.Select(task => task is null
            ? null!
            : new WorkTaskDefinition(new(task.Id), task.Kind, task.Title, task.Instruction,
                task.ParentTaskId is { } parent ? new WorkTaskId(parent) : null, task.Description)).ToArray() ?? []);
        var result = await service.CreateAsync(WorkspaceId(workspaceName), definition, token);
        return result.IsSuccess
            ? Results.Created($"/api/workspaces/{Uri.EscapeDataString(workspaceName)}/tasks/{result.Value!.RootTaskId}/tree",
                ToTreeResponse(workspaceName, result.Value))
            : TaskTreeProblem(result.ErrorCode!);
    }

    private static async Task<IResult> GetTaskTreeAsync(string workspaceName, Guid taskId,
        WorkTaskTreeService service, CancellationToken token)
    {
        var result = await service.GetAsync(WorkspaceId(workspaceName), new(taskId), token);
        return result.IsSuccess ? Results.Ok(ToTreeResponse(workspaceName, result.Value!)) : TaskTreeProblem(result.ErrorCode!);
    }

    private static IResult TaskTreeProblem(string code) => Results.Problem(title: code, statusCode: code switch
    {
        "work_task_not_found" => StatusCodes.Status404NotFound,
        "task_tree_conflict" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    });

    private static WorkTaskTreeResponse ToTreeResponse(string workspaceName, WorkTaskTree tree) => new(
        WorkspaceId(workspaceName).Value, tree.RootTaskId.Value, tree.Tasks.Select(task => new WorkTaskTreeNodeResponse(
            task.Definition.Id.Value, task.Definition.Kind, task.Definition.ParentTaskId?.Value, task.Definition.Title,
            task.Definition.Instruction, task.Definition.Description, task.Status, task.Version, task.CreatedAt, task.UpdatedAt)).ToArray());
}
