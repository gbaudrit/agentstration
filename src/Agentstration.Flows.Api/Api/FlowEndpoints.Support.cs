using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Agentstration.Work;

namespace Agentstration.Web;

public static partial class FlowEndpoints
{
    private static async Task<StoredFlow> RequiredVisibleAsync(
        FlowId id,
        string? scopeRef,
        FlowService service,
        ICurrentRequestContext requestContext,
        CancellationToken token) =>
        await service.GetVisibleAsync(CurrentConsumerScope(requestContext), id, ParseScopeRef(scopeRef), token)
        ?? throw new FlowNotFoundException(id);

    private static WorkspaceId CurrentWorkspace(ICurrentRequestContext requestContext) => CurrentScope(requestContext).WorkspaceId;
    private static ResourceScopeRef CurrentConsumerScope(ICurrentRequestContext requestContext) => ResourceScopeRef.Workspace(CurrentWorkspace(requestContext).Value);
    private static ResourceScopeRef? ParseScopeRef(string? value) => string.IsNullOrWhiteSpace(value) ? null : ResourceScopeRef.Parse(value);
    private static ResourceScopeRef RequestedOwnerScope(ResourceScopeRef? value, ICurrentRequestContext requestContext) => value ?? CurrentConsumerScope(requestContext);
    private static ResourceScopeRef RequestedOwnerScope(string? value, ICurrentRequestContext requestContext) => ParseScopeRef(value) ?? CurrentConsumerScope(requestContext);
    private static Task<T> WriteFlowAsync<T>(IResourceScopeOperations scopes, ResourceScopeRef scopeRef, Func<CancellationToken, Task<T>> operation, CancellationToken token) =>
        scopes.WriteAsync(FlowResourceKinds.Flow, scopeRef, AuthorizationPermissions.ResourcesWrite, operation, token);
    private static Task<T> DeleteFlowAsync<T>(IResourceScopeOperations scopes, ResourceScopeRef scopeRef, Func<CancellationToken, Task<T>> operation, CancellationToken token) =>
        scopes.WriteAsync(FlowResourceKinds.Flow, scopeRef, AuthorizationPermissions.ResourcesDelete, operation, token);

    private static FlowResponse ToResponse(FlowResource value) => new(value.Id.Value, value.Name, value.Description, value.Version, value.Enabled, value.ActiveVersion, value.Definition, value.Metadata, value.CreatedAt, value.UpdatedAt, value.Graph) { Namespace = value.Id.Namespace, ScopeRef = value.ScopeRef, DisplayName = value.DisplayName };

    private static FlowSummaryResponse ToSummary(FlowResource value) => new(value.Id.Value, value.Name, value.Description, value.Definition.Kind, value.Version, value.Enabled, value.ActiveVersion, value.UpdatedAt) { Namespace = value.Id.Namespace, ScopeRef = value.ScopeRef, DisplayName = value.DisplayName };

    private static FlowVersionResponse ToVersion(FlowVersion value) => new(value.FlowId.Value, value.Version, value.Description, value.Definition, value.Metadata, value.PublishedAt, value.Graph, value.DefinitionHash, value.ReleaseNotes) { Namespace = value.FlowId.Namespace, ScopeRef = value.ScopeRef };

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (FlowNotFoundException exception) { return Results.Problem(statusCode: 404, title: "flow_not_found", detail: exception.Message); }
        catch (FlowRunNotFoundException exception) { return Results.Problem(statusCode: 404, title: "flow_run_not_found", detail: exception.Message); }
        catch (FlowRunNotTerminalException exception) { return Results.Problem(statusCode: 409, title: "flow_run_not_terminal", detail: exception.Message); }
        catch (InputRequestAlreadyResolvedException exception) { return Results.Problem(statusCode: 409, title: "input_request_already_resolved", detail: exception.Message); }
        catch (FlowConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); }
        catch (FlowReferenceOutsideScopeException exception) { return Results.Problem(statusCode: 400, title: "flow_reference_outside_scope", detail: exception.Message); }
        catch (FlowReferenceAmbiguousException exception) { return Results.Problem(statusCode: 400, title: "flow_reference_ambiguous", detail: exception.Message); }
        catch (ResourceScopeAccessDeniedException exception) { return Results.Problem(statusCode: 403, title: "resource_scope_access_denied", detail: exception.Message); }
        catch (ResourceScopePolicyException exception) { return Results.Problem(statusCode: 422, title: "resource_scope_invalid", detail: exception.Message); }
        catch (FlowValidationException exception) when (exception.Code == "flow_version_already_published") { return Results.Problem(statusCode: 409, title: exception.Code, detail: exception.Message); }
        catch (FlowValidationException exception) when (exception.Code == "flow_run_in_use_by_knowledge_snapshot") { return Results.Problem(statusCode: 409, title: exception.Code, detail: exception.Message); }
        catch (FlowValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (WorkValidationException exception) when (exception.Code == "flow_invocation_idempotency_conflict") { return Results.Problem(statusCode: 409, title: exception.Code, detail: exception.Message); }
        catch (WorkValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
    }

    private static string RequiredIfMatch(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault()
        ?? throw new FlowValidationException("if_match_required", "Saving a Flow Draft requires an If-Match ETag.");
}
