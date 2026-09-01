namespace Agentstration.Management.Abstractions;

public enum SecurityAuditOutcome
{
    Succeeded,
    Failed
}

public static class SecurityAuditActions
{
    public const string InstanceBootstrapped = "instance.bootstrapped";
    public const string PlatformAdministratorGranted = "platform-administrator.granted";
    public const string PlatformAdministratorRevoked = "platform-administrator.revoked";
    public const string LocalLogin = "local-account.login";
    public const string LocalLogout = "local-account.logout";
    public const string LocalAccountCreated = "local-account.created";
    public const string LocalAccountEnabled = "local-account.enabled";
    public const string LocalAccountDisabled = "local-account.disabled";
    public const string LocalPasswordChanged = "local-account.password-changed";
    public const string LocalSessionsRevoked = "local-account.sessions-revoked";
    public const string ExternalIdentityLinked = "external-identity.linked";
    public const string ExternalIdentityUnlinked = "external-identity.unlinked";
    public const string WorkspaceMembershipSet = "workspace-membership.set";
    public const string WorkspaceMembershipRemoved = "workspace-membership.removed";
    public const string AgentRevisionPurged = "agent-revision.purged";
    public const string AgentRevisionForcePurged = "agent-revision.force-purged";
    public const string PersonalAccessTokenCreated = "personal-access-token.created";
    public const string PersonalAccessTokenRevoked = "personal-access-token.revoked";
    public const string PersonalAccessTokensRevoked = "personal-access-token.revoked-all";
    public const string BootstrapProfileApplied = "bootstrap-profile.applied";
}

public sealed record SecurityAuditEvent(
    Guid Id,
    string Action,
    SecurityAuditOutcome Outcome,
    Guid? ActorPrincipalId,
    Guid? TargetPrincipalId,
    Guid? TargetAccountId,
    Guid? TenantId,
    Guid? WorkspaceId,
    string? ReasonCode,
    string? CorrelationId,
    DateTimeOffset OccurredAt);

public sealed record SecurityAuditWrite(
    string Action,
    SecurityAuditOutcome Outcome = SecurityAuditOutcome.Succeeded,
    Guid? ActorPrincipalId = null,
    Guid? TargetPrincipalId = null,
    Guid? TargetAccountId = null,
    Guid? TenantId = null,
    Guid? WorkspaceId = null,
    string? ReasonCode = null);

public interface ISecurityAuditWriter
{
    Task WriteAsync(SecurityAuditWrite entry, CancellationToken cancellationToken);
}

public interface ISecurityAuditStore
{
    Task AppendAsync(SecurityAuditEvent entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<SecurityAuditEvent>> ListLatestAsync(int limit, CancellationToken cancellationToken);
}
