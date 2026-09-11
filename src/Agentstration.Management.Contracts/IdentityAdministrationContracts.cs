using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Contracts;

public sealed record IdentityConsoleWorkspaceResponse(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string TenantDisplayName,
    string Name,
    string DisplayName,
    WorkspaceStatus Status,
    IReadOnlyList<string> Permissions);

public sealed record IdentityConsoleContextResponse(
    RequestContext Context,
    string UserDisplayName,
    string TenantName,
    string TenantDisplayName,
    string WorkspaceName,
    string WorkspaceDisplayName,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<IdentityConsoleWorkspaceResponse> AvailableWorkspaces);

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

public sealed record AepEnrollmentSettingsSnapshot(
    bool PairingCodeEnabled,
    bool SharedKeyFileEnabled,
    bool PairingCodeConfigurable,
    bool SharedKeyFileConfigurable,
    string? ETag);

public sealed record AepPairingCodeResult(Guid RequestId, string Code, DateTimeOffset ExpiresAt);
