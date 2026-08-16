using System.Net;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Security.AspNetCoreIdentity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SecurityApiTests
{
    private const string LocalPassword = "A-strong-local-password-42!";

    [TestMethod]
    public async Task ProtectedAgentEndpointReturns401WithoutAuthentication()
    {
        await using var factory = Factory("Disabled");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/agents");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task ProtectedAgentEndpointAllowsAuthorizedPrincipalAndRejectsMissingPermission()
    {
        await using var factory = Factory("Development");
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<IIdentityStore>();
        var context = factory.Services.GetRequiredService<CurrentRequestContext>().Current;

        using var allowed = await client.GetAsync("/api/agents");
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);

        foreach (var assignment in await store.ListRoleAssignmentsAsync(context.TenantId, context.PrincipalId, default))
            await store.RemoveRoleAssignmentAsync(assignment.Id, default);
        using var denied = await client.GetAsync("/api/agents");
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [TestMethod]
    public async Task WorkspacePolicyIsResourceBasedAndDoesNotCrossWorkspaceBoundary()
    {
        await using var factory = Factory("Development");
        using var client = factory.CreateClient();
        var context = factory.Services.GetRequiredService<CurrentRequestContext>().Current;
        var store = factory.Services.GetRequiredService<IIdentityStore>();

        using var createdResponse = await client.PostAsJsonAsync("/api/identity/workspaces", new { name = "workspace-b", displayName = "Workspace B" });
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        var workspaceB = await createdResponse.Content.ReadFromJsonAsync<Workspace>();
        Assert.IsNotNull(workspaceB);

        foreach (var assignment in await store.ListRoleAssignmentsAsync(context.TenantId, context.PrincipalId, default))
            await store.RemoveRoleAssignmentAsync(assignment.Id, default);
        var reader = new RoleDefinition(Guid.NewGuid(), "Security-test-reader", "Security test reader", [AuthorizationPermissions.WorkspacesRead], false);
        await store.AddRoleDefinitionAsync(reader, default);
        await store.AddRoleAssignmentAsync(new RoleAssignment(Guid.NewGuid(), context.TenantId, context.PrincipalId, PrincipalType.User, reader.Id, AuthorizationScopes.Workspace(context.WorkspaceId)), default);

        using var ownWorkspace = await client.GetAsync($"/api/identity/workspaces/{context.WorkspaceId:D}");
        using var otherWorkspace = await client.GetAsync($"/api/identity/workspaces/{workspaceB.Id:D}");

        Assert.AreEqual(HttpStatusCode.OK, ownWorkspace.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, otherWorkspace.StatusCode);
    }

    [TestMethod]
    public async Task LocalBootstrapCreatesIdentityPrincipalAndOneTimeAdministrator()
    {
        await using var factory = Factory("Local");
        using var client = factory.CreateClient();

        var initial = await client.GetFromJsonAsync<BootstrapStatus>("/api/auth/bootstrap");
        Assert.IsNotNull(initial);
        Assert.IsFalse(initial.Initialized);

        using var created = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            userName = "local-admin",
            password = LocalPassword,
            displayName = "Local administrator",
            email = "admin-before@example.test"
        });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

        var initialized = await client.GetFromJsonAsync<BootstrapStatus>("/api/auth/bootstrap");
        Assert.IsNotNull(initialized);
        Assert.IsTrue(initialized.Initialized);
        using var repeated = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            userName = "second-admin",
            password = LocalPassword,
            displayName = "Second administrator"
        });
        Assert.AreEqual(HttpStatusCode.Conflict, repeated.StatusCode);

        using var agents = await client.GetAsync("/api/agents");
        using var platform = await client.GetAsync("/api/identity/platform");
        Assert.AreEqual(HttpStatusCode.OK, agents.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, platform.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalIdentityUser>>();
        var account = await users.FindByNameAsync("local-admin");
        Assert.IsNotNull(account);
        Assert.IsNotNull(account.PasswordHash);
        Assert.AreNotEqual(LocalPassword, account.PasswordHash);
        var resolver = scope.ServiceProvider.GetRequiredService<IPrincipalResolver>();
        var principal = await resolver.ResolveLocalAsync(account.Id, default);
        Assert.IsNotNull(principal);
        Assert.AreEqual("admin-before@example.test", principal.Email);
        Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<IPlatformAuthorizationService>()
            .IsPlatformAdministratorAsync(principal.Id, default));

        using var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        using var unauthenticated = await client.GetAsync("/api/agents");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var login = await client.PostAsJsonAsync("/api/auth/local/login", new
        {
            userName = "local-admin",
            password = LocalPassword
        });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        using var authenticated = await client.GetAsync("/api/agents");
        Assert.AreEqual(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [TestMethod]
    public async Task WorkspaceOwnerIsNotImplicitlyPlatformAdministrator()
    {
        await using var factory = Factory("Local");
        using var client = factory.CreateClient();
        using var bootstrapped = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            userName = "platform-admin",
            password = LocalPassword,
            displayName = "Platform administrator"
        });
        Assert.AreEqual(HttpStatusCode.Created, bootstrapped.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalIdentityUser>>();
            var workspaceOwner = new LocalIdentityUser { Id = Guid.NewGuid(), UserName = "workspace-owner" };
            var result = await users.CreateAsync(workspaceOwner, LocalPassword);
            Assert.IsTrue(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));

            var store = scope.ServiceProvider.GetRequiredService<IIdentityStore>();
            var tenant = (await store.ListTenantsAsync(default)).Single();
            var workspace = (await store.ListWorkspacesAsync(tenant.Id, default)).Single();
            var owner = await store.FindRoleDefinitionByNameAsync("Owner", default);
            Assert.IsNotNull(owner);
            var principal = new Principal(Guid.NewGuid(), PrincipalKind.Human, "Workspace owner", null, PrincipalStatus.Active, DateTimeOffset.UtcNow);
            await store.AddPrincipalAsync(principal, default);
            await store.AddLocalIdentityAsync(new LocalIdentity(workspaceOwner.Id, principal.Id, DateTimeOffset.UtcNow), default);
            await store.AddMembershipAsync(new TenantMembership(Guid.NewGuid(), tenant.Id, principal.Id, MembershipStatus.Active, DateTimeOffset.UtcNow), default);
            await store.AddWorkspaceMembershipAsync(new WorkspaceMembership(Guid.NewGuid(), workspace.Id, principal.Id, MembershipStatus.Active, DateTimeOffset.UtcNow), default);
            await store.AddRoleAssignmentAsync(new RoleAssignment(Guid.NewGuid(), tenant.Id, principal.Id, PrincipalType.User, owner.Id, AuthorizationScopes.Workspace(workspace.Id)), default);
        }

        using var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/local/login", new
        {
            userName = "workspace-owner",
            password = LocalPassword
        });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);

        using var workspaceAccess = await client.GetAsync("/api/agents");
        using var platformAccess = await client.GetAsync("/api/identity/platform");
        Assert.AreEqual(HttpStatusCode.OK, workspaceAccess.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, platformAccess.StatusCode);
    }

    [TestMethod]
    public async Task PlatformAdministratorCanCreateListDisableAndEnableLocalAccounts()
    {
        await using var factory = Factory("Local");
        using var client = factory.CreateClient();
        using var bootstrap = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            userName = "account-admin",
            password = LocalPassword,
            displayName = "Account administrator"
        });
        Assert.AreEqual(HttpStatusCode.Created, bootstrap.StatusCode);
        var context = await client.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.IsNotNull(context);

        using var created = await client.PostAsJsonAsync("/api/identity/accounts/", new
        {
            userName = "local-member",
            password = LocalPassword,
            displayName = "Local member",
            email = "member@example.test",
            workspaceId = context.Context.WorkspaceId,
            role = BuiltInIdentityRoles.Member
        });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var account = await created.Content.ReadFromJsonAsync<LocalAccountView>();
        Assert.IsNotNull(account);
        Assert.AreEqual(PrincipalStatus.Active, account.PrincipalStatus);
        Assert.IsFalse(account.PlatformAdministrator);

        var accounts = await client.GetFromJsonAsync<LocalAccountView[]>("/api/identity/accounts/");
        Assert.IsNotNull(accounts);
        Assert.HasCount(2, accounts);

        using var disabled = await client.PutAsJsonAsync($"/api/identity/accounts/{account.AccountId:D}/status", new { enabled = false });
        Assert.AreEqual(HttpStatusCode.OK, disabled.StatusCode);
        using var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        using var deniedLogin = await client.PostAsJsonAsync("/api/auth/local/login", new { userName = "local-member", password = LocalPassword });
        Assert.AreEqual(HttpStatusCode.Locked, deniedLogin.StatusCode);

        using var adminLogin = await client.PostAsJsonAsync("/api/auth/local/login", new { userName = "account-admin", password = LocalPassword });
        Assert.AreEqual(HttpStatusCode.NoContent, adminLogin.StatusCode);
        using var enabled = await client.PutAsJsonAsync($"/api/identity/accounts/{account.AccountId:D}/status", new { enabled = true });
        Assert.AreEqual(HttpStatusCode.OK, enabled.StatusCode);
        using var protectPlatformAdmin = await client.PutAsJsonAsync($"/api/identity/accounts/{accounts.Single(value => value.PlatformAdministrator).AccountId:D}/status", new { enabled = false });
        Assert.AreEqual(HttpStatusCode.Conflict, protectPlatformAdmin.StatusCode);
    }

    [TestMethod]
    public async Task WorkspaceMembershipAdministrationAssignsRolesAndProtectsLastOwner()
    {
        await using var factory = Factory("Local");
        using var client = factory.CreateClient();
        using var bootstrap = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            userName = "membership-admin",
            password = LocalPassword,
            displayName = "Membership administrator"
        });
        Assert.AreEqual(HttpStatusCode.Created, bootstrap.StatusCode);
        var context = await client.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.IsNotNull(context);

        using var created = await client.PostAsJsonAsync("/api/identity/accounts/", new
        {
            userName = "workspace-viewer",
            password = LocalPassword,
            displayName = "Workspace viewer",
            workspaceId = context.Context.WorkspaceId,
            role = BuiltInIdentityRoles.Viewer
        });
        var account = await created.Content.ReadFromJsonAsync<LocalAccountView>();
        Assert.IsNotNull(account);

        using var promoted = await client.PutAsJsonAsync(
            $"/api/identity/workspaces/{context.Context.WorkspaceId:D}/memberships/{account.PrincipalId:D}",
            new { role = BuiltInIdentityRoles.Admin });
        Assert.AreEqual(HttpStatusCode.OK, promoted.StatusCode);
        var memberships = await client.GetFromJsonAsync<WorkspaceMemberView[]>(
            $"/api/identity/workspaces/{context.Context.WorkspaceId:D}/memberships");
        Assert.IsNotNull(memberships);
        Assert.AreEqual(BuiltInIdentityRoles.Admin, memberships.Single(value => value.Principal.Id == account.PrincipalId).Role);

        using var removeLastOwner = await client.DeleteAsync(
            $"/api/identity/workspaces/{context.Context.WorkspaceId:D}/memberships/{context.Context.PrincipalId:D}");
        Assert.AreEqual(HttpStatusCode.Conflict, removeLastOwner.StatusCode);

        using var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/local/login", new { userName = "workspace-viewer", password = LocalPassword });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        using var workspaceAdminAllowed = await client.GetAsync(
            $"/api/identity/workspaces/{context.Context.WorkspaceId:D}/memberships");
        using var platformAdminDenied = await client.GetAsync("/api/identity/accounts/");
        Assert.AreEqual(HttpStatusCode.OK, workspaceAdminAllowed.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, platformAdminDenied.StatusCode);
    }

    private static WebApplicationFactory<Program> Factory(string mode) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Authentication:Mode", mode);
        });

    private sealed record BootstrapStatus(bool Initialized);
}
