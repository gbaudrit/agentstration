using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Agentstration.Knowledge.Api.Internal;

internal static class KnowledgeRetrievalEndpoints
{
    private const string ProblemBase = "https://agentstration.dev/problems/";

    public static void Map(RouteGroupBuilder sources)
    {
        sources.MapPost("/{name}/search", SearchAsync)
            .WithSummary("Search a KnowledgeSource through its retrieval Flow")
            .Produces<KnowledgeRetrievalResult>()
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        sources.MapPost("/{name}/query", QueryAsync)
            .WithSummary("Query a KnowledgeSource through its retrieval Flow")
            .Produces<KnowledgeRetrievalResult>()
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        sources.MapPost("/{name}/read", ReadAsync)
            .WithSummary("Read KnowledgeSource content through its retrieval Flow")
            .Produces<KnowledgeRetrievalResult>()
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
    }

    private static Task<IResult> SearchAsync(
        string name,
        string? @namespace,
        SearchKnowledgeRequest body,
        KnowledgeRetrievalService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Ok(
            await service.SearchAsync(new(name, ResourceNamespace.Parse(@namespace)), body, cancellationToken)));

    private static Task<IResult> QueryAsync(
        string name,
        string? @namespace,
        QueryKnowledgeRequest body,
        KnowledgeRetrievalService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Ok(
            await service.QueryAsync(new(name, ResourceNamespace.Parse(@namespace)), body, cancellationToken)));

    private static Task<IResult> ReadAsync(
        string name,
        string? @namespace,
        ReadKnowledgeRequest body,
        KnowledgeRetrievalService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Ok(
            await service.ReadAsync(new(name, ResourceNamespace.Parse(@namespace)), body, cancellationToken)));

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (KnowledgeRetrievalException exception)
        {
            var status = exception.Code is "knowledge_retrieval_source_not_found"
                or "knowledge_snapshot_not_found"
                or "knowledge_snapshot_active_not_found" ? 404 : 422;
            return Problem(exception.Code, "Knowledge retrieval failed", status, exception.Message);
        }
        catch (ResourceScopeAccessDeniedException exception)
        { return Problem("resource-scope-access-denied", "Resource scope access denied", 403, exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        { return Problem("knowledge_retrieval_request_invalid", "Invalid Knowledge retrieval request", 422, exception.Message); }
    }

    private static IResult Problem(string type, string title, int status, string detail) => Results.Problem(new ProblemDetails
    {
        Type = ProblemBase + type,
        Title = title,
        Status = status,
        Detail = detail
    });
}
