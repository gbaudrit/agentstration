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
        StringAssert.Contains(bootstrapHtml, "Tenant name");
        StringAssert.Contains(bootstrapHtml, "Workspace name");
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
            ["Input.TenantName"] = "interactive",
            ["Input.TenantDisplayName"] = "Interactive tenant",
            ["Input.WorkspaceName"] = "default",
            ["Input.WorkspaceDisplayName"] = "Default workspace",
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
            email = "admin-before@example.test",
            topology = BootstrapTopology()
        });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

        var initialized = await client.GetFromJsonAsync<BootstrapStatus>("/api/auth/bootstrap");
        Assert.IsNotNull(initialized);
        Assert.IsTrue(initialized.Initialized);
        using var repeated = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            userName = "second-admin",
            password = LocalPassword,
            displayName = "Second administrator",
            topology = BootstrapTopology()
        });
        Assert.AreEqual(HttpStatusCode.Conflict, repeated.StatusCode);

        using var agents = await client.GetAsync("/api/agents");
        var standardRuntime = await client.GetFromJsonAsync<RuntimeProfileResource>("/api/runtimeprofiles/maf-builtin");
        using var platform = await client.GetAsync("/api/identity/platform");
        Assert.AreEqual(HttpStatusCode.OK, agents.StatusCode);
        Assert.IsNotNull(standardRuntime);
        Assert.AreEqual("microsoft-agent-framework", standardRuntime.Definition.RuntimeType);
        Assert.AreEqual(RuntimeSessionMode.Transient, standardRuntime.Definition.Execution.SessionMode);
        Assert.AreEqual(RuntimeToolInvocationMode.Automatic, standardRuntime.Definition.Execution.ToolInvocation);
        Assert.AreEqual(StreamingMode.Automatic, standardRuntime.Definition.Execution.Streaming);
        Assert.AreEqual("true", standardRuntime.Metadata.Annotations[ResourceProvenanceAnnotations.BuiltIn]);
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

}

