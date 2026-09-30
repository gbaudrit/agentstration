using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Agentstration.Knowledge.Api.Internal;

internal static class KnowledgeSourceEndpoints
{
    private const string ProblemBase = "https://agentstration.dev/problems/";

    public static void Map(RouteGroupBuilder sources)
    {
        sources.MapGet("/", ListAsync)
            .WithSummary("List KnowledgeSources")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        sources.MapGet("/{name}", GetAsync)
            .WithSummary("Get a KnowledgeSource")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        sources.MapGet("/{name}/readiness", GetReadinessAsync)
            .WithSummary("Inspect KnowledgeSource readiness")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        sources.MapGet("/{name}/tool-exposure", GetToolExposureAsync)
            .WithSummary("Get a KnowledgeSource Tool exposure")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        sources.MapPost("/", CreateAsync)
            .WithSummary("Create a KnowledgeSource")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        sources.MapPut("/{name}", PutAsync)
            .WithSummary("Update a KnowledgeSource")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        sources.MapPut("/{name}/enabled", SetEnabledAsync)
            .WithSummary("Enable or disable a KnowledgeSource")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        sources.MapPost("/{name}/tool-exposure", PublishToolExposureAsync)
            .WithSummary("Publish source-specific Tools and a ToolSet for a KnowledgeSource")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        sources.MapDelete("/{name}", DeleteAsync)
            .WithSummary("Delete a KnowledgeSource")
            .RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static Task<IResult> ListAsync(
        string? @namespace,
        KnowledgeSourceManagementService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var values = (await service.ListAsync(cancellationToken)).Select(value => value.Value);
            if (!string.IsNullOrWhiteSpace(@namespace))
            {
                var parsed = ResourceNamespace.Parse(@namespace);
                values = values.Where(value => value.Namespace == parsed);
            }
            return Results.Ok(values.ToArray());
        });

    private static Task<IResult> GetAsync(
        string name,
        string? @namespace,
        HttpResponse response,
        KnowledgeSourceManagementService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var id = new KnowledgeSourceId(name, ResourceNamespace.Parse(@namespace));
            var stored = await service.GetAsync(id, cancellationToken) ?? throw new KnowledgeSourceNotFoundException(id);
            return Resource(stored, response, 200);
        });

    private static Task<IResult> GetReadinessAsync(
        string name,
        string? @namespace,
        KnowledgeSourceManagementService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
            Results.Ok(await service.GetReadinessAsync(
                new(name, ResourceNamespace.Parse(@namespace)), cancellationToken)));

    private static Task<IResult> GetToolExposureAsync(
        string name,
        string? @namespace,
        HttpResponse response,
        KnowledgeSourceToolExposureService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var id = new KnowledgeSourceId(name, ResourceNamespace.Parse(@namespace));
            var stored = await service.GetAsync(id, cancellationToken)
                ?? throw new KnowledgeSourceValidationException("knowledge_source_tool_exposure_not_found", $"KnowledgeSource '{id}' has no Tool exposure.");
            return Resource(stored, response, 200);
        });

    private static Task<IResult> CreateAsync(
        CreateKnowledgeSourceRequest body,
        HttpResponse response,
        KnowledgeSourceManagementService service,
        ICurrentRequestContext context,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var ns = ResourceNamespace.Parse(body.Namespace);
            var stored = await service.CreateAsync(new KnowledgeSourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSource,
                Metadata = new ResourceMetadata { Name = body.Name, Namespace = ns },
                ScopeRef = ResourceScopeRef.Workspace(context.Current.WorkspaceId),
                Definition = body.Properties
            }, cancellationToken);
            response.Headers.Location = $"/api/knowledgesources/{Uri.EscapeDataString(body.Name)}?namespace={Uri.EscapeDataString(ns.Value)}";
            return Resource(stored, response, 201);
        });

    private static Task<IResult> PutAsync(
        string name,
        string? @namespace,
        PutKnowledgeSourceRequest body,
        HttpRequest request,
        HttpResponse response,
        KnowledgeSourceManagementService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Resource(
            await service.PutAsync(new(name, ResourceNamespace.Parse(@namespace)), body.Properties,
                request.Headers.IfMatch.FirstOrDefault(), cancellationToken), response, 200));

    private static Task<IResult> SetEnabledAsync(
        string name,
        string? @namespace,
        SetKnowledgeSourceEnabledRequest body,
        HttpRequest request,
        HttpResponse response,
        KnowledgeSourceManagementService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Resource(
            await service.SetEnabledAsync(new(name, ResourceNamespace.Parse(@namespace)), body.Enabled,
                request.Headers.IfMatch.FirstOrDefault(), cancellationToken), response, 200));

    private static Task<IResult> PublishToolExposureAsync(
        string name,
        string? @namespace,
        PublishKnowledgeSourceToolExposureRequest body,
        HttpResponse response,
        KnowledgeSourceToolExposureService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Resource(
            await service.PublishAsync(new(name, ResourceNamespace.Parse(@namespace)), body.Version,
                body.RequiresApproval, cancellationToken), response, 201));

    private static Task<IResult> DeleteAsync(
        string name,
        string? @namespace,
        HttpRequest request,
        KnowledgeSourceManagementService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            await service.DeleteAsync(new(name, ResourceNamespace.Parse(@namespace)),
                request.Headers.IfMatch.FirstOrDefault(), cancellationToken);
            return Results.NoContent();
        });

    private static IResult Resource<T>(StoredResource<T> stored, HttpResponse response, int status) where T : Resource
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: status);
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (KnowledgeSourceNotFoundException exception)
        { return Problem("knowledge-source-not-found", "KnowledgeSource not found", 404, exception.Message); }
        catch (ResourceConcurrencyException exception)
        { return Problem("resource-version-conflict", "Resource version conflict", 409, exception.Message); }
        catch (ResourceScopeAccessDeniedException exception)
        { return Problem("resource-scope-access-denied", "Resource scope access denied", 403, exception.Message); }
        catch (KnowledgeSourceValidationException exception)
        { return Problem(exception.Code, "Invalid KnowledgeSource", 422, exception.Message); }
        catch (ToolDefinitionValidationException exception)
        { return Problem(exception.Code, "Invalid KnowledgeSource Tool exposure", 422, exception.Message); }
        catch (ToolSetValidationException exception)
        { return Problem(exception.Code, "Invalid KnowledgeSource Tool exposure", 422, exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        { return Problem("knowledge-source-operation-invalid", "Invalid KnowledgeSource operation", 422, exception.Message); }
    }

    private static IResult Problem(string type, string title, int status, string detail) => Results.Problem(new ProblemDetails
    {
        Type = ProblemBase + type,
        Title = title,
        Status = status,
        Detail = detail
    });
}
