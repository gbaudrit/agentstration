using Agentstration.Application.Work;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web;

public static partial class WorkplaceEndpoints
{
    private static async Task<IResult> DiscoverTaskProviderAsync(string workspaceName, Guid projectId, string bindingName,
        ExternalTaskOperationKind operation, WorkTaskProviderService service, CancellationToken token)
    {
        var result = await service.DiscoverAsync(WorkspaceId(workspaceName), new(projectId), bindingName, operation, token);
        return result.IsSuccess ? Results.Ok(result.Value) : TaskProviderProblem(result.ErrorCode!);
    }

    private static async Task<IResult> BeginTaskProviderOperationAsync(string workspaceName, Guid projectId,
        BeginWorkTaskOperationRequest request, WorkTaskProviderService service, CancellationToken token)
    {
        var result = await service.BeginAsync(WorkspaceId(workspaceName), new(projectId), new(new(request.Id), request.ExpectedProjectVersion,
            request.BindingName, request.Input, request.PlanId is { } planId ? new(planId) : null, request.PlanRevision, request.PlanDigest), token);
        return OperationAccepted(workspaceName, projectId, result);
    }

    private static async Task<IResult> ResumeTaskProviderOperationAsync(string workspaceName, Guid projectId, Guid operationId,
        WorkTaskProviderService service, CancellationToken token) =>
        OperationAccepted(workspaceName, projectId, await service.ResumeAsync(WorkspaceId(workspaceName), new(projectId), new(operationId), token));

    private static async Task<IResult> GetTaskProviderOperationAsync(string workspaceName, Guid projectId, Guid operationId,
        WorkTaskProviderService service, CancellationToken token)
    {
        var result = await service.GetAsync(WorkspaceId(workspaceName), new(projectId), new(operationId), token);
        return result.IsSuccess ? Results.Ok(ToOperationResponse(result.Value!)) : TaskProviderProblem(result.ErrorCode!);
    }

    private static async Task<IResult> ListTaskProviderOperationsAsync(string workspaceName, Guid projectId, int? skip, int? take,
        WorkTaskProviderService service, CancellationToken token)
    {
        var result = await service.ListAsync(WorkspaceId(workspaceName), new(projectId), skip ?? 0, take ?? 25, token);
        return result.IsSuccess ? Results.Ok(new WorkTaskOperationPageResponse(result.Value!.Items.Select(ToOperationResponse).ToArray(),
            result.Value.HasMore, skip ?? 0, take ?? 25)) : TaskProviderProblem(result.ErrorCode!);
    }

    private static IResult OperationAccepted(string workspaceName, Guid projectId, Result<WorkTaskOperation> result)
    {
        if (!result.IsSuccess) return TaskProviderProblem(result.ErrorCode!);
        var operation = result.Value!;
        return operation.Status == WorkTaskOperationStatus.Rejected
            ? Results.Problem(title: operation.ErrorCode ?? "task_provider_operation_rejected", statusCode: 409,
                extensions: new Dictionary<string, object?> { ["operationId"] = operation.Id.Value })
            : Results.Accepted($"/api/workspaces/{Uri.EscapeDataString(workspaceName)}/projects/{projectId}/task-operations/{operation.Id}", ToOperationResponse(operation));
    }

    private static WorkTaskOperationResponse ToOperationResponse(WorkTaskOperation value) => new(value.Id.Value, value.ProjectId.Value,
        value.ProjectVersion, value.PlanId?.Value, value.PlanRevision, value.PlanDigest, value.Input, value.IntentDigest,
        value.OperationKey, value.Route, value.Status, value.Version, value.CreatedAt, value.UpdatedAt, value.Task, value.ErrorCode);

    private static IResult TaskProviderProblem(string code) => code.StartsWith("work_project_", StringComparison.Ordinal) ? WorkProjectProblem(code)
        : code.StartsWith("work_plan_", StringComparison.Ordinal) ? WorkPlanProblem(code)
        : Results.Problem(title: code, statusCode: code switch
        {
            "task_provider_operation_not_found" => 404,
            "task_provider_denied" => 403,
            "task_provider_operation_conflict" or "task_provider_work_changed" or "task_provider_binding_changed" or "task_provider_route_changed"
                or "task_provider_approval_required" or "task_provider_unavailable" or "task_provider_tool_unavailable" => 409,
            _ => 400
        });
}
