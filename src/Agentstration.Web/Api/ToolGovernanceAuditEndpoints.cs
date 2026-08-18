using Agentstration.Management.Abstractions;
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
        string? toolId,
        string? hookId,
        string? decision,
        IToolGovernanceAuditReader reader,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = new ToolGovernanceAuditQuery
            {
                OwnerKind = ParseOwnerKind(ownerKind),
                WorkspaceId = new WorkspaceId(requestContext.Current.WorkspaceId),
                RunId = runId,
                AfterSequence = afterSequence ?? 0,
                Limit = limit ?? 100,
                ToolId = NullIfWhiteSpace(toolId),
                HookId = NullIfWhiteSpace(hookId),
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
}
