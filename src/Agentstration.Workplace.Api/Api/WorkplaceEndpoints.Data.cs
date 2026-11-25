using Agentstration.Application.Work;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static partial class WorkplaceEndpoints
{
    private static void MapWorkData(RouteGroupBuilder workspaces)
    {
        var data = workspaces.MapGroup("/projects/{projectId:guid}/data").WithTags("Work data");
        data.MapPost("/preview", async (string workspaceName, Guid projectId, PreviewWorkDataRequest request, WorkDataImportService service, CancellationToken token) =>
        {
            var result = await service.PreviewAsync(WorkspaceId(workspaceName), new(projectId), new(request.Package, request.Revision), token);
            return result.IsSuccess ? Results.Ok(result.Value) : DataProblem(result.ErrorCode!);
        }).WithSummary("Preview versioned YAML Work data without creating work").Produces<WorkDataPreview>(StatusCodes.Status200OK).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1024 * 1024));
        data.MapPost("/imports", async (string workspaceName, Guid projectId, ImportWorkDataRequest request, WorkDataImportService service, CancellationToken token) =>
        {
            var result = await service.ImportAsync(WorkspaceId(workspaceName), new(projectId), new(new(request.Id), new(request.Package, request.Revision), request.ReviewedDigest), token);
            return result.IsSuccess ? DataAccepted(workspaceName, result.Value!) : DataProblem(result.ErrorCode!);
        }).WithSummary("Import reviewed YAML Work data as durable local drafts").Produces<WorkDataImportResponse>(StatusCodes.Status202Accepted).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1024 * 1024));
        data.MapGet("/imports/{operationId:guid}", async (string workspaceName, Guid projectId, Guid operationId, WorkDataImportService service, CancellationToken token) =>
        {
            var result = await service.GetAsync(WorkspaceId(workspaceName), new(projectId), new(operationId), token);
            return result.IsSuccess ? Results.Ok(WorkDataContractMapping.ToResponse(result.Value!)) : DataProblem(result.ErrorCode!);
        }).WithSummary("Get a durable Work data import receipt").Produces<WorkDataImportResponse>(StatusCodes.Status200OK).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        data.MapPost("/imports/{operationId:guid}/resume", async (string workspaceName, Guid projectId, Guid operationId, WorkDataImportService service, CancellationToken token) =>
        {
            var result = await service.ResumeAsync(WorkspaceId(workspaceName), new(projectId), new(operationId), token);
            return result.IsSuccess ? DataAccepted(workspaceName, result.Value!) : DataProblem(result.ErrorCode!);
        }).WithSummary("Resume an admitted Work data import").Produces<WorkDataImportResponse>(StatusCodes.Status202Accepted).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
    }

    private static IResult DataAccepted(string workspaceName, WorkDataImport value) => Results.Accepted(
        $"/api/workspaces/{Uri.EscapeDataString(workspaceName)}/projects/{value.ProjectId}/data/imports/{value.Id}", WorkDataContractMapping.ToResponse(value));

    private static IResult DataProblem(string code) => code.StartsWith("work_project_", StringComparison.Ordinal) ? WorkProjectProblem(code)
        : Results.Problem(title: code, statusCode: code switch
        {
            "work_data_import_not_found" => 404,
            "work_data_review_stale" or "work_data_command_conflict" or "work_data_operation_conflict" => 409,
            "sharing_provider_denied" or "sharing_provider_governance_denied" => 403,
            _ => 400
        });
}
