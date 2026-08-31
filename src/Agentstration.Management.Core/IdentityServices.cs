using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class LocalBootstrapOptions
{
    public const string DevelopmentIssuer = "https://agentstration.local/development";
    public const string DevelopmentSubject = "local-operator";
    public string TenantName { get; set; } = "dev";
    public string TenantDisplayName { get; set; } = "Development";
    public string WorkspaceName { get; set; } = "default";
    public string WorkspaceDisplayName { get; set; } = "Default workspace";
    public string PrincipalDisplayName { get; set; } = "Development operator";
    public string ExternalIdentityIssuer { get; set; } = DevelopmentIssuer;
    public string ExternalIdentitySubject { get; set; } = DevelopmentSubject;
}
public sealed class CurrentRequestContext : ICurrentRequestContext, IRequestContextScopeFactory
{
    private readonly AsyncLocal<AmbientRequestContext?> ambient = new();
    public bool IsInitialized => AccessMode == ControlPlaneAccessMode.Workspace;
    public ControlPlaneAccessMode AccessMode => ambient.Value?.AccessMode ?? ControlPlaneAccessMode.Unavailable;
    public RequestContext Current => ambient.Value switch
    {
        { AccessMode: ControlPlaneAccessMode.Workspace, Context: not null } value => value.Context,
        { AccessMode: ControlPlaneAccessMode.System } => throw new InvalidOperationException("System operations do not have a workspace request context."),
        _ => throw new InvalidOperationException("The request context has not been initialized.")
    };
    public IDisposable Push(RequestContext context) => Push(new AmbientRequestContext(ControlPlaneAccessMode.Workspace, context));
    public IDisposable PushSystem() => Push(new AmbientRequestContext(ControlPlaneAccessMode.System, null));

    private IDisposable Push(AmbientRequestContext context)
    {
        var previous = ambient.Value;
        ambient.Value = context;
        return new Scope(() => ambient.Value = previous);
    }

    private sealed record AmbientRequestContext(ControlPlaneAccessMode AccessMode, RequestContext? Context);

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? disposeAction = dispose;
        public void Dispose() => Interlocked.Exchange(ref disposeAction, null)?.Invoke();
    }
}

public sealed class ExternalIdentityPrincipalResolver(IIdentityStore store) : IPrincipalResolver
{
    public async Task<Principal?> ResolveAsync(string issuer, string subject, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject)) return null;
        var identity = await store.FindExternalIdentityAsync(issuer, subject, cancellationToken);
        return identity is null ? null : await store.GetPrincipalAsync(identity.PrincipalId, cancellationToken);
    }

    public async Task<Principal?> ResolveLocalAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var identity = await store.FindLocalIdentityAsync(accountId, cancellationToken);
        return identity is null ? null : await store.GetPrincipalAsync(identity.PrincipalId, cancellationToken);
    }
}

public sealed class InitialPrincipalProvisioner(
    IIdentityStore store,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider) : IInitialPrincipalProvisioner
{
    public async Task<InitialPrincipalProvisioningResult> ProvisionAsync(InitialPrincipalProvisioning request, CancellationToken cancellationToken)
    {
        if (await store.FindLocalIdentityAsync(request.AccountId, cancellationToken) is not null)
            throw new InvalidOperationException("The local account is already linked to an Agentstration principal.");

        var now = timeProvider.GetUtcNow();
        var principal = new Principal(request.PrincipalId, PrincipalKind.Human, request.DisplayName, request.Email, PrincipalStatus.Active, now);
        await store.AddPrincipalAsync(principal, cancellationToken);
        await store.AddLocalIdentityAsync(new LocalIdentity(request.AccountId, principal.Id, now), cancellationToken);
        await BuiltInIdentityRoles.EnsureAsync(store, cancellationToken);
        await store.AddPlatformAdministratorAsync(new PlatformAdministrator(principal.Id, now), cancellationToken);
        await audit.WriteAsync(new(
            SecurityAuditActions.PlatformAdministratorGranted,
            ActorPrincipalId: principal.Id,
            TargetPrincipalId: principal.Id,
            TargetAccountId: request.AccountId), cancellationToken);
        return new InitialPrincipalProvisioningResult(principal);
    }
}

public sealed class InitialTopologyProvisioner(
    IIdentityStore store,
    TimeProvider timeProvider) : IInitialTopologyProvisioner
{
    public async Task<InitialTopologyProvisioningResult> ProvisionAsync(
        InitialTopologyProvisioning request,
        CancellationToken cancellationToken)
    {
        var principal = await store.GetPrincipalAsync(request.PrincipalId, cancellationToken)
            ?? throw new InvalidOperationException("The initial Principal does not exist.");
        if (!await store.IsPlatformAdministratorAsync(principal.Id, cancellationToken))
            throw new InvalidOperationException("The initial Principal is not a Platform administrator.");

        var tenantName = IdentityBootstrapValidation.Name(request.TenantName, "Tenant");
        var tenantDisplayName = IdentityBootstrapValidation.DisplayName(request.TenantDisplayName, "Tenant");
        var workspaceName = IdentityBootstrapValidation.Name(request.WorkspaceName, "Workspace");
        var workspaceDisplayName = IdentityBootstrapValidation.DisplayName(request.WorkspaceDisplayName, "Workspace");
        var now = timeProvider.GetUtcNow();

        var tenant = await store.FindTenantByNameAsync(tenantName, cancellationToken);
        if (tenant is null)
        {
            tenant = new Tenant(Guid.NewGuid(), tenantName, tenantDisplayName, TenantStatus.Active, now);
            await store.AddTenantAsync(tenant, cancellationToken);
        }

        var workspace = await store.FindWorkspaceByNameAsync(tenant.Id, workspaceName, cancellationToken);
        if (workspace is null)
        {
            workspace = new Workspace(Guid.NewGuid(), tenant.Id, workspaceName, workspaceDisplayName, WorkspaceStatus.Active, now);
            await store.AddWorkspaceAsync(workspace, cancellationToken);
        }

        var preferences = await store.GetPrincipalPreferencesAsync(principal.Id, cancellationToken)
            ?? new PrincipalPreferences(principal.Id, ThemePreference.System, now);
        await store.UpsertPrincipalPreferencesAsync(preferences with
        {
            DefaultTenantId = tenant.Id,
            DefaultWorkspaceId = workspace.Id,
            UpdatedAt = now
        }, cancellationToken);
        return new InitialTopologyProvisioningResult(tenant, workspace);
    }
}

internal static class IdentityBootstrapValidation
{
    public static string Name(string value, string resource)
    {
        value = value.Trim().ToLowerInvariant();
        if (value.Length is < 2 or > 64
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ArgumentException($"{resource} names must contain 2-64 lowercase letters, digits, or hyphens.");
        return value;
    }

    public static string DisplayName(string value, string resource)
    {
        value = value.Trim();
        if (value.Length is < 2 or > 120)
            throw new ArgumentException($"{resource} display names must contain 2-120 characters.");
        return value;
    }
}

public static class BuiltInIdentityRoles
{
    public const string Owner = "Owner";
    public const string Admin = "Admin";
    public const string Member = "Member";
    public const string Viewer = "Viewer";

    public static readonly IReadOnlyList<string> Names = [Owner, Admin, Member, Viewer];

    private static readonly IReadOnlyList<RoleDefinition> Definitions =
    [
        new(LocalEnvironmentBootstrapper.OwnerRoleId, Owner, Owner, AuthorizationPermissions.All, true),
        new(new Guid("ef263a5e-1f83-4882-bef4-b76847574fe2"), Admin, Admin,
            [AuthorizationPermissions.TenantsRead, AuthorizationPermissions.WorkspacesRead, AuthorizationPermissions.WorkspacesWrite,
             AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite, AuthorizationPermissions.ResourcesDelete,
             AuthorizationPermissions.RunsRead, AuthorizationPermissions.RunsExecute, AuthorizationPermissions.AuthorizationRead,
             AuthorizationPermissions.AuthorizationWrite], true),
        new(new Guid("2c0b9724-f78f-43db-b0b6-673c04dc68a4"), Member, Member,
            [AuthorizationPermissions.WorkspacesRead, AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.RunsRead, AuthorizationPermissions.RunsExecute], true),
        new(new Guid("8bb015ea-acda-4770-8d7a-0399e1d28ab4"), Viewer, Viewer,
            [AuthorizationPermissions.WorkspacesRead, AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.RunsRead], true)
    ];

    public static async Task EnsureAsync(IIdentityStore store, CancellationToken cancellationToken)
    {
        foreach (var definition in Definitions)
            if (await store.FindRoleDefinitionByNameAsync(definition.Name, cancellationToken) is null)
                await store.AddRoleDefinitionAsync(definition, cancellationToken);
    }
}

public sealed class LocalPrincipalProvisioner(
    IIdentityStore store,
    ICurrentRequestContext requestContext,
    IPlatformAuthorizationService platformAuthorization,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider) : ILocalPrincipalProvisioner
{
    public async Task<Principal> ProvisionAsync(LocalPrincipalProvisioning request, CancellationToken cancellationToken)
    {
        if (!requestContext.IsInitialized || !await platformAuthorization.IsPlatformAdministratorAsync(requestContext.Current.PrincipalId, cancellationToken))
            throw new AuthorizationDeniedException("platform/admin");
        if (await store.FindLocalIdentityAsync(request.AccountId, cancellationToken) is not null)
            throw new InvalidOperationException("The local account is already linked.");
        var workspace = await store.GetWorkspaceAsync(request.WorkspaceId, cancellationToken)
            ?? throw new ArgumentException("The target Workspace does not exist.", nameof(request));
        if (workspace.Status != WorkspaceStatus.Active) throw new ArgumentException("The target Workspace is disabled.", nameof(request));
        await BuiltInIdentityRoles.EnsureAsync(store, cancellationToken);
        var role = await store.FindRoleDefinitionByNameAsync(request.Role, cancellationToken);
        if (role is null || !BuiltInIdentityRoles.Names.Contains(role.Name, StringComparer.Ordinal))
            throw new ArgumentException("The requested role is not a supported built-in Workspace role.", nameof(request));

        var now = timeProvider.GetUtcNow();
        var principal = new Principal(request.PrincipalId, PrincipalKind.Human, request.DisplayName, request.Email, PrincipalStatus.Active, now);
        await store.AddPrincipalAsync(principal, cancellationToken);
        if (await store.FindMembershipAsync(workspace.TenantId, principal.Id, cancellationToken) is null)
            await store.AddMembershipAsync(new TenantMembership(Guid.NewGuid(), workspace.TenantId, principal.Id, MembershipStatus.Active, now), cancellationToken);
        await store.AddWorkspaceMembershipAsync(new WorkspaceMembership(Guid.NewGuid(), workspace.Id, principal.Id, MembershipStatus.Active, now), cancellationToken);
        await store.AddRoleAssignmentAsync(new RoleAssignment(Guid.NewGuid(), workspace.TenantId, principal.Id, PrincipalType.User, role.Id, AuthorizationScopes.Workspace(workspace.Id)), cancellationToken);
        await store.AddLocalIdentityAsync(new LocalIdentity(request.AccountId, principal.Id, now), cancellationToken);
        await audit.WriteAsync(new(
            SecurityAuditActions.WorkspaceMembershipSet,
            TargetPrincipalId: principal.Id,
            TargetAccountId: request.AccountId,
            TenantId: workspace.TenantId,
            WorkspaceId: workspace.Id,
            ReasonCode: $"role-{role.Name.ToLowerInvariant()}"), cancellationToken);
        return principal;
    }
}
