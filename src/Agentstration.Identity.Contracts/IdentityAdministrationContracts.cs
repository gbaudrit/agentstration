using System.Text.Json.Serialization;

namespace Agentstration.Identity.Contracts;

public enum IdentityConsoleContextKind
{
    Instance,
    Tenant,
    Workspace
}

public sealed record IdentityConsoleContextOptionResponse(
    string ScopeRef,
    IdentityConsoleContextKind Kind,
    Guid? TenantId,
    string? TenantName,
    string? TenantDisplayName,
    Guid? WorkspaceId,
    string? WorkspaceName,
    string DisplayName);

public sealed record IdentityConsoleWorkspaceResponse(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string TenantDisplayName,
    string Name,
    string DisplayName,
    WorkspaceStatus Status,
    IReadOnlyList<string> Permissions);

[method: JsonConstructor]
public sealed record IdentityConsoleContextResponse(
    RequestContext Context,
    string SelectedScopeRef,
    IdentityConsoleContextKind SelectedKind,
    string UserDisplayName,
    string TenantName,
    string TenantDisplayName,
    string WorkspaceName,
    string WorkspaceDisplayName,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<IdentityConsoleWorkspaceResponse> AvailableWorkspaces,
    IReadOnlyList<IdentityConsoleContextOptionResponse> AvailableContexts)
{
    public IdentityConsoleContextResponse(
        RequestContext context,
        string userDisplayName,
        string tenantName,
        string tenantDisplayName,
        string workspaceName,
        string workspaceDisplayName,
        IReadOnlyList<string> permissions,
        IReadOnlyList<IdentityConsoleWorkspaceResponse> availableWorkspaces)
        : this(context, $"/workspaces/{context.WorkspaceId:D}", IdentityConsoleContextKind.Workspace,
            userDisplayName, tenantName, tenantDisplayName, workspaceName, workspaceDisplayName,
            permissions, availableWorkspaces,
            [new($"/workspaces/{context.WorkspaceId:D}", IdentityConsoleContextKind.Workspace,
                context.TenantId, tenantName, tenantDisplayName, context.WorkspaceId, workspaceName,
                workspaceDisplayName)])
    {
    }
}

public sealed record OrganizationAdministrationResponse(
    Tenant Tenant,
    IReadOnlyList<Workspace> Workspaces,
    IReadOnlyList<OrganizationMemberResponse> Members);

public sealed record OrganizationMemberResponse(
    Principal Principal,
    TenantMembership Membership,
    IReadOnlyList<ExternalIdentity> ExternalIdentities,
    IReadOnlyList<AssignedRoleResponse> Roles);

public sealed record AssignedRoleResponse(RoleDefinition Role, string Scope);

public sealed record WorkspaceMemberResponse(
    Principal Principal,
    WorkspaceMembership Membership,
    string? Role,
    bool Inherited);

public sealed record PlatformAdministratorResponse(Principal Principal, PlatformAdministrator Grant);

public sealed record IdentityAdministrationCapabilitiesResponse(
    Guid PrincipalId,
    Guid WorkspaceId,
    bool IsPlatformAdministrator,
    bool SupportsLocalAccounts);

public sealed record LocalAccountResponse(
    Guid AccountId,
    Guid PrincipalId,
    string UserName,
    string DisplayName,
    string? Email,
    PrincipalStatus PrincipalStatus,
    int AccessFailedCount,
    DateTimeOffset? LockoutEnd,
    bool PlatformAdministrator);

public sealed record CreateWorkspaceRequest(string Name, string DisplayName);
public sealed record SelectWorkspaceRequest(Guid WorkspaceId);
public sealed record SelectConsoleContextRequest(string ScopeRef);
public sealed record SelectConsoleContextResponse(
    string ScopeRef,
    IdentityConsoleContextKind Kind,
    Guid OperationalWorkspaceId);
public sealed record SetWorkspaceMembershipRequest(string Role);
public sealed record LinkExternalIdentityRequest(string Issuer, string Subject);
public sealed record CreateLocalAccountRequest(
    string UserName,
    string Password,
    string DisplayName,
    string? Email,
    Guid WorkspaceId,
    string Role);
public sealed record SetLocalAccountStatusRequest(bool Enabled);

public static class BuiltInIdentityRoleNames
{
    public const string Owner = "Owner";
    public const string Admin = "Admin";
    public const string Member = "Member";
    public const string Viewer = "Viewer";
    public static IReadOnlyList<string> All { get; } = [Owner, Admin, Member, Viewer];
}
