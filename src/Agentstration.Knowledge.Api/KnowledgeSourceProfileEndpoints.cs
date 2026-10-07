using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Agentstration.Knowledge.Api.Internal;

internal static class KnowledgeSourceProfileEndpoints
{
    private const string ProblemBase = "https://agentstration.dev/problems/";

    public static void Map(RouteGroupBuilder profiles)
    {
        profiles.MapGet("/", ListAsync).WithSummary("List Knowledge Source Profiles")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        profiles.MapGet("/{name}", GetAsync).WithSummary("Get a Knowledge Source Profile")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        profiles.MapGet("/{name}/revisions", ListRevisionsAsync).WithSummary("List published Knowledge Source Profile revisions")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        profiles.MapGet("/{name}/revisions/{version}", GetRevisionAsync).WithSummary("Get a published Knowledge Source Profile revision")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        profiles.MapPost("/", CreateAsync).WithSummary("Create a Knowledge Source Profile")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        profiles.MapPut("/{name}", PutAsync).WithSummary("Update a Knowledge Source Profile draft")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        profiles.MapPost("/{name}/revisions", PublishAsync).WithSummary("Publish a Knowledge Source Profile revision")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        profiles.MapPut("/{name}/active-revision", ActivateAsync).WithSummary("Activate or roll back a Knowledge Source Profile revision")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        profiles.MapPost("/{name}/application-plan", PreviewApplicationAsync).WithSummary("Preview applying a published profile revision")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        profiles.MapPost("/{name}/applications", ApplyAsync).WithSummary("Apply and publish a profile revision into another logical profile")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        profiles.MapDelete("/{name}", DeleteAsync).WithSummary("Delete a Knowledge Source Profile")
            .RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static Task<IResult> ListAsync(
        string? @namespace,
        KnowledgeSourceProfileService service,
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
        KnowledgeSourceProfileService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var stored = await service.GetAsync(ResourceNamespace.Parse(@namespace), name, cancellationToken)
                ?? throw new ResourceNotFoundException(new(KnowledgeResourceKinds.KnowledgeSourceProfile,
                    name, ResourceNamespace.Parse(@namespace)));
            return Resource(stored, response, 200);
        });

    private static Task<IResult> ListRevisionsAsync(
        string name,
        string? @namespace,
        KnowledgeSourceProfileService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
            Results.Ok((await service.ListRevisionsAsync(ResourceNamespace.Parse(@namespace), name, cancellationToken))
                .Select(value => value.Value).ToArray()));

    private static Task<IResult> GetRevisionAsync(
        string name,
        string version,
        string? @namespace,
        HttpResponse response,
        KnowledgeSourceProfileService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var stored = await service.GetRevisionAsync(ResourceNamespace.Parse(@namespace), name, version, cancellationToken)
                ?? throw new ResourceNotFoundException(new(KnowledgeResourceKinds.KnowledgeSourceProfileRevision,
                    $"{name}:{version}", ResourceNamespace.Parse(@namespace)));
            return Resource(stored, response, 200);
        });

    private static Task<IResult> CreateAsync(
        CreateKnowledgeSourceProfileRequest body,
        HttpResponse response,
        KnowledgeSourceProfileService service,
        ICurrentRequestContext context,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var ns = ResourceNamespace.Parse(body.Namespace);
            var stored = await service.CreateAsync(new KnowledgeSourceProfileResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSourceProfile,
                Metadata = new ResourceMetadata { Name = body.Name, Namespace = ns },
                ScopeRef = ResourceScopeRef.Workspace(context.Current.WorkspaceId),
                Definition = body.Properties
            }, cancellationToken);
            response.Headers.Location = $"/api/knowledgesourceprofiles/{Uri.EscapeDataString(body.Name)}?namespace={Uri.EscapeDataString(ns.Value)}";
            return Resource(stored, response, 201);
        });

    private static Task<IResult> PutAsync(
        string name,
        string? @namespace,
        PutKnowledgeSourceProfileRequest body,
        HttpRequest request,
        HttpResponse response,
        KnowledgeSourceProfileService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Resource(
            await service.PutAsync(ResourceNamespace.Parse(@namespace), name, body.Properties,
                request.Headers.IfMatch.FirstOrDefault(), cancellationToken), response, 200));

    private static Task<IResult> PublishAsync(
        string name,
        string? @namespace,
        PublishKnowledgeSourceProfileRequest body,
        HttpResponse response,
        KnowledgeSourceProfileService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Resource(
            await service.PublishAsync(ResourceNamespace.Parse(@namespace), name, body, cancellationToken), response, 201));

    private static Task<IResult> ActivateAsync(
        string name,
        string? @namespace,
        ActivateKnowledgeSourceProfileRequest body,
        HttpRequest request,
        HttpResponse response,
        KnowledgeSourceProfileService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Resource(
            await service.ActivateAsync(ResourceNamespace.Parse(@namespace), name, body,
                request.Headers.IfMatch.FirstOrDefault(), cancellationToken), response, 200));

    private static Task<IResult> PreviewApplicationAsync(
        string name,
        string? @namespace,
        PreviewKnowledgeSourceProfileApplicationRequest body,
        KnowledgeSourceProfileService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Ok(
            await service.PreviewApplicationAsync(ResourceNamespace.Parse(@namespace), name, body, cancellationToken)));

    private static Task<IResult> ApplyAsync(
        string name,
        string? @namespace,
        PreviewKnowledgeSourceProfileApplicationRequest body,
        HttpRequest request,
        HttpResponse response,
        KnowledgeSourceProfileService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Resource(
            await service.ApplyAsync(ResourceNamespace.Parse(@namespace), name, body,
                request.Headers.IfMatch.FirstOrDefault(), cancellationToken), response, 201));

    private static Task<IResult> DeleteAsync(
        string name,
        string? @namespace,
        HttpRequest request,
        KnowledgeSourceProfileService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            await service.DeleteAsync(ResourceNamespace.Parse(@namespace), name,
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
        catch (ResourceNotFoundException exception)
        { return Problem("knowledge-source-profile-not-found", "Knowledge Source Profile not found", 404, exception.Message); }
        catch (ResourceConcurrencyException exception)
        { return Problem("resource-version-conflict", "Resource version conflict", 409, exception.Message); }
        catch (ResourceScopeAccessDeniedException exception)
        { return Problem("resource-scope-access-denied", "Resource scope access denied", 403, exception.Message); }
        catch (KnowledgeSourceProfileValidationException exception)
        {
            var status = exception.Code is "knowledge_source_profile_in_use"
                or "knowledge_source_profile_revision_immutable"
                or "knowledge_source_profile_builtin_protected" ? 409 : 422;
            return Problem(exception.Code, "Invalid Knowledge Source Profile", status, exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        { return Problem("knowledge-source-profile-operation-invalid", "Invalid Knowledge Source Profile operation", 422, exception.Message); }
    }

    private static IResult Problem(string type, string title, int status, string detail) => Results.Problem(new ProblemDetails
    {
        Type = ProblemBase + type,
        Title = title,
        Status = status,
        Detail = detail
    });
}
