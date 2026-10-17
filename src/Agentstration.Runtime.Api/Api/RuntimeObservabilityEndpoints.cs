using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Security;
using Agentstration.Runtime.Core;
using Agentstration.Web.Api.Runtime;

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
        runtime.MapGet("/agent-instances", async (string? scopeRef, IRuntimeObservabilityQueryService service,
                RuntimeRunService runs, ICurrentRequestContext requestContext, CancellationToken token) =>
            {
                var scope = RuntimeHttp.CurrentScope(requestContext,
                    string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef));
                await runs.AuthorizeReadAsync(scope, token);
                return Results.Ok(await service.ListAgentInstancesExactAsync(scope.ScopeRef, token));
            })
            .Produces<AgentInstanceResponse[]>()
            .WithSummary("List active Agent instances observed in the current Workspace");
        runtime.MapGet("/placements/{targetKind}/{runId}", async (string targetKind, string runId, string? scopeRef,
                IRuntimeObservabilityQueryService service, RuntimeRunService runs, ICurrentRequestContext requestContext,
                CancellationToken token) =>
            {
                if (!Enum.TryParse<RuntimeAssignmentTargetKind>(targetKind, true, out var kind))
                    return Results.BadRequest();
                var scope = RuntimeHttp.CurrentScope(requestContext,
                    string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef));
                await runs.AuthorizeReadAsync(scope, token);
                var placement = await service.GetPlacementExactAsync(scope.ScopeRef, kind, runId, token);
                return placement is null ? Results.NotFound() : Results.Ok(placement);
            })
            .Produces<RuntimeAssignmentPlacementResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get durable Worker placement and attempt history for a Runtime or Flow Run");
    }
}
