using Agentstration.Reconciliation.Contracts;
using Agentstration.SystemGraph;
using Agentstration.Web.Security;

namespace Agentstration.Reconciliation.Api;

public static class ReconciliationApiModule
{
    public static IEndpointRouteBuilder MapReconciliationApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/system-reconciliation").RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "no-store"; return await next(context); });
        group.MapPost("/preview", async (ReconciliationPreviewRequest body, IReconciliationPreviewService service, CancellationToken ct) =>
            await ExecuteAsync(() => service.PreviewAsync(body, ct)))
            .Produces<ReconciliationPreview>().WithSummary("Preview selected dependency reconciliation without mutations");
        group.MapPost("/revalidate", async (ReconciliationRevalidationRequest body, IReconciliationPreviewService service, CancellationToken ct) =>
            await ExecuteAsync(() => service.RevalidateAsync(body, ct)))
            .Produces<ReconciliationPreview>().WithSummary("Revalidate the relevant evidence of a reconciliation review");
        return endpoints;
    }
    private static async Task<IResult> ExecuteAsync(Func<Task<ReconciliationPreview>> action)
    {
        try { return Results.Ok(await action()); }
        catch (ReconciliationPreviewException error) { return Results.Problem(statusCode: error.Code == "reconciliation_request_invalid" ? 400 : 409, title: error.Code); }
        catch (SystemGraphException error) { return Results.Problem(statusCode: error.Code == "system_graph_forbidden" ? 403 : error.Code == "system_graph_changed" ? 409 : 503, title: "reconciliation_context_unavailable"); }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "reconciliation_request_invalid"); }
    }
}
