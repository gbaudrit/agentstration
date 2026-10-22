using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Runtime.Api.Api;

internal static class RuntimeObservabilityEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var workers = endpoints.MapGroup("/api/runtime/workers")
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        workers.MapGet("/", async (IRuntimeObservabilityQueryService service, CancellationToken token) =>
                Results.Ok(await service.ListWorkersAsync(token)))
            .Produces<RuntimeWorkerSummaryResponse[]>()
            .WithSummary("List enrolled Runtime Workers and their live presence");
        workers.MapGet("/{workerId:guid}", async (Guid workerId, IRuntimeObservabilityQueryService service,
                CancellationToken token) =>
            {
                var worker = await service.GetWorkerAsync(workerId, token);
                return worker is null ? Results.NotFound() : Results.Ok(worker);
            })
            .Produces<RuntimeWorkerDetailsResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get Runtime Worker presence and assignment history");

        var runtime = endpoints.MapGroup("/api/runtime")
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        runtime.MapGet("/agent-instances", async (IRuntimeObservabilityQueryService service,
                ICurrentRequestContext requestContext, CancellationToken token) =>
            Results.Ok(await service.ListAgentInstancesAsync(
                new WorkspaceId(requestContext.Current.WorkspaceId), token)))
            .Produces<AgentInstanceResponse[]>()
            .WithSummary("List active Agent instances observed in the current Workspace");
        runtime.MapGet("/placements/{targetKind}/{runId}", async (string targetKind, string runId,
                IRuntimeObservabilityQueryService service, ICurrentRequestContext requestContext,
                CancellationToken token) =>
            {
                if (!Enum.TryParse<RuntimeAssignmentTargetKind>(targetKind, true, out var kind))
                    return Results.BadRequest();
                var placement = await service.GetPlacementAsync(
                    new WorkspaceId(requestContext.Current.WorkspaceId), kind, runId, token);
                return placement is null ? Results.NotFound() : Results.Ok(placement);
            })
            .Produces<RuntimeAssignmentPlacementResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get durable Worker placement and attempt history for a Runtime or Flow Run");
    }
}
