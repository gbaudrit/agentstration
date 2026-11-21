using Agentstration.Agents;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class AgentDeploymentAssignmentsEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/agent-definition-catalog", DefinitionsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources)
            .Produces<AgentDefinitionCatalog>().WithSummary("Read an authorized Agent and its exact retained revision identities for definition selection");
        group.MapGet("/agent-deployment-assignments", ListAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources)
            .Produces<IReadOnlyList<AgentDeploymentView>>().WithSummary("List authorized durable Agent deployment assignments and retained configurations");
        group.MapPost("/agent-deployment-assignments/{name}", CreateAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources)
            .Produces<AgentDeploymentView>(201).WithSummary("Record an Agent revision assignment without provisioning or starting execution");
        group.MapPut("/agent-deployment-assignments/{name}", UpdateAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources)
            .Produces<AgentDeploymentView>().WithSummary("Explicitly reassign a deployment and atomically retain its previous configuration");
    }
    private static Task<IResult> DefinitionsAsync(Guid uid, AgentDeploymentAssignmentService service, HttpResponse response, CancellationToken ct) =>
        ExecuteAsync(response, async () => Results.Ok(await service.DefinitionsAsync(uid, ct)));
    private static Task<IResult> ListAsync(string? @namespace, Guid? agentUid, AgentDeploymentAssignmentService service,
        HttpResponse response, CancellationToken ct) => ExecuteAsync(response, async () => Results.Ok(await service.ListAsync(
            ResourceNamespace.Parse(@namespace ?? ResourceNamespace.DefaultValue), agentUid, ct)));
    private static Task<IResult> CreateAsync(string name, AgentDeploymentAssignmentRequest value, AgentDeploymentAssignmentService service,
        HttpResponse response, CancellationToken ct) => ExecuteAsync(response, async () =>
        {
            if (name != value.Name) return Results.Problem(statusCode: 400, title: "agent_deployment_assignment_invalid");
            var created = await service.AssignAsync(value, null, true, ct); response.Headers.ETag = created.ETag;
            return Results.Created("/api/agent-deployment-assignments/" + Uri.EscapeDataString(created.Name), created);
        });
    private static Task<IResult> UpdateAsync(string name, AgentDeploymentAssignmentRequest value, AgentDeploymentAssignmentService service,
        HttpRequest request, HttpResponse response, CancellationToken ct) => ExecuteAsync(response, async () =>
        {
            if (name != value.Name) return Results.Problem(statusCode: 400, title: "agent_deployment_assignment_invalid");
            var updated = await service.AssignAsync(value, request.Headers.IfMatch.ToString(), false, ct); response.Headers.ETag = updated.ETag;
            return Results.Ok(updated);
        });
    private static async Task<IResult> ExecuteAsync(HttpResponse response, Func<Task<IResult>> action)
    {
        response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (AgentDeploymentAssignmentException error) { return Results.Problem(statusCode: error.Status, title: error.Code); }
        catch (SystemGraphException) { return Results.Problem(statusCode: 403, title: "agent_deployment_unavailable"); }
        catch (ResourceScopeAccessDeniedException) { return Results.Problem(statusCode: 403, title: "agent_deployment_unavailable"); }
        catch (ResourceConcurrencyException) { return Results.Problem(statusCode: 412, title: "agent_deployment_precondition_failed"); }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "agent_deployment_assignment_invalid"); }
    }
}
