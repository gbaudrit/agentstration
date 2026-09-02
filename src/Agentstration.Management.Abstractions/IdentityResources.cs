namespace Agentstration.Management.Abstractions;

public enum TenantStatus { Active, Disabled }
public enum WorkspaceStatus { Active, Disabled }
public enum PrincipalStatus { Active, Disabled }
public enum PrincipalKind { Human, Workload }
public enum MembershipStatus { Active, Suspended }
public enum PrincipalType { User, Group, ServicePrincipal }
public enum ThemePreference { System, Light, Dark }

public sealed record Tenant(
    Guid Id,
    string Name,
    string DisplayName,
    TenantStatus Status,
    DateTimeOffset CreatedAt);

public sealed record Workspace(
    Guid Id,
    Guid TenantId,
    string Name,
    string DisplayName,
    WorkspaceStatus Status,
    DateTimeOffset CreatedAt);

public sealed record Principal(
    Guid Id,
    PrincipalKind Kind,
    string DisplayName,
    string? Email,
    PrincipalStatus Status,
    DateTimeOffset CreatedAt);

public sealed record PrincipalPreferences(
    Guid PrincipalId,
    ThemePreference Theme,
    DateTimeOffset UpdatedAt,
    string? Language = null,
    Guid? DefaultTenantId = null,
    Guid? DefaultWorkspaceId = null);

public sealed record ExternalIdentity(
    Guid Id,
    string Issuer,
    string Subject,
    Guid PrincipalId,
    DateTimeOffset LinkedAt);

public sealed record LocalIdentity(
    Guid AccountId,
    Guid PrincipalId,
    DateTimeOffset LinkedAt);

public sealed record PlatformAdministrator(
    Guid PrincipalId,
    DateTimeOffset GrantedAt);

public sealed record TenantMembership(
    Guid Id,
    Guid TenantId,
    Guid PrincipalId,
    MembershipStatus Status,
    DateTimeOffset JoinedAt);

public sealed record WorkspaceMembership(
    Guid Id,
    Guid WorkspaceId,
    Guid PrincipalId,
    MembershipStatus Status,
    DateTimeOffset JoinedAt);

public sealed record RoleDefinition(
    Guid Id,
    string Name,
    string DisplayName,
    IReadOnlyCollection<string> Permissions,
    bool IsBuiltIn);

public sealed record RoleAssignment(
    Guid Id,
    Guid TenantId,
    Guid PrincipalId,
    PrincipalType PrincipalType,
    Guid RoleDefinitionId,
    string Scope);

public static class AuthorizationPermissions
{
    public const string TenantsRead = "tenants/read";
    public const string TenantsManage = "tenants/manage";
    public const string WorkspacesRead = "workspaces/read";
    public const string WorkspacesWrite = "workspaces/write";
    public const string WorkspacesDelete = "workspaces/delete";
    public const string ResourcesRead = "resources/read";
    public const string ResourcesWrite = "resources/write";
    public const string ResourcesDelete = "resources/delete";
    public const string RunsRead = "runs/read";
    public const string RunsExecute = "runs/execute";
    public const string RunsDelete = "runs/delete";
    public const string AuthorizationRead = "authorization/read";
    public const string AuthorizationWrite = "authorization/write";

    public static readonly IReadOnlyCollection<string> All =
    [
        TenantsRead, TenantsManage, WorkspacesRead, WorkspacesWrite, WorkspacesDelete,
        ResourcesRead, ResourcesWrite, ResourcesDelete, RunsRead, RunsExecute, RunsDelete,
        AuthorizationRead, AuthorizationWrite
    ];
}

public static class AuthorizationScopes
{
    public static string Tenant(Guid tenantId) => $"/tenants/{tenantId:D}";
    public static string Workspace(Guid workspaceId) => $"/workspaces/{workspaceId:D}";
}

public sealed record RequestContext(
    Guid PrincipalId,
    Guid TenantId,
    Guid WorkspaceId,
    AuthorizationRestriction? Restriction = null)
{
    public Guid UserId => PrincipalId;
}
public enum ControlPlaneAccessMode { Unavailable, Workspace, System }

public interface ICurrentRequestContext
{
    bool IsInitialized { get; }
    RequestContext Current { get; }
    ControlPlaneAccessMode AccessMode => IsInitialized ? ControlPlaneAccessMode.Workspace : ControlPlaneAccessMode.Unavailable;
}

public sealed class UnavailableRequestContext : ICurrentRequestContext
{
    public bool IsInitialized => false;
    public ControlPlaneAccessMode AccessMode => ControlPlaneAccessMode.Unavailable;
    public RequestContext Current => throw new InvalidOperationException("No request context is available.");
}

public sealed class SystemOperationRequestContext : ICurrentRequestContext
{
    public bool IsInitialized => false;
    public ControlPlaneAccessMode AccessMode => ControlPlaneAccessMode.System;
    public RequestContext Current => throw new InvalidOperationException("System operations do not have a workspace request context.");
}

public interface IRequestContextScopeFactory
{
    IDisposable Push(RequestContext context);
    IDisposable PushSystem();
}

public interface IPrincipalResolver
{
    Task<Principal?> ResolveAsync(string issuer, string subject, CancellationToken cancellationToken);
    Task<Principal?> ResolveLocalAsync(Guid accountId, CancellationToken cancellationToken);
}

public sealed record InitialPrincipalProvisioning(
    Guid AccountId,
    Guid PrincipalId,
    string DisplayName,
    string? Email);

public sealed record InitialPrincipalProvisioningResult(
    Principal Principal);

public interface IInitialPrincipalProvisioner
{
    Task<InitialPrincipalProvisioningResult> ProvisionAsync(InitialPrincipalProvisioning request, CancellationToken cancellationToken);
}

public sealed record InitialTopologyProvisioning(
    Guid PrincipalId,
    string TenantName,
    string TenantDisplayName,
    string WorkspaceName,
    string WorkspaceDisplayName);

public sealed record InitialTopologyProvisioningResult(Tenant Tenant, Workspace Workspace);

public interface IInitialTopologyProvisioner
{
    Task<InitialTopologyProvisioningResult> ProvisionAsync(InitialTopologyProvisioning request, CancellationToken cancellationToken);
}

public interface ILocalAccountPrincipalResolver
{
    Task<Principal?> ResolveByUserNameAsync(string userName, CancellationToken cancellationToken);
}

public sealed record LocalPrincipalProvisioning(
    Guid AccountId,
    Guid PrincipalId,
    Guid WorkspaceId,
    string Role,
    string DisplayName,
    string? Email);

public interface ILocalPrincipalProvisioner
{
    Task<Principal> ProvisionAsync(LocalPrincipalProvisioning request, CancellationToken cancellationToken);
}

public interface IPlatformAuthorizationService
{
    Task<bool> IsPlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken);
}

public interface IPlatformAdministratorPolicy
{
    Task<IAsyncDisposable> AcquireDisableLeaseAsync(Guid principalId, CancellationToken cancellationToken);
}

public interface ILocalEnvironmentBootstrapper
{
    Task<RequestContext> EnsureInitializedAsync(CancellationToken cancellationToken);
}

public interface IAuthorizationService
{
    Task<IReadOnlySet<string>> GetPermissionsAsync(RequestContext context, CancellationToken cancellationToken);
    Task<bool> HasPermissionAsync(RequestContext context, string permission, CancellationToken cancellationToken);
    Task EnsurePermissionAsync(RequestContext context, string permission, CancellationToken cancellationToken);
}

public interface IIdentityStore
{
    Task<Tenant?> FindTenantByNameAsync(string name, CancellationToken cancellationToken);
    Task<Tenant?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Tenant>> ListTenantsAsync(CancellationToken cancellationToken);
    Task AddTenantAsync(Tenant tenant, CancellationToken cancellationToken);
    Task<Workspace?> FindWorkspaceByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken);
    Task<Workspace?> GetWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken);
    Task<Workspace?> GetWorkspaceAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Workspace>> ListWorkspacesAsync(Guid tenantId, CancellationToken cancellationToken);
    Task AddWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken);
    Task<Principal?> GetPrincipalAsync(Guid principalId, CancellationToken cancellationToken);
    Task AddPrincipalAsync(Principal principal, CancellationToken cancellationToken);
    Task UpdatePrincipalAsync(Principal principal, CancellationToken cancellationToken);
    Task<PrincipalPreferences?> GetPrincipalPreferencesAsync(Guid principalId, CancellationToken cancellationToken);
    Task UpsertPrincipalPreferencesAsync(PrincipalPreferences preferences, CancellationToken cancellationToken);
    Task<ExternalIdentity?> FindExternalIdentityAsync(string issuer, string subject, CancellationToken cancellationToken);
    Task<IReadOnlyList<ExternalIdentity>> ListExternalIdentitiesAsync(Guid principalId, CancellationToken cancellationToken);
    Task AddExternalIdentityAsync(ExternalIdentity externalIdentity, CancellationToken cancellationToken);
    Task RemoveExternalIdentityAsync(Guid principalId, Guid externalIdentityId, CancellationToken cancellationToken);
    Task<LocalIdentity?> FindLocalIdentityAsync(Guid accountId, CancellationToken cancellationToken);
    Task<LocalIdentity?> FindLocalIdentityByPrincipalAsync(Guid principalId, CancellationToken cancellationToken);
    Task<IReadOnlyList<LocalIdentity>> ListLocalIdentitiesAsync(CancellationToken cancellationToken);
    Task AddLocalIdentityAsync(LocalIdentity localIdentity, CancellationToken cancellationToken);
    Task<bool> IsPlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken);
    Task<PlatformAdministrator?> GetPlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PlatformAdministrator>> ListPlatformAdministratorsAsync(CancellationToken cancellationToken);
    Task AddPlatformAdministratorAsync(PlatformAdministrator administrator, CancellationToken cancellationToken);
    Task RemovePlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken);
    Task<TenantMembership?> FindMembershipAsync(Guid tenantId, Guid principalId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantMembership>> ListMembershipsAsync(Guid tenantId, CancellationToken cancellationToken);
    Task AddMembershipAsync(TenantMembership membership, CancellationToken cancellationToken);
    Task<WorkspaceMembership?> FindWorkspaceMembershipAsync(Guid workspaceId, Guid principalId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkspaceMembership>> ListWorkspaceMembershipsAsync(Guid principalId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkspaceMembership>> ListWorkspaceMembersAsync(Guid workspaceId, CancellationToken cancellationToken);
    Task AddWorkspaceMembershipAsync(WorkspaceMembership membership, CancellationToken cancellationToken);
    Task UpdateWorkspaceMembershipAsync(WorkspaceMembership membership, CancellationToken cancellationToken);
    Task RemoveWorkspaceMembershipAsync(Guid membershipId, CancellationToken cancellationToken);
    Task<RoleDefinition?> FindRoleDefinitionByNameAsync(string name, CancellationToken cancellationToken);
    Task<RoleDefinition?> GetRoleDefinitionAsync(Guid roleDefinitionId, CancellationToken cancellationToken);
    Task AddRoleDefinitionAsync(RoleDefinition roleDefinition, CancellationToken cancellationToken);
    Task<IReadOnlyList<RoleAssignment>> ListRoleAssignmentsAsync(Guid tenantId, Guid principalId, CancellationToken cancellationToken);
    Task AddRoleAssignmentAsync(RoleAssignment roleAssignment, CancellationToken cancellationToken);
    Task RemoveRoleAssignmentAsync(Guid roleAssignmentId, CancellationToken cancellationToken);
}

public sealed class AuthorizationDeniedException(string permission)
    : Exception($"The current principal does not have permission '{permission}'.");
