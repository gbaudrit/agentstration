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
        catch (AgentRevisionPurgeBlockedException exception)
        {
            var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Type = "https://agentstration.dev/problems/agent-revision-purge-blocked",
                Title = "agent_revision_purge_blocked",
                Status = StatusCodes.Status409Conflict,
                Detail = exception.Message
            };
            problem.Extensions["impact"] = exception.Impact;
            return Results.Problem(problem);
        }
        catch (PackNotFoundException exception) { return Results.Problem(statusCode: 404, title: "pack_not_found", detail: exception.Message); }
        catch (KeyNotFoundException exception) { return Results.Problem(statusCode: 404, title: "resource_not_found", detail: exception.Message); }
        catch (PackValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (PackAlreadyInstalledException exception) { return Results.Problem(statusCode: 409, title: "pack_already_installed", detail: exception.Message); }
        catch (PackResourceConflictException exception) { return Results.Problem(statusCode: 409, title: "pack_resource_conflict", detail: exception.Message); }
        catch (PackResourceModifiedException exception) { return Results.Problem(statusCode: 409, title: "pack_resource_modified", detail: exception.Message); }
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
        catch (InvalidDataException exception) { return Results.Problem(statusCode: 400, title: "pack_archive_invalid", detail: exception.Message); }
        catch (InvalidOperationException exception) { return Results.Problem(statusCode: 409, title: "operation_conflict", detail: exception.Message); }
    }

    public static IResult ResourceResult<T>(StoredResource<T> stored, HttpResponse response, int statusCode) where T : Resource
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }

    public static void RequireApiVersion(HttpRequest request)
    {
        if (request.Query.TryGetValue("api-version", out var version)
            && !string.Equals(version, ManagementApiVersions.CoreV1, StringComparison.Ordinal))
            throw new AgentDefinitionValidationException("api_version_not_supported", $"Only api-version={ManagementApiVersions.CoreV1} is supported.");
    }

    public static string? IfMatch(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault();

    public static bool IfNoneMatch(HttpRequest request) =>
        request.Headers.IfNoneMatch.Any(value => string.Equals(value, "*", StringComparison.Ordinal));

    public static Task<IResult> ExecuteDeploymentActionAsync(
        string name,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        Func<StoredResource<AgentDeployment>, CancellationToken, Task<StoredResource<AgentDeployment>>> action,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            RequireApiVersion(request);
            var stored = await service.GetDeploymentAsync(name, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.AgentDeployment, name));
            var requestedETag = IfMatch(request);
            if (requestedETag is not null && !string.Equals(requestedETag, stored.ETag, StringComparison.Ordinal))
                throw new ControlPlaneConcurrencyException("The supplied ETag does not match the current deployment.");
            var updated = await action(stored, cancellationToken);
            response.Headers.ETag = updated.ETag;
            response.Headers.Location = $"/api/deployments/{Uri.EscapeDataString(updated.Value.Metadata.Name)}";
            return Results.Accepted(response.Headers.Location, updated.Value);
        });
}
