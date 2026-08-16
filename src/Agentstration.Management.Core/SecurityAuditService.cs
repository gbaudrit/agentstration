using System.Diagnostics;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class SecurityAuditService(
    ISecurityAuditStore store,
    ICurrentRequestContext requestContext,
    IPlatformAuthorizationService platformAuthorization,
    TimeProvider timeProvider) : ISecurityAuditWriter
{
    public Task WriteAsync(SecurityAuditWrite entry, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Action);
        if (entry.Action.Length > 128) throw new ArgumentException("Security audit actions cannot exceed 128 characters.", nameof(entry));
        if (entry.ReasonCode?.Length > 64) throw new ArgumentException("Security audit reason codes cannot exceed 64 characters.", nameof(entry));

        var context = requestContext.IsInitialized ? requestContext.Current : null;
        return store.AppendAsync(new SecurityAuditEvent(
            Guid.NewGuid(),
            entry.Action,
            entry.Outcome,
            entry.ActorPrincipalId ?? context?.PrincipalId,
            entry.TargetPrincipalId,
            entry.TargetAccountId,
            entry.TenantId ?? context?.TenantId,
            entry.WorkspaceId ?? context?.WorkspaceId,
            entry.ReasonCode,
            Activity.Current?.TraceId.ToString(),
            timeProvider.GetUtcNow()), cancellationToken);
    }

    public async Task<IReadOnlyList<SecurityAuditEvent>> ListLatestAsync(int limit, CancellationToken cancellationToken)
    {
        if (!requestContext.IsInitialized
            || !await platformAuthorization.IsPlatformAdministratorAsync(requestContext.Current.PrincipalId, cancellationToken))
            throw new AuthorizationDeniedException("platform/admin");
        return await store.ListLatestAsync(Math.Clamp(limit, 1, 200), cancellationToken);
    }
}
