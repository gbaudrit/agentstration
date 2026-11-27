using Agentstration.Application.Work;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using static Agentstration.Work.Contracts.WorkProjectContractMapping;

namespace Agentstration.Web;

public static partial class WorkplaceEndpoints
{
    private static async Task<IResult> CreateWorkProjectAsync(string workspaceName, CreateWorkProjectRequest request, WorkProjectService service, CancellationToken token)
    {
        var result = await service.CreateAsync(WorkspaceId(workspaceName), new(request.Id), request.Name, request.Namespace,
            new(request.CommandId, 0, ToProjectDefinition(request.Definition)), token);
        return result.IsSuccess ? Results.Created($"/api/workspaces/{Uri.EscapeDataString(workspaceName)}/projects/{request.Id}", ToProjectResponse(result.Value!)) : WorkProjectProblem(result.ErrorCode!);
    }

    private static async Task<IResult> UpdateWorkProjectAsync(string workspaceName, Guid projectId, UpdateWorkProjectRequest request, WorkProjectService service, CancellationToken token)
    {
        var result = await service.UpdateAsync(WorkspaceId(workspaceName), new(projectId), new(request.CommandId, request.ExpectedVersion, ToProjectDefinition(request.Definition)), token);
        return result.IsSuccess ? Results.Ok(ToProjectResponse(result.Value!)) : WorkProjectProblem(result.ErrorCode!);
    }

    private static async Task<IResult> GetWorkProjectAsync(string workspaceName, Guid projectId, WorkProjectService service, CancellationToken token)
    {
        var result = await service.GetAsync(WorkspaceId(workspaceName), new(projectId), token);
        return result.IsSuccess ? Results.Ok(ToProjectResponse(result.Value!)) : WorkProjectProblem(result.ErrorCode!);
    }

    private static async Task<IResult> ListWorkProjectsAsync(string workspaceName, int? skip, int? take, WorkProjectService service, CancellationToken token)
    {
        var result = await service.ListAsync(WorkspaceId(workspaceName), skip ?? 0, take ?? 25, token);
        return result.IsSuccess ? Results.Ok(new WorkProjectPageResponse(result.Value!.Items.Select(ToProjectResponse).ToArray(), result.Value.HasMore, skip ?? 0, take ?? 25))
            : WorkProjectProblem(result.ErrorCode!);
    }

    private static async Task<IResult> GetWorkProjectContextAsync(string workspaceName, Guid projectId, WorkProjectContextService service, CancellationToken token)
    {
        var result = await service.GetAvailabilityAsync(WorkspaceId(workspaceName), new(projectId), token);
        return result.IsSuccess ? Results.Ok(new WorkProjectContextResponse(projectId, result.Value!.ProjectVersion, result.Value.Bindings) { Connections = result.Value.Connections }) : WorkProjectProblem(result.ErrorCode!);
    }

    private static WorkProjectDefinition ToProjectDefinition(WorkProjectDefinitionRequest? request) => request is null ? null!
        : new(request.DisplayName, request.Description, request.PlanIds?.Select(id => new WorkPlanId(id)).ToArray() ?? [], request.TaskIds?.Select(id => new WorkTaskId(id)).ToArray() ?? [])
        { Bindings = request.Bindings?.ToArray() ?? [], Sharing = request.Sharing, ConnectionRequirements = request.ConnectionRequirements?.ToArray() ?? [], ConnectionAssignments = request.ConnectionAssignments?.ToArray() ?? [] };
    private static IResult WorkProjectProblem(string code) => Results.Problem(title: code, statusCode: code switch
    {
        "work_project_not_found" or "work_project_work_not_found" => StatusCodes.Status404NotFound,
        "work_project_conflict" or "work_project_command_conflict" or "work_project_work_changed" or "work_project_history_limit" or "work_project_binding_stale"
            or "work_project_context_changed" or "work_project_plan_not_associated" or "work_project_contract_changed" or "work_project_connection_changed" or "work_project_connection_stale" => StatusCodes.Status409Conflict,
        "work_project_contract_timeout" or "work_project_contract_unavailable" => StatusCodes.Status503ServiceUnavailable,
        "work_project_binding_unavailable" or "work_project_connection_unavailable" or "work_project_connection_adapter_unavailable" => StatusCodes.Status404NotFound,
        "work_project_binding_denied" => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest
    });
}
