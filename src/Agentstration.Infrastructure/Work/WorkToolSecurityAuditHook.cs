using Agentstration.Identity.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Security.Contracts;

namespace Agentstration.Infrastructure.Work;

public sealed class WorkToolSecurityAuditHook(ISecurityAuditWriter audit, ICurrentRequestContext current) : IToolExecutionHook
{
    public string Id => "work-tool-security-audit";
    public int Order => int.MinValue;

    public async ValueTask AfterInvokeAsync(ToolExecutionContext context, ToolExecutionOutcome outcome, CancellationToken cancellationToken = default)
    {
        var planning = context.ToolName.StartsWith("work.plan.", StringComparison.Ordinal)
            || context.ToolName.StartsWith("work.project.", StringComparison.Ordinal) || context.ToolName.StartsWith("work.task.", StringComparison.Ordinal);
        if (!planning && context.OwnerKind != ToolExecutionOwnerKind.WorkOperation || !current.IsInitialized) return;
        var trusted = current.Current;
        if (trusted.TenantId != context.TenantId || trusted.WorkspaceId != context.WorkspaceId?.Value || trusted.PrincipalId != context.PrincipalId) return;
        var reason = context.OwnerKind == ToolExecutionOwnerKind.WorkOperation ? context.ToolCallId
            : $"{context.ToolName}:{WorkPlanningMcpTool.CommandId(trusted.TenantId, context.WorkspaceId!.Value, trusted.PrincipalId, context.ToolName, context.ToolCallId):D}";
        // Store only the bounded technical call coordinate, never arguments or remote content.
        if (reason.Length > 64) return;
        await audit.WriteAsync(new(context.OwnerKind == ToolExecutionOwnerKind.WorkOperation
                ? SecurityAuditActions.WorkTaskProviderInvoked : SecurityAuditActions.WorkPlanningToolInvoked,
            outcome.Kind == ToolExecutionOutcomeKind.Succeeded ? SecurityAuditOutcome.Succeeded : SecurityAuditOutcome.Failed,
            ActorPrincipalId: trusted.PrincipalId, TenantId: trusted.TenantId, WorkspaceId: trusted.WorkspaceId, ReasonCode: reason), cancellationToken);
    }
}
