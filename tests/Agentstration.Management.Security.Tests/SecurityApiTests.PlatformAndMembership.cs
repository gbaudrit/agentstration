using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Security.AspNetCoreIdentity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class SecurityApiTests
{
    [TestMethod]
    public async Task PlatformAdministratorCanSelectAWorkspaceCreatedLaterWithoutMembership()
    {
        await using var factory = Factory("Local");
        using var client = UnredirectedClient(factory);
        await BootstrapAsync(client, "global-admin");
        var initial = await client.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.IsNotNull(initial);

        var store = factory.Services.GetRequiredService<IIdentityStore>();
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant(Guid.NewGuid(), "second", "Second tenant", TenantStatus.Active, now);
        var workspace = new Workspace(Guid.NewGuid(), tenant.Id, "operations", "Operations", WorkspaceStatus.Active, now);
        await store.AddTenantAsync(tenant, default);
        await store.AddWorkspaceAsync(workspace, default);
        Assert.IsNull(await store.FindMembershipAsync(tenant.Id, initial.Context.PrincipalId, default));
        Assert.IsNull(await store.FindWorkspaceMembershipAsync(workspace.Id, initial.Context.PrincipalId, default));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/identity/context");
        request.Headers.Add("X-Agentstration-Workspace", workspace.Id.ToString("D"));
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var selected = await response.Content.ReadFromJsonAsync<ConsoleContextView>();
        Assert.IsNotNull(selected);
        Assert.AreEqual(tenant.Id, selected.Context.TenantId);
        Assert.AreEqual(workspace.Id, selected.Context.WorkspaceId);
        Assert.IsTrue(selected.AvailableWorkspaces.Any(value => value.Id == initial.Context.WorkspaceId));
        Assert.IsTrue(selected.AvailableWorkspaces.Any(value => value.Id == workspace.Id));
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
            displayName = "Platform administrator",
            topology = BootstrapTopology()
        });
        Assert.AreEqual(HttpStatusCode.Created, bootstrapped.StatusCode);

        Guid workspaceOwnerPrincipalId;
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
            workspaceOwnerPrincipalId = principal.Id;
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
        using var platformAdministrationAccess = await client.GetAsync("/api/identity/platform-administrators");
        using var externalIdentityAdministrationAccess = await client.GetAsync($"/api/identity/principals/{workspaceOwnerPrincipalId:D}/external-identities");
        Assert.AreEqual(HttpStatusCode.OK, workspaceAccess.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, platformAccess.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, platformAdministrationAccess.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, externalIdentityAdministrationAccess.StatusCode);
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
            displayName = "Account administrator",
            topology = BootstrapTopology()
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
    public async Task PlatformAdministratorLifecycleSupportsSafeAdministrationTransfer()
    {
        await using var factory = Factory("Local");
        using var initialAdministrator = UnredirectedClient(factory);
        using var successorAdministrator = UnredirectedClient(factory);
        await BootstrapAsync(initialAdministrator, "initial-platform-admin");
        var context = await initialAdministrator.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.IsNotNull(context);

        using var created = await initialAdministrator.PostAsJsonAsync("/api/identity/accounts/", new
        {
            userName = "successor-platform-admin",
            password = LocalPassword,
            displayName = "Successor administrator",
            workspaceId = context.Context.WorkspaceId,
            role = BuiltInIdentityRoles.Admin
        });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var successor = await created.Content.ReadFromJsonAsync<LocalAccountView>();
        Assert.IsNotNull(successor);
        var accounts = await initialAdministrator.GetFromJsonAsync<LocalAccountView[]>("/api/identity/accounts/");
        Assert.IsNotNull(accounts);
        var initial = accounts.Single(value => value.PlatformAdministrator);

        using var selfDisable = await initialAdministrator.PutAsJsonAsync(
            $"/api/identity/accounts/{initial.AccountId:D}/status", new { enabled = false });
        Assert.AreEqual(HttpStatusCode.Conflict, selfDisable.StatusCode);

        using var granted = await initialAdministrator.PutAsync(
            $"/api/identity/platform-administrators/{successor.PrincipalId:D}", null);
        Assert.AreEqual(HttpStatusCode.OK, granted.StatusCode);
        var administrators = await initialAdministrator.GetFromJsonAsync<PlatformAdministratorView[]>(
            "/api/identity/platform-administrators");
        Assert.IsNotNull(administrators);
        Assert.HasCount(2, administrators);

        using var selfRevoke = await initialAdministrator.DeleteAsync(
            $"/api/identity/platform-administrators/{initial.PrincipalId:D}");
        Assert.AreEqual(HttpStatusCode.Conflict, selfRevoke.StatusCode);

        await LoginAsync(successorAdministrator, "successor-platform-admin", LocalPassword);
        using var disableInitial = await successorAdministrator.PutAsJsonAsync(
            $"/api/identity/accounts/{initial.AccountId:D}/status", new { enabled = false });
        Assert.AreEqual(HttpStatusCode.OK, disableInitial.StatusCode);
        using var invalidatedInitialSession = await initialAdministrator.GetAsync("/api/identity/platform");
        Assert.AreEqual(HttpStatusCode.Unauthorized, invalidatedInitialSession.StatusCode);

        using var revoked = await successorAdministrator.DeleteAsync(
            $"/api/identity/platform-administrators/{initial.PrincipalId:D}");
        Assert.AreEqual(HttpStatusCode.NoContent, revoked.StatusCode);
        using var grantDisabled = await successorAdministrator.PutAsync(
            $"/api/identity/platform-administrators/{initial.PrincipalId:D}", null);
        Assert.AreEqual(HttpStatusCode.Conflict, grantDisabled.StatusCode);
        using var enableInitial = await successorAdministrator.PutAsJsonAsync(
            $"/api/identity/accounts/{initial.AccountId:D}/status", new { enabled = true });
        Assert.AreEqual(HttpStatusCode.OK, enableInitial.StatusCode);
        await LoginAsync(initialAdministrator, "initial-platform-admin", LocalPassword);
        using var formerAdministratorDenied = await initialAdministrator.GetAsync("/api/identity/platform");
        Assert.AreEqual(HttpStatusCode.Forbidden, formerAdministratorDenied.StatusCode);

        using var lastAdministratorSelfRevoke = await successorAdministrator.DeleteAsync(
            $"/api/identity/platform-administrators/{successor.PrincipalId:D}");
        Assert.AreEqual(HttpStatusCode.Conflict, lastAdministratorSelfRevoke.StatusCode);
        administrators = await successorAdministrator.GetFromJsonAsync<PlatformAdministratorView[]>(
            "/api/identity/platform-administrators");
        Assert.IsNotNull(administrators);
        Assert.HasCount(1, administrators);
        Assert.AreEqual(successor.PrincipalId, administrators[0].Principal.Id);

        var auditEvents = await successorAdministrator.GetFromJsonAsync<SecurityAuditEvent[]>("/api/identity/audit-events");
        Assert.IsNotNull(auditEvents);
        Assert.IsTrue(auditEvents.Any(value => value.Action == SecurityAuditActions.PlatformAdministratorGranted
            && value.TargetPrincipalId == successor.PrincipalId));
        Assert.IsTrue(auditEvents.Any(value => value.Action == SecurityAuditActions.PlatformAdministratorRevoked
            && value.TargetPrincipalId == initial.PrincipalId && value.ActorPrincipalId == successor.PrincipalId));
    }

    [TestMethod]
    public async Task ConcurrentPlatformAdministratorsCannotDisableEachOtherAndLeaveNoActiveAdministrator()
    {
        await using var factory = Factory("Local");
        using var initialAdministrator = UnredirectedClient(factory);
        using var successorAdministrator = UnredirectedClient(factory);
        await BootstrapAsync(initialAdministrator, "concurrent-initial-admin");
        var context = await initialAdministrator.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.IsNotNull(context);

        using var created = await initialAdministrator.PostAsJsonAsync("/api/identity/accounts/", new
        {
            userName = "concurrent-successor-admin",
            password = LocalPassword,
            displayName = "Concurrent successor",
            workspaceId = context.Context.WorkspaceId,
            role = BuiltInIdentityRoles.Admin
        });
        var successor = await created.Content.ReadFromJsonAsync<LocalAccountView>();
        Assert.IsNotNull(successor);
        var initial = (await initialAdministrator.GetFromJsonAsync<LocalAccountView[]>("/api/identity/accounts/"))!
            .Single(value => value.PlatformAdministrator);
        using var granted = await initialAdministrator.PutAsync(
            $"/api/identity/platform-administrators/{successor.PrincipalId:D}", null);
        Assert.AreEqual(HttpStatusCode.OK, granted.StatusCode);
        await LoginAsync(successorAdministrator, "concurrent-successor-admin", LocalPassword);

        var responses = await Task.WhenAll(
            initialAdministrator.PutAsJsonAsync($"/api/identity/accounts/{successor.AccountId:D}/status", new { enabled = false }),
            successorAdministrator.PutAsJsonAsync($"/api/identity/accounts/{initial.AccountId:D}/status", new { enabled = false }));
        Assert.AreEqual(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));

        var activeClient = responses[0].StatusCode == HttpStatusCode.OK ? initialAdministrator : successorAdministrator;
        var accounts = await activeClient.GetFromJsonAsync<LocalAccountView[]>("/api/identity/accounts/");
        Assert.IsNotNull(accounts);
        Assert.AreEqual(1, accounts.Count(value => value.PlatformAdministrator && value.PrincipalStatus == PrincipalStatus.Active));
        foreach (var response in responses) response.Dispose();
    }

    [TestMethod]
    public async Task PlatformAdministratorCanManageProviderNeutralExternalIdentityLinks()
    {
        await using var factory = Factory("Local");
        using var administrator = UnredirectedClient(factory);
        await BootstrapAsync(administrator, "external-identity-admin");
        var context = await administrator.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.IsNotNull(context);

        async Task<LocalAccountView> CreateTargetAsync(string userName)
        {
            using var response = await administrator.PostAsJsonAsync("/api/identity/accounts/", new
            {
                userName,
                password = LocalPassword,
                displayName = userName,
                workspaceId = context.Context.WorkspaceId,
                role = BuiltInIdentityRoles.Member
            });
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<LocalAccountView>())!;
        }

        var first = await CreateTargetAsync("external-target-one");
        var second = await CreateTargetAsync("external-target-two");
        using var invalidIssuer = await administrator.PostAsJsonAsync(
            $"/api/identity/principals/{first.PrincipalId:D}/external-identities",
            new { issuer = "not-an-issuer", subject = "subject-1" });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidIssuer.StatusCode);

        const string firstIssuer = "https://issuer-a.example/tenant";
        const string secondIssuer = "https://issuer-b.example/tenant";
        using var linked = await administrator.PostAsJsonAsync(
            $"/api/identity/principals/{first.PrincipalId:D}/external-identities",
            new { issuer = firstIssuer, subject = "same-subject" });
        Assert.AreEqual(HttpStatusCode.OK, linked.StatusCode);
        var firstIdentity = await linked.Content.ReadFromJsonAsync<ExternalIdentity>();
        Assert.IsNotNull(firstIdentity);

        using var idempotent = await administrator.PostAsJsonAsync(
            $"/api/identity/principals/{first.PrincipalId:D}/external-identities",
            new { issuer = firstIssuer, subject = "same-subject" });
        Assert.AreEqual(firstIdentity.Id, (await idempotent.Content.ReadFromJsonAsync<ExternalIdentity>())?.Id);
        using var otherIssuer = await administrator.PostAsJsonAsync(
            $"/api/identity/principals/{first.PrincipalId:D}/external-identities",
            new { issuer = secondIssuer, subject = "same-subject" });
        Assert.AreEqual(HttpStatusCode.OK, otherIssuer.StatusCode);

        using var collision = await administrator.PostAsJsonAsync(
            $"/api/identity/principals/{second.PrincipalId:D}/external-identities",
            new { issuer = firstIssuer, subject = "same-subject" });
        Assert.AreEqual(HttpStatusCode.Conflict, collision.StatusCode);
        var identities = await administrator.GetFromJsonAsync<ExternalIdentity[]>(
            $"/api/identity/principals/{first.PrincipalId:D}/external-identities");
        Assert.IsNotNull(identities);
        Assert.HasCount(2, identities);

        var resolver = factory.Services.GetRequiredService<IPrincipalResolver>();
        Assert.AreEqual(first.PrincipalId, (await resolver.ResolveAsync(firstIssuer, "same-subject", default))?.Id);
        Assert.AreEqual(first.PrincipalId, (await resolver.ResolveAsync(secondIssuer, "same-subject", default))?.Id);
        using var removed = await administrator.DeleteAsync(
            $"/api/identity/principals/{first.PrincipalId:D}/external-identities/{firstIdentity.Id:D}");
        Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.IsNull(await resolver.ResolveAsync(firstIssuer, "same-subject", default));

        using var details = await administrator.GetAsync($"/settings/organization/members/{first.PrincipalId:D}");
        Assert.AreEqual(HttpStatusCode.OK, details.StatusCode);
        StringAssert.Contains(await details.Content.ReadAsStringAsync(), "External authentication");
        var auditEvents = await administrator.GetFromJsonAsync<SecurityAuditEvent[]>("/api/identity/audit-events");
        Assert.IsNotNull(auditEvents);
        Assert.IsTrue(auditEvents.Any(value => value.Action == SecurityAuditActions.ExternalIdentityLinked
            && value.TargetPrincipalId == first.PrincipalId));
        Assert.IsTrue(auditEvents.Any(value => value.Action == SecurityAuditActions.ExternalIdentityUnlinked
            && value.TargetPrincipalId == first.PrincipalId));
    }

    [TestMethod]
    public async Task ConcurrentUnlinkCannotRemoveEveryAuthenticationIdentity()
    {
        await using var factory = Factory("Local");
        using var administrator = UnredirectedClient(factory);
        await BootstrapAsync(administrator, "external-concurrency-admin");
        var principal = new Principal(Guid.NewGuid(), PrincipalKind.Human, "External only", null,
            PrincipalStatus.Active, DateTimeOffset.UtcNow);
        var first = new ExternalIdentity(Guid.NewGuid(), "https://issuer.example/one", "subject", principal.Id, DateTimeOffset.UtcNow);
        var second = new ExternalIdentity(Guid.NewGuid(), "https://issuer.example/two", "subject", principal.Id, DateTimeOffset.UtcNow);
        var store = factory.Services.GetRequiredService<IIdentityStore>();
        await store.AddPrincipalAsync(principal, default);
        await store.AddExternalIdentityAsync(first, default);
        await store.AddExternalIdentityAsync(second, default);

        var responses = await Task.WhenAll(
            administrator.DeleteAsync($"/api/identity/principals/{principal.Id:D}/external-identities/{first.Id:D}"),
            administrator.DeleteAsync($"/api/identity/principals/{principal.Id:D}/external-identities/{second.Id:D}"));
        Assert.AreEqual(1, responses.Count(value => value.StatusCode == HttpStatusCode.NoContent));
        Assert.AreEqual(1, responses.Count(value => value.StatusCode == HttpStatusCode.Conflict));
        var remaining = await store.ListExternalIdentitiesAsync(principal.Id, default);
        Assert.HasCount(1, remaining);
        foreach (var response in responses) response.Dispose();
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
            displayName = "Membership administrator",
            topology = BootstrapTopology()
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
            new { role = BuiltInIdentityRoles.Owner });
        Assert.AreEqual(HttpStatusCode.OK, promoted.StatusCode);
        var memberships = await client.GetFromJsonAsync<WorkspaceMemberView[]>(
            $"/api/identity/workspaces/{context.Context.WorkspaceId:D}/memberships");
        Assert.IsNotNull(memberships);
        Assert.AreEqual(BuiltInIdentityRoles.Owner, memberships.Single(value => value.Principal.Id == account.PrincipalId).Role);

        using var removeLastOwner = await client.DeleteAsync(
            $"/api/identity/workspaces/{context.Context.WorkspaceId:D}/memberships/{account.PrincipalId:D}");
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

}

