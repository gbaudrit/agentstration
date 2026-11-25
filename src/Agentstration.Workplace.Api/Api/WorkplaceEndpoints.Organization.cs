using Agentstration.Application.Work;
using Agentstration.Work.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static partial class WorkplaceEndpoints
{
    private static void MapWorkOrganization(RouteGroupBuilder workspaces)
    {
        var organization = workspaces.MapGroup("/projects/{projectId:guid}/organization").WithTags("Work organization");
        organization.MapGet("", async (string workspaceName, Guid projectId, WorkProjectOrganizationService service, CancellationToken token) =>
        {
            var result = await service.GetAsync(WorkspaceId(workspaceName), new(projectId), token);
            return result.IsSuccess ? Results.Ok(WorkOrganizationContractMapping.ToResponse(result.Value!)) : OrganizationProblem(result.ErrorCode!);
        }).WithSummary("Get independent logical epic organization").Produces<WorkOrganizationResponse>(StatusCodes.Status200OK).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        organization.MapPut("", async (string workspaceName, Guid projectId, UpdateWorkOrganizationRequest request, WorkProjectOrganizationService service, CancellationToken token) =>
        {
            var result = await service.UpdateAsync(WorkspaceId(workspaceName), new(projectId), new(request.Id, request.ExpectedVersion, request.Definition), token);
            return result.IsSuccess ? Results.Ok(WorkOrganizationContractMapping.ToResponse(result.Value!)) : OrganizationProblem(result.ErrorCode!);
        }).WithSummary("Update logical epic organization without changing plan execution").Produces<WorkOrganizationResponse>(StatusCodes.Status200OK).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(256 * 1024));
        organization.MapPost("/publication", async (string workspaceName, Guid projectId, PublishWorkOrganizationRequest request, WorkProjectOrganizationService service, CancellationToken token) =>
            SharingAccepted(workspaceName, projectId, await service.PublishAsync(WorkspaceId(workspaceName), new(projectId),
                new(request.Id), request.ExpectedOrganizationVersion, request.ExpectedRecordVersion, token)))
            .WithSummary("Publish logical epic organization separately from plans").Produces<WorkSharingOperationResponse>(StatusCodes.Status202Accepted).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        organization.MapPost("/import", async (string workspaceName, Guid projectId, ImportWorkOrganizationRequest request, WorkProjectOrganizationService service, CancellationToken token) =>
        {
            var result = await service.ImportSharedAsync(WorkspaceId(workspaceName), new(projectId), request.Id, request.ExpectedVersion, request.ExpectedDigest, token);
            return result.IsSuccess ? Results.Ok(WorkOrganizationContractMapping.ToResponse(result.Value!)) : OrganizationProblem(result.ErrorCode!);
        }).WithSummary("Import reviewed shared logical epic organization").Produces<WorkOrganizationResponse>(StatusCodes.Status200OK).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
    }

    private static IResult OrganizationProblem(string code) => code.StartsWith("work_project_", StringComparison.Ordinal) ? WorkProjectProblem(code)
        : Results.Problem(title: code, statusCode: code switch
        {
            "work_organization_conflict" or "work_organization_command_conflict" or "work_organization_local_conflict" or "sharing_record_conflict" => 409,
            "sharing_provider_denied" or "sharing_provider_governance_denied" => 403,
            _ => 400
        });
}
