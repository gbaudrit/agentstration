using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static class ToolGovernanceAuditEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationToolGovernanceAuditApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tool-governance/{ownerKind}/{runId}", ListAsync)
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string ownerKind,
        string runId,
        long? afterSequence,
        int? limit,
        string? toolCallId,
        string? invocationId,
        string? toolId,
        string? hookId,
        long? resourceGeneration,
        string? decision,
        string? scopeRef,
        IToolGovernanceAuditReader reader,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var scope = RequestedScope(requestContext, scopeRef);
            var query = new ToolGovernanceAuditQuery
            {
                OwnerKind = ParseOwnerKind(ownerKind),
                ScopeRef = scope,
                RunId = runId,
                AfterSequence = afterSequence ?? 0,
                Limit = limit ?? 100,
                ToolCallId = NullIfWhiteSpace(toolCallId),
                InvocationId = NullIfWhiteSpace(invocationId),
                ToolId = NullIfWhiteSpace(toolId),
                HookId = NullIfWhiteSpace(hookId),
                ResourceGeneration = resourceGeneration,
                Decision = ParseDecision(decision)
            };
            return Results.Ok(await reader.ListAsync(query, cancellationToken));
        }
        catch (ToolGovernanceAuditRunNotFoundException exception)
        {
            return Results.Problem(statusCode: 404, title: "run_not_found", detail: exception.Message);
        }
        catch (ToolGovernanceAuditValidationException exception)
        {
            return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message);
        }
    }

    private static ToolExecutionOwnerKind ParseOwnerKind(string value) => value.ToLowerInvariant() switch
    {
        "runtime" or "runtimerun" => ToolExecutionOwnerKind.RuntimeRun,
        "flow" or "flowrun" => ToolExecutionOwnerKind.FlowRun,
        _ => throw new ToolGovernanceAuditValidationException(
            "invalid_owner_kind",
            "ownerKind must be 'runtime' or 'flow'.")
    };

    private static ToolExecutionHookEvaluationKind? ParseDecision(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse<ToolExecutionHookEvaluationKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ToolGovernanceAuditValidationException(
                "invalid_decision",
                "decision must be 'allowed', 'denied' or 'failed'.");
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static ResourceScopeRef RequestedScope(ICurrentRequestContext requestContext, string? value)
    {
        var current = requestContext.Current;
        var requested = string.IsNullOrWhiteSpace(value)
            ? ResourceScopeRef.Workspace(current.WorkspaceId)
            : ResourceScopeRef.Parse(value);
        return requested.Kind switch
        {
            ResourceScopeKind.Instance => requested,
            ResourceScopeKind.Tenant when requested.TargetId == current.TenantId => requested,
            ResourceScopeKind.Workspace when requested.TargetId == current.WorkspaceId => requested,
            _ => throw new ToolGovernanceAuditValidationException(
                "execution_scope_not_authorized",
                "The requested execution scope is not the current Instance, Tenant, or Workspace context.")
        };
    }
}
