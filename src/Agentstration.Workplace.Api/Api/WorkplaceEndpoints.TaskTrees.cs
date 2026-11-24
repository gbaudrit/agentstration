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
                task.ParentTaskId is { } parent ? new WorkTaskId(parent) : null, task.Description, task.IsOptional)).ToArray() ?? []);
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

    private static async Task<IResult> RecordTaskOutcomeAsync(string workspaceName, Guid taskId,
        RecordWorkTaskOutcomeRequest request, WorkTaskTreeService service, CancellationToken token)
    {
        var result = await service.RecordOutcomeAsync(WorkspaceId(workspaceName), new(taskId),
            new(request.EventId, request.ExpectedVersion, request.Outcome, request.Evidence), token);
        return result.IsSuccess ? Results.Ok(ToTreeResponse(workspaceName, result.Value!)) : TaskTreeProblem(result.ErrorCode!);
    }

    private static IResult TaskTreeProblem(string code) => Results.Problem(title: code, statusCode: code switch
    {
        "work_task_not_found" => StatusCodes.Status404NotFound,
        "task_tree_conflict" => StatusCodes.Status409Conflict,
        "task_outcome_conflict" or "task_outcome_derived" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    });

    private static WorkTaskTreeResponse ToTreeResponse(string workspaceName, WorkTaskTree tree) => new(
        WorkspaceId(workspaceName).Value, tree.RootTaskId.Value, tree.Tasks.Select(task => new WorkTaskTreeNodeResponse(
            task.Definition.Id.Value, task.Definition.Kind, task.Definition.ParentTaskId?.Value, task.Definition.Title,
            task.Definition.Instruction, task.Definition.Description, task.Status, task.Version, task.CreatedAt, task.UpdatedAt,
            task.Definition.IsOptional, ToAcceptanceResponse(task.Acceptance))).ToArray());

    private static WorkTaskAcceptanceResponse? ToAcceptanceResponse(WorkTaskAcceptance? acceptance) => acceptance is null ? null : new(
        acceptance.Outcome, acceptance.Evidence is { } evidence
            ? new(evidence.EventId, evidence.Outcome, evidence.Evidence, evidence.RecordedAt) : null,
        acceptance.RequiredChildren.Select(child => new WorkTaskChildOutcomeResponse(child.TaskId.Value, child.Outcome, child.Version)).ToArray(),
        acceptance.UpdatedAt);
}
