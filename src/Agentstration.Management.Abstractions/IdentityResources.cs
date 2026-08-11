namespace Agentstration.Management.Abstractions;

public enum TenantStatus { Active, Disabled }
public enum WorkspaceStatus { Active, Disabled }
public enum UserStatus { Active, Disabled }
public enum MembershipStatus { Active, Suspended }
public enum PrincipalType { User, Group, ServicePrincipal }

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

public sealed record ResourceGroup(
    Guid Id,
    Guid TenantId,
    Guid WorkspaceId,
    string Name,
    DateTimeOffset CreatedAt);

public sealed record User(
    Guid Id,
    string? ExternalSubject,
    string DisplayName,
    string? Email,
    UserStatus Status,
    DateTimeOffset CreatedAt);

public sealed record TenantMembership(
    Guid Id,
    Guid TenantId,
    Guid UserId,
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
    public const string AuthorizationRead = "authorization/read";
    public const string AuthorizationWrite = "authorization/write";

    public static readonly IReadOnlyCollection<string> All =
    [
        TenantsRead, TenantsManage, WorkspacesRead, WorkspacesWrite, WorkspacesDelete,
        ResourcesRead, ResourcesWrite, ResourcesDelete, RunsRead, RunsExecute,
        AuthorizationRead, AuthorizationWrite
    ];
}

public static class AuthorizationScopes
{
    public static string Tenant(Guid tenantId) => $"/tenants/{tenantId:D}";
    public static string Workspace(Guid workspaceId) => $"/workspaces/{workspaceId:D}";
}

public sealed record RequestContext(Guid UserId, Guid TenantId, Guid WorkspaceId);

public interface ICurrentRequestContext
{
    bool IsInitialized { get; }
    RequestContext Current { get; }
}

public interface IRequestContextInitializer
{
    void Initialize(RequestContext context);
}

public interface IRequestContextScopeFactory
{
    IDisposable Push(RequestContext context);
}

public interface IIdentityProvider
{
    Task<User> ResolveCurrentUserAsync(CancellationToken cancellationToken);
}

public interface ILocalEnvironmentBootstrapper
{
    Task EnsureInitializedAsync(CancellationToken cancellationToken);
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
    Task<Workspace?> GetWorkspaceAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Workspace>> ListWorkspacesAsync(Guid tenantId, CancellationToken cancellationToken);
    Task AddWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken);
    Task<ResourceGroup?> FindResourceGroupAsync(Guid tenantId, Guid workspaceId, string name, CancellationToken cancellationToken);
    Task AddResourceGroupAsync(ResourceGroup resourceGroup, CancellationToken cancellationToken);
    Task<User?> FindUserByExternalSubjectAsync(string externalSubject, CancellationToken cancellationToken);
    Task<User?> GetUserAsync(Guid userId, CancellationToken cancellationToken);
    Task AddUserAsync(User user, CancellationToken cancellationToken);
    Task<TenantMembership?> FindMembershipAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantMembership>> ListMembershipsAsync(Guid tenantId, CancellationToken cancellationToken);
    Task AddMembershipAsync(TenantMembership membership, CancellationToken cancellationToken);
    Task<RoleDefinition?> FindRoleDefinitionByNameAsync(string name, CancellationToken cancellationToken);
    Task<RoleDefinition?> GetRoleDefinitionAsync(Guid roleDefinitionId, CancellationToken cancellationToken);
    Task AddRoleDefinitionAsync(RoleDefinition roleDefinition, CancellationToken cancellationToken);
    Task<IReadOnlyList<RoleAssignment>> ListRoleAssignmentsAsync(Guid tenantId, Guid principalId, CancellationToken cancellationToken);
    Task AddRoleAssignmentAsync(RoleAssignment roleAssignment, CancellationToken cancellationToken);
}

public interface IResourceScopeMigrator
{
    Task BackfillUnscopedResourcesAsync(Guid tenantId, Guid workspaceId, Guid resourceGroupId, CancellationToken cancellationToken);
}

public sealed class AuthorizationDeniedException(string permission)
    : Exception($"The current principal does not have permission '{permission}'.");
