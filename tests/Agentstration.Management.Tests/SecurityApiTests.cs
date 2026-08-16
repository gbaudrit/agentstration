using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
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
    private const string ChangedLocalPassword = "A-changed-local-password-84!";

    [TestMethod]
    public async Task LocalAuthenticationPagesBootstrapAndLoginWithAntiforgery()
    {
        await using var factory = Factory("Local");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        using var loginBeforeInitialization = await client.GetAsync("/login?returnUrl=%2Fagents");
        Assert.AreEqual(HttpStatusCode.Redirect, loginBeforeInitialization.StatusCode);
        Assert.StartsWith("/bootstrap", loginBeforeInitialization.Headers.Location?.OriginalString);

        using var bootstrapPage = await client.GetAsync(loginBeforeInitialization.Headers.Location);
        Assert.AreEqual(HttpStatusCode.OK, bootstrapPage.StatusCode);
        var bootstrapHtml = await bootstrapPage.Content.ReadAsStringAsync();
        StringAssert.Contains(bootstrapHtml, "Initialize Agentstration");
        Assert.IsFalse(bootstrapHtml.Contains(LocalPassword, StringComparison.Ordinal));

        using var missingAntiforgery = await client.PostAsync("/bootstrap", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.DisplayName"] = "Web administrator",
            ["Input.UserName"] = "web-admin",
            ["Input.Password"] = LocalPassword,
            ["Input.ConfirmPassword"] = LocalPassword
        }));
        Assert.AreEqual(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);

        var bootstrapToken = AntiforgeryToken(bootstrapHtml);
        using var initialized = await client.PostAsync("/bootstrap", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = bootstrapToken,
            ["ReturnUrl"] = "/agents",
            ["Input.DisplayName"] = "Web administrator",
            ["Input.UserName"] = "web-admin",
            ["Input.Email"] = "web-admin@example.test",
            ["Input.Password"] = LocalPassword,
            ["Input.ConfirmPassword"] = LocalPassword
        }));
        Assert.AreEqual(HttpStatusCode.Redirect, initialized.StatusCode);
        Assert.AreEqual("/agents", initialized.Headers.Location?.OriginalString);

        using var authorized = await client.GetAsync("/api/agents");
        Assert.AreEqual(HttpStatusCode.OK, authorized.StatusCode);
        using var loggedOut = await client.PostAsync("/api/auth/logout", null);
        Assert.AreEqual(HttpStatusCode.NoContent, loggedOut.StatusCode);

        using var loginPage = await client.GetAsync("/login?returnUrl=%2F%2Fevil.example");
        Assert.AreEqual(HttpStatusCode.OK, loginPage.StatusCode);
        var loginHtml = await loginPage.Content.ReadAsStringAsync();
        var loginToken = AntiforgeryToken(loginHtml);
        using var signedIn = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = loginToken,
            ["ReturnUrl"] = "//evil.example",
            ["Input.UserName"] = "web-admin",
            ["Input.Password"] = LocalPassword
        }));
        Assert.AreEqual(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.AreEqual("/", signedIn.Headers.Location?.OriginalString);

        using var logoutPage = await client.GetAsync("/logout");
        Assert.AreEqual(HttpStatusCode.OK, logoutPage.StatusCode);
        var logoutToken = AntiforgeryToken(await logoutPage.Content.ReadAsStringAsync());
        using var logoutWithoutToken = await client.PostAsync("/logout", new FormUrlEncodedContent([]));
        Assert.AreEqual(HttpStatusCode.BadRequest, logoutWithoutToken.StatusCode);
        using var signedOut = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = logoutToken
        }));
        Assert.AreEqual(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.AreEqual("/login", signedOut.Headers.Location?.OriginalString);
        using var anonymous = await client.GetAsync("/api/agents");
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [TestMethod]
    public async Task LogoutAndAccessDeniedPagesAreAvailableAtTheAuthenticationBoundary()
    {
        await using var factory = Factory("Local");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        using var denied = await client.GetAsync("/access-denied");
        Assert.AreEqual(HttpStatusCode.OK, denied.StatusCode);

        using var logout = await client.GetAsync("/logout");
        Assert.AreEqual(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.AreEqual("/login", logout.Headers.Location?.AbsolutePath);
    }

    [TestMethod]
    public async Task LocalAccountCanChangePasswordAndOtherSessionsAreInvalidated()
    {
        await using var factory = Factory("Local");
        using var currentBrowser = UnredirectedClient(factory);
        using var otherBrowser = UnredirectedClient(factory);
        await BootstrapAsync(currentBrowser, "security-user");
        await LoginAsync(otherBrowser, "security-user", LocalPassword);

        using var securityPage = await currentBrowser.GetAsync("/account/security");
        Assert.AreEqual(HttpStatusCode.OK, securityPage.StatusCode);
        var securityHtml = await securityPage.Content.ReadAsStringAsync();
        StringAssert.Contains(securityHtml, "Change password");
        var token = AntiforgeryToken(securityHtml);

        using var noAntiforgery = await currentBrowser.PostAsync(
            "/account/security?handler=ChangePassword",
            ChangePasswordForm(null, LocalPassword, ChangedLocalPassword));
        Assert.AreEqual(HttpStatusCode.BadRequest, noAntiforgery.StatusCode);

        using var wrongCurrentPassword = await currentBrowser.PostAsync(
            "/account/security?handler=ChangePassword",
            ChangePasswordForm(token, "A-wrong-current-password-42!", ChangedLocalPassword));
        Assert.AreEqual(HttpStatusCode.OK, wrongCurrentPassword.StatusCode);
        StringAssert.Contains(await wrongCurrentPassword.Content.ReadAsStringAsync(), "Incorrect password");

        using var weakPassword = await currentBrowser.PostAsync(
            "/account/security?handler=ChangePassword",
            ChangePasswordForm(token, LocalPassword, "weak"));
        Assert.AreEqual(HttpStatusCode.OK, weakPassword.StatusCode);
        StringAssert.Contains(await weakPassword.Content.ReadAsStringAsync(), "Passwords must be at least 12 characters");

        using var changed = await currentBrowser.PostAsync(
            "/account/security?handler=ChangePassword",
            ChangePasswordForm(token, LocalPassword, ChangedLocalPassword));
        Assert.AreEqual(HttpStatusCode.Redirect, changed.StatusCode);
        Assert.AreEqual("/account/security?status=password-changed", changed.Headers.Location?.OriginalString);

        using var currentSession = await currentBrowser.GetAsync("/api/agents");
        using var invalidatedSession = await otherBrowser.GetAsync("/api/agents");
        Assert.AreEqual(HttpStatusCode.OK, currentSession.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, invalidatedSession.StatusCode);
        var passwordEvents = await currentBrowser.GetFromJsonAsync<SecurityAuditEvent[]>("/api/identity/audit-events");
        Assert.IsNotNull(passwordEvents);
        Assert.IsTrue(passwordEvents.Any(value => value.Action == SecurityAuditActions.LocalPasswordChanged
            && value.Outcome == SecurityAuditOutcome.Succeeded));

        using var logout = await currentBrowser.PostAsync("/api/auth/logout", null);
        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        using var oldPassword = await currentBrowser.PostAsJsonAsync("/api/auth/local/login", new
        {
            userName = "security-user",
            password = LocalPassword
        });
        Assert.AreEqual(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        await LoginAsync(currentBrowser, "security-user", ChangedLocalPassword);
    }

    [TestMethod]
    public async Task LocalAccountCanSignOutOtherSessionsWithoutEndingCurrentSession()
    {
        await using var factory = Factory("Local");
        using var currentBrowser = UnredirectedClient(factory);
        using var otherBrowser = UnredirectedClient(factory);
        await BootstrapAsync(currentBrowser, "session-user");
        await LoginAsync(otherBrowser, "session-user", LocalPassword);

        using var securityPage = await currentBrowser.GetAsync("/account/security");
        var token = AntiforgeryToken(await securityPage.Content.ReadAsStringAsync());
        using var signedOut = await currentBrowser.PostAsync(
            "/account/security?handler=SignOutOtherSessions",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token
            }));
        Assert.AreEqual(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.AreEqual("/account/security?status=sessions-signed-out", signedOut.Headers.Location?.OriginalString);

        using var currentSession = await currentBrowser.GetAsync("/api/agents");
        using var invalidatedSession = await otherBrowser.GetAsync("/api/agents");
        Assert.AreEqual(HttpStatusCode.OK, currentSession.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, invalidatedSession.StatusCode);
        var sessionEvents = await currentBrowser.GetFromJsonAsync<SecurityAuditEvent[]>("/api/identity/audit-events");
        Assert.IsNotNull(sessionEvents);
        Assert.IsTrue(sessionEvents.Any(value => value.Action == SecurityAuditActions.LocalSessionsRevoked
            && value.Outcome == SecurityAuditOutcome.Succeeded));
    }

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
        var auditEvents = await client.GetFromJsonAsync<SecurityAuditEvent[]>("/api/identity/audit-events");
        Assert.IsNotNull(auditEvents);
        Assert.IsTrue(auditEvents.Any(value => value.Action == SecurityAuditActions.LocalAccountDisabled && value.TargetAccountId == account.AccountId));
        Assert.IsTrue(auditEvents.Any(value => value.Action == SecurityAuditActions.LocalAccountEnabled && value.TargetAccountId == account.AccountId));
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

    [TestMethod]
    public async Task SecurityAuditRecordsSensitiveOperationsAndRequiresPlatformAdministrator()
    {
        await using var factory = Factory("Local");
        using var administrator = UnredirectedClient(factory);
        using var memberBrowser = UnredirectedClient(factory);

        using var anonymousAudit = await administrator.GetAsync("/api/identity/audit-events");
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousAudit.StatusCode);
        await BootstrapAsync(administrator, "audit-admin");
        var context = await administrator.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.IsNotNull(context);

        using var created = await administrator.PostAsJsonAsync("/api/identity/accounts/", new
        {
            userName = "audit-member",
            password = LocalPassword,
            displayName = "Audit member",
            email = "audit-member@example.test",
            workspaceId = context.Context.WorkspaceId,
            role = BuiltInIdentityRoles.Admin
        });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var account = await created.Content.ReadFromJsonAsync<LocalAccountView>();
        Assert.IsNotNull(account);

        using var failedLogin = await memberBrowser.PostAsJsonAsync("/api/auth/local/login", new
        {
            userName = "audit-member",
            password = "Not-the-right-password-42!"
        });
        Assert.AreEqual(HttpStatusCode.Unauthorized, failedLogin.StatusCode);
        await LoginAsync(memberBrowser, "audit-member", LocalPassword);

        using var deniedAudit = await memberBrowser.GetAsync("/api/identity/audit-events");
        Assert.AreEqual(HttpStatusCode.Forbidden, deniedAudit.StatusCode);

        using var removed = await administrator.DeleteAsync(
            $"/api/identity/workspaces/{context.Context.WorkspaceId:D}/memberships/{account.PrincipalId:D}");
        Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode);

        using var auditResponse = await administrator.GetAsync("/api/identity/audit-events?limit=200");
        Assert.AreEqual(HttpStatusCode.OK, auditResponse.StatusCode);
        var events = await auditResponse.Content.ReadFromJsonAsync<SecurityAuditEvent[]>();
        Assert.IsNotNull(events);
        Assert.IsTrue(events.Any(value => value.Action == SecurityAuditActions.InstanceBootstrapped));
        Assert.IsTrue(events.Any(value => value.Action == SecurityAuditActions.PlatformAdministratorGranted));
        Assert.IsTrue(events.Any(value => value.Action == SecurityAuditActions.LocalAccountCreated
            && value.TargetPrincipalId == account.PrincipalId && value.ActorPrincipalId == context.Context.PrincipalId));
        Assert.IsTrue(events.Any(value => value.Action == SecurityAuditActions.WorkspaceMembershipSet
            && value.TargetPrincipalId == account.PrincipalId && value.WorkspaceId == context.Context.WorkspaceId));
        Assert.IsTrue(events.Any(value => value.Action == SecurityAuditActions.LocalLogin
            && value.Outcome == SecurityAuditOutcome.Failed && value.ReasonCode == "invalid-credentials"));
        Assert.IsTrue(events.Any(value => value.Action == SecurityAuditActions.LocalLogin
            && value.Outcome == SecurityAuditOutcome.Succeeded && value.ActorPrincipalId == account.PrincipalId));
        Assert.IsTrue(events.Any(value => value.Action == SecurityAuditActions.WorkspaceMembershipRemoved
            && value.TargetPrincipalId == account.PrincipalId && value.WorkspaceId == context.Context.WorkspaceId));

        var json = await auditResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("audit-member@example.test", json, StringComparison.Ordinal);
        Assert.DoesNotContain(LocalPassword, json, StringComparison.Ordinal);

        using var auditPage = await administrator.GetAsync("/settings/organization/security-audit");
        Assert.AreEqual(HttpStatusCode.OK, auditPage.StatusCode);
        StringAssert.Contains(await auditPage.Content.ReadAsStringAsync(), "Security audit");
    }

    private static WebApplicationFactory<Program> Factory(string mode) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Authentication:Mode", mode);
        });

    private static HttpClient UnredirectedClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private static async Task BootstrapAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            userName,
            password = LocalPassword,
            displayName = "Security test user"
        });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task LoginAsync(HttpClient client, string userName, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/local/login", new { userName, password });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static FormUrlEncodedContent ChangePasswordForm(
        string? antiforgeryToken,
        string currentPassword,
        string newPassword)
    {
        var values = new Dictionary<string, string>
        {
            ["Input.CurrentPassword"] = currentPassword,
            ["Input.NewPassword"] = newPassword,
            ["Input.ConfirmPassword"] = newPassword
        };
        if (antiforgeryToken is not null) values["__RequestVerificationToken"] = antiforgeryToken;
        return new FormUrlEncodedContent(values);
    }

    private static string AntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.IsTrue(match.Success, "The rendered form must contain an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private sealed record BootstrapStatus(bool Initialized);
}
