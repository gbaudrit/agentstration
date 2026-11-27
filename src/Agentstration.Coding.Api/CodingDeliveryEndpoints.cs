using Agentstration.Coding.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Security;
using Agentstration.Work;

namespace Agentstration.Coding.Api;

public static partial class CodingApiModule
{
    private static void MapCodingDeliveries(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/workspaces/{workspaceName}/projects/{projectId:guid}/coding/deliveries")
            .RequireAuthorization(AgentstrationPolicies.Authenticated).AddEndpointFilter(RequireWorkspaceAsync).WithTags("Coding");
        group.MapPost("/", async (string workspaceName, Guid projectId, BeginCodingDeliveryRequest request, CodingDeliveryService service,
            ICurrentRequestContext current, CancellationToken token) => DeliveryAccepted(workspaceName, projectId,
                await service.BeginAsync(Workspace(current), new(projectId), request, token)))
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(32 * 1024))
            .Produces<WorkTaskDeliveryOperation>(StatusCodes.Status202Accepted).ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Deliver retained Coding results through explicitly selected tracking, forge, Git and sharing connections");
        group.MapPost("/{deliveryId:guid}/resume", async (string workspaceName, Guid projectId, Guid deliveryId, CodingDeliveryService service,
            ICurrentRequestContext current, CancellationToken token) => DeliveryAccepted(workspaceName, projectId,
                await service.ResumeAsync(Workspace(current), new(projectId), new(deliveryId), token)))
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns)
            .Produces<WorkTaskDeliveryOperation>(StatusCodes.Status202Accepted).ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Recover original delivery effects with their captured connections and native intent");
        group.MapGet("/{deliveryId:guid}", async (Guid projectId, Guid deliveryId, CodingDeliveryService service, ICurrentRequestContext current, CancellationToken token) =>
        {
            var result = await service.GetAsync(Workspace(current), new(projectId), new(deliveryId), token);
            return result.IsSuccess ? Results.Ok(result.Value) : DeliveryProblem(result.ErrorCode!);
        }).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        group.MapGet("/", async (Guid projectId, int? skip, int? take, CodingDeliveryService service, ICurrentRequestContext current, CancellationToken token) =>
        {
            var result = await service.ListAsync(Workspace(current), new(projectId), skip ?? 0, take ?? 25, token);
            return result.IsSuccess ? Results.Ok(result.Value) : DeliveryProblem(result.ErrorCode!);
        }).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
    }
    private static IResult DeliveryAccepted(string workspace, Guid project, Result<WorkTaskDeliveryOperation> result) => result.IsSuccess
        ? Results.Accepted($"/api/workspaces/{Uri.EscapeDataString(workspace)}/projects/{project}/coding/deliveries/{result.Value!.Id}", result.Value)
        : DeliveryProblem(result.ErrorCode!);
    private static IResult DeliveryProblem(string code) => Results.Problem(title: code, statusCode: code.EndsWith("not_found", StringComparison.Ordinal) ? 404
        : code.Contains("denied", StringComparison.Ordinal) ? 403 : code.Contains("stale", StringComparison.Ordinal) || code.Contains("conflict", StringComparison.Ordinal)
            || code.Contains("changed", StringComparison.Ordinal) || code is "coding_delivery_execution_not_final" or "coding_delivery_attempt_already_retained" ? 409 : 400);
}
