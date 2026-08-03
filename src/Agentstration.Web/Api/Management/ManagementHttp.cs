using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Web.Api.Management;

internal static class ManagementHttp
{
    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ControlPlaneResourceNotFoundException exception) { return Results.Problem(statusCode: 404, title: "resource_not_found", detail: exception.Message); }
        catch (ControlPlaneConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); }
        catch (AgentDefinitionValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (ModelProfileValidationException exception)
        {
            var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Type = $"https://agentstration.dev/problems/{exception.Code}",
                Title = "Invalid model profile reference",
                Status = StatusCodes.Status422UnprocessableEntity,
                Detail = exception.Message
            };
            problem.Extensions["errors"] = exception.Errors;
            return Results.Problem(problem);
        }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
        catch (InvalidOperationException exception) { return Results.Problem(statusCode: 409, title: "operation_conflict", detail: exception.Message); }
    }

    public static IResult ResourceResult<T>(StoredResource<T> stored, HttpResponse response, int statusCode) where T : Resource
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }

    public static void RequireApiVersion(HttpRequest request)
    {
        if (!string.Equals(request.Query["api-version"], ManagementApiVersions.V20260801, StringComparison.Ordinal))
            throw new AgentDefinitionValidationException("api_version_not_supported", $"Query parameter api-version={ManagementApiVersions.V20260801} is required.");
    }

    public static string? IfMatch(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault();

    public static bool IfNoneMatch(HttpRequest request) =>
        request.Headers.IfNoneMatch.Any(value => string.Equals(value, "*", StringComparison.Ordinal));

    public static string AgentId(string resourceGroup, string name) =>
        ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Agents, "agents", name).Value;

    public static string DeploymentId(string resourceGroup, string name) =>
        ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Agents, "deployments", name).Value;

    public static Task<IResult> ExecuteDeploymentActionAsync(
        string resourceGroup,
        string name,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        Func<StoredResource<AgentDeployment>, CancellationToken, Task<StoredResource<AgentDeployment>>> action,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            RequireApiVersion(request);
            var id = DeploymentId(resourceGroup, name);
            var stored = await service.GetDeploymentAsync(id, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(id);
            var requestedETag = IfMatch(request);
            if (requestedETag is not null && !string.Equals(requestedETag, stored.ETag, StringComparison.Ordinal))
                throw new ControlPlaneConcurrencyException("The supplied ETag does not match the current deployment.");
            var updated = await action(stored, cancellationToken);
            response.Headers.ETag = updated.ETag;
            response.Headers.Location = $"{updated.Value.Id}?api-version={ManagementApiVersions.V20260801}";
            return Results.Accepted(response.Headers.Location, updated.Value);
        });
}
