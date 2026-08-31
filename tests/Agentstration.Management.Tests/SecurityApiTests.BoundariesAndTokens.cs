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
    public async Task HttpMcpAndSignalRBoundariesEnforceTheRoleAndPatMatrix()
    {
        await using var factory = Factory("Local");
        using var administrator = UnredirectedClient(factory);
        await BootstrapAsync(administrator, "boundary-owner");
        var context = await administrator.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.IsNotNull(context);

        var viewer = await CreateAccountAsync(administrator, context.Context.WorkspaceId, "boundary-viewer", BuiltInIdentityRoles.Viewer);
        _ = await CreateAccountAsync(administrator, context.Context.WorkspaceId, "boundary-member", BuiltInIdentityRoles.Member);
        _ = await CreateAccountAsync(administrator, context.Context.WorkspaceId, "boundary-admin", BuiltInIdentityRoles.Admin);
        using var platformGrant = await administrator.PutAsync($"/api/identity/platform-administrators/{viewer.PrincipalId:D}", null);
        Assert.AreEqual(HttpStatusCode.OK, platformGrant.StatusCode);

        using var patResponse = await administrator.PostAsJsonAsync("/api/identity/pat", new
        {
            name = "Boundary read-only token",
            workspaceId = context.Context.WorkspaceId,
            permissions = new[] { AuthorizationPermissions.ResourcesRead },
            expiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });
        Assert.AreEqual(HttpStatusCode.Created, patResponse.StatusCode);
        using var patDocument = JsonDocument.Parse(await patResponse.Content.ReadAsStringAsync());
        var pat = patDocument.RootElement.GetProperty("token").GetString();
        Assert.IsNotNull(pat);

        using var anonymous = UnredirectedClient(factory);
        using var viewerClient = UnredirectedClient(factory);
        using var memberClient = UnredirectedClient(factory);
        using var adminClient = UnredirectedClient(factory);
        using var patClient = UnredirectedClient(factory);
        await LoginAsync(viewerClient, "boundary-viewer", LocalPassword);
        await LoginAsync(memberClient, "boundary-member", LocalPassword);
        await LoginAsync(adminClient, "boundary-admin", LocalPassword);
        patClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        patClient.DefaultRequestHeaders.Add("X-Agentstration-Workspace", context.Context.WorkspaceId.ToString("D"));

        var resourceRead = new AccessProbe("resources/read", () => new(HttpMethod.Get, "/api/modelproviders"));
        var resourceWrite = new AccessProbe("resources/write", () => Json(HttpMethod.Post, "/api/modelproviders", "{}"));
        var resourceDelete = new AccessProbe("resources/delete", () => new(HttpMethod.Delete, "/api/modelproviders/missing"));
        var runsExecute = new AccessProbe("runs/execute", () => new(HttpMethod.Post, "/api/triggers/missing/run"));
        var hubRead = new AccessProbe("SignalR runs/read", () => new(HttpMethod.Post, "/hubs/workplace/negotiate?negotiateVersion=1") { Content = new ByteArrayContent([]) });
        var probes = new[] { resourceRead, resourceWrite, resourceDelete, runsExecute, hubRead };

        await AssertAccessMatrixAsync(anonymous, probes, [false, false, false, false, false]);
        await AssertAccessMatrixAsync(viewerClient, probes, [true, true, true, true, true]);
        await AssertAccessMatrixAsync(memberClient, probes, [true, false, false, true, true]);
        await AssertAccessMatrixAsync(adminClient, probes, [true, true, true, true, true]);
        await AssertAccessMatrixAsync(patClient, probes, [true, false, false, false, false]);
        foreach (var deniedClient in new[] { anonymous, patClient })
        {
            using var deniedMcp = await deniedClient.SendAsync(Json(HttpMethod.Post, "/mcp", "{}"));
            Assert.IsTrue(deniedMcp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        }

        using var platformAccess = await viewerClient.GetAsync("/api/identity/platform");
        Assert.AreEqual(HttpStatusCode.OK, platformAccess.StatusCode);
        using var platformWorkspaceWrite = await viewerClient.SendAsync(resourceWrite.Request());
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, platformWorkspaceWrite.StatusCode);
        Assert.AreNotEqual(HttpStatusCode.Forbidden, platformWorkspaceWrite.StatusCode,
            "PlatformAdmin must authorize resources in every active Workspace.");
        using var vaultInitialization = await viewerClient.PostAsync("/api/vaults/missing/initialize", null);
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, vaultInitialization.StatusCode);
        Assert.AreNotEqual(HttpStatusCode.Forbidden, vaultInitialization.StatusCode);
    }

    [TestMethod]
    public async Task PersonalAccessTokenIsWorkspaceScopedPermissionLimitedAndRevocable()
    {
        await using var factory = Factory("Local");
        using var browser = UnredirectedClient(factory);
        await BootstrapAsync(browser, "pat-user");
        var context = await browser.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.IsNotNull(context);

        using var createdResponse = await browser.PostAsJsonAsync("/api/identity/pat", new
        {
            name = "Read-only automation",
            workspaceId = context.Context.WorkspaceId,
            permissions = new[] { AuthorizationPermissions.ResourcesRead },
            expiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        using var created = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync());
        var token = created.RootElement.GetProperty("token").GetString();
        var tokenId = created.RootElement.GetProperty("metadata").GetProperty("id").GetGuid();
        Assert.IsNotNull(token);
        Assert.StartsWith(PersonalAccessTokenService.TokenPrefix, token, StringComparison.Ordinal);

        using var listResponse = await browser.GetAsync("/api/identity/pat");
        Assert.AreEqual(HttpStatusCode.OK, listResponse.StatusCode);
        var listedJson = await listResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, listedJson, StringComparison.Ordinal);
        StringAssert.Contains(listedJson, "Read-only automation");

        using var tokenClient = UnredirectedClient(factory);
        tokenClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        tokenClient.DefaultRequestHeaders.Add("X-Agentstration-Workspace", context.Context.WorkspaceId.ToString("D"));
        using var allowed = await tokenClient.GetAsync("/api/agents");
        using var deniedByScope = await tokenClient.GetAsync("/api/flowRuns");
        using var deniedAdministration = await tokenClient.GetAsync("/api/identity/pat");
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, deniedByScope.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, deniedAdministration.StatusCode);

        using var crossWorkspace = UnredirectedClient(factory);
        crossWorkspace.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        crossWorkspace.DefaultRequestHeaders.Add("X-Agentstration-Workspace", Guid.NewGuid().ToString("D"));
        using var deniedWorkspace = await crossWorkspace.GetAsync("/api/agents");
        Assert.AreEqual(HttpStatusCode.Forbidden, deniedWorkspace.StatusCode);

        using var revoked = await browser.DeleteAsync($"/api/identity/pat/{tokenId:D}");
        Assert.AreEqual(HttpStatusCode.NoContent, revoked.StatusCode);
        using var deniedAfterRevocation = await tokenClient.GetAsync("/api/agents");
        Assert.AreEqual(HttpStatusCode.Unauthorized, deniedAfterRevocation.StatusCode);
    }

    [TestMethod]
    public async Task PersonalAccessTokenPageUsesAntiforgeryAndNeverDisplaysExistingSecrets()
    {
        await using var factory = Factory("Local");
        using var browser = UnredirectedClient(factory);
        await BootstrapAsync(browser, "pat-page-user");

        using var page = await browser.GetAsync("/account/pat");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "Personal access tokens");
        StringAssert.Contains(html, "Create a token");
        _ = AntiforgeryToken(html);

        using var missingAntiforgery = await browser.PostAsync(
            "/account/pat?handler=RevokeAll",
            new FormUrlEncodedContent([]));
        Assert.AreEqual(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
    }

    [TestMethod]
    public async Task AuthenticatedPrincipalCanPersistOwnPreferences()
    {
        await using var factory = Factory("Local");
        using var browser = UnredirectedClient(factory);
        await BootstrapAsync(browser, "preferences-user");

        var defaults = await browser.GetFromJsonAsync<PreferencesResponse>("/api/identity/preferences");
        Assert.AreEqual("System", defaults?.Theme);
        Assert.IsNull(defaults?.Language);

        using var updated = await browser.PutAsJsonAsync(
            "/api/identity/preferences",
            new { theme = "Dark", language = "fr-FR" });
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
        var updatedPreferences = await updated.Content.ReadFromJsonAsync<PreferencesResponse>();
        Assert.AreEqual("Dark", updatedPreferences?.Theme);
        Assert.AreEqual("fr-FR", updatedPreferences?.Language);
        var persisted = await browser.GetFromJsonAsync<PreferencesResponse>("/api/identity/preferences");
        Assert.AreEqual("Dark", persisted?.Theme);
        Assert.AreEqual("fr-FR", persisted?.Language);

        using var invalid = await browser.PutAsJsonAsync(
            "/api/identity/preferences",
            new { theme = "Sepia" });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var unsupportedLanguage = await browser.PutAsJsonAsync(
            "/api/identity/preferences",
            new { theme = "Dark", language = "de-DE" });
        Assert.AreEqual(HttpStatusCode.BadRequest, unsupportedLanguage.StatusCode);

        using var anonymous = UnredirectedClient(factory);
        using var unauthorized = await anonymous.GetAsync("/api/identity/preferences");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [TestMethod]
    public async Task CultureCookieLocalizesTheAuthenticatedProfilePage()
    {
        await using var factory = Factory("Local");
        using var browser = UnredirectedClient(factory);
        await BootstrapAsync(browser, "localized-profile-user");

        using var selected = await browser.GetAsync(
            "/_culture?culture=fr-FR&returnUrl=%2Fsettings%2Fprofile");
        Assert.AreEqual(HttpStatusCode.Redirect, selected.StatusCode);
        Assert.AreEqual("/settings/profile", selected.Headers.Location?.OriginalString);

        using var profile = await browser.GetAsync("/settings/profile");
        Assert.AreEqual(HttpStatusCode.OK, profile.StatusCode);
        var html = await profile.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "<html lang=\"fr-FR\">");
        StringAssert.Contains(WebUtility.HtmlDecode(html), "Paramètres du profil");
        StringAssert.Contains(WebUtility.HtmlDecode(html), "Vue d’ensemble");

        using var overview = await browser.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.OK, overview.StatusCode);
        var overviewHtml = WebUtility.HtmlDecode(await overview.Content.ReadAsStringAsync());
        StringAssert.Contains(overviewHtml, "Vue d’ensemble de la plateforme");
        StringAssert.Contains(overviewHtml, "Agents définis");

        using var settings = await browser.GetAsync("/settings");
        Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
        var settingsHtml = WebUtility.HtmlDecode(await settings.Content.ReadAsStringAsync());
        StringAssert.Contains(settingsHtml, "Paramètres de la Console");
        StringAssert.Contains(settingsHtml, "API HTTP canoniques");

        using var unsupported = await browser.GetAsync(
            "/_culture?culture=de-DE&returnUrl=%2Fsettings%2Fprofile");
        Assert.AreEqual(HttpStatusCode.BadRequest, unsupported.StatusCode);
    }

}

