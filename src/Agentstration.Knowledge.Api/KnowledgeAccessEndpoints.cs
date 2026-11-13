using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Web.Security;

namespace Agentstration.Knowledge.Api.Internal;

internal static class KnowledgeAccessEndpoints
{
    public static void Map(RouteGroupBuilder sources)
    {
        sources.MapPost("/{name}/content/read", (string name, string? @namespace, string? scopeRef,
            KnowledgeReadRequest body, KnowledgeAccessService service, CancellationToken ct) => ExecuteAsync(async () =>
                Content(await service.ReadAsync(Id(name, @namespace, scopeRef), body, ct))))
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        sources.MapPost("/{name}/content/search-regex", (string name, string? @namespace, string? scopeRef,
            KnowledgeRegexRequest body, KnowledgeAccessService service, CancellationToken ct) => ExecuteAsync(async () =>
                Content(await service.SearchRegexAsync(Id(name, @namespace, scopeRef), body, ct))))
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        sources.MapPost("/{name}/content/status", (string name, string? @namespace, string? scopeRef,
            KnowledgeStatusRequest body, KnowledgeAccessService service, CancellationToken ct) => ExecuteAsync(async () =>
                Results.Ok(await service.GetStatusAsync(Id(name, @namespace, scopeRef), body, ct))))
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
    }
    private static KnowledgeSourceId Id(string name, string? ns, string? scope) => new(name,
        ResourceNamespace.Parse(ns), string.IsNullOrWhiteSpace(scope) ? null : ResourceScopeRef.Parse(scope));
    private static IResult Content(KnowledgeAccessResult result) => Results.Json(result, statusCode: result.State == "preparing" ? 202 : 200);
    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (KnowledgeAccessException exception) { return Problem(exception.Code); }
        catch (KnowledgeSnapshotException exception) { return Problem(exception.Code); }
        catch (KnowledgeSourceValidationException exception) { return Problem(exception.Code); }
        catch (KnowledgeProjectionException exception) { return Problem(exception.Code); }
        catch (StorageException exception) { return Problem(exception.Code); }
        catch (ResourceScopeAccessDeniedException) { return Problem("resource-scope-access-denied", 403); }
        catch (KnowledgeSourceNotFoundException) { return Problem("knowledge_access_source_not_found", 404); }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        { return Problem("knowledge_access_request_invalid"); }
    }
    private static IResult Problem(string code, int status = 422) => Results.Problem(
        type: "https://agentstration.dev/problems/" + code, title: "Knowledge content access failed",
        statusCode: status, detail: "The Knowledge content operation is unavailable or invalid.");
}
