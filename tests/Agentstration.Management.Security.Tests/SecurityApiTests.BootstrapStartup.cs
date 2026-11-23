using System.Net;
using System.Net.Http.Json;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web.Console;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class SecurityApiTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartupProgressIsPrivateAndAdmitsOnlyPlatformAdministrators(bool failed)
    {
        await using var factory = Factory("Local", fullHost: true);
        using var administrator = UnredirectedClient(factory);
        await BootstrapAsync(administrator, "startup-owner");
        var context = await administrator.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        _ = await CreateAccountAsync(administrator, context!.Context.WorkspaceId, "startup-member", BuiltInIdentityRoles.Admin);
        using var member = UnredirectedClient(factory);
        await LoginAsync(member, "startup-member", LocalPassword);
        var startup = factory.Services.GetRequiredService<BootstrapStartupState>();
        startup.Begin(); startup.EnableAdministration(); startup.MoveTo("extensions"); startup.MoveTo("resources");
        startup.ObserveModelWait("profile", "provider", "expected:tag", "required_model_missing", "custom");
        startup.RegisterResource("/instance", "startup", "profile.yaml", "resources", "ModelProfile", "profile");
        if (failed) startup.Fail("private-password-in-an-exception");

        using var anonymous = UnredirectedClient(factory);
        using var publicRead = await anonymous.GetAsync("/api/bootstrap/startup");
        Assert.AreEqual(HttpStatusCode.Unauthorized, publicRead.StatusCode);
        using var denied = await member.GetAsync("/api/bootstrap/startup");
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using var anonymousFiles = await anonymous.PostAsJsonAsync("/api/bootstrap/profiles/files", new BootstrapProfileFilesRequest("startup"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousFiles.StatusCode);
        using var deniedFiles = await member.PostAsJsonAsync("/api/bootstrap/profiles/files", new BootstrapProfileFilesRequest("startup"));
        Assert.AreEqual(HttpStatusCode.Forbidden, deniedFiles.StatusCode);
        Assert.IsFalse((await denied.Content.ReadAsStringAsync()).Contains("expected:tag", StringComparison.Ordinal));
        using var freshMember = UnredirectedClient(factory);
        using var rejected = await freshMember.PostAsJsonAsync("/api/auth/local/login", new { userName = "startup-member", password = LocalPassword });
        Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var newAdmin = UnredirectedClient(factory);
        await LoginAsync(newAdmin, "startup-owner", LocalPassword);
        using var progress = await newAdmin.GetAsync("/api/bootstrap/startup");
        Assert.AreEqual(HttpStatusCode.OK, progress.StatusCode);
        Assert.IsTrue(progress.Headers.CacheControl?.NoStore);
        var json = await progress.Content.ReadAsStringAsync();
        StringAssert.Contains(json, "expected:tag");
        Assert.IsFalse(json.Contains("private-password", StringComparison.Ordinal));
        var snapshot = await progress.Content.ReadFromJsonAsync<BootstrapStartupSnapshot>();
        Assert.AreEqual(failed ? "failed" : "running", snapshot!.State);
        Assert.IsFalse(snapshot.Ready);
        var resourceUrl = $"/api/bootstrap/startup/resources?attemptId={snapshot.AttemptId:D}&limit=25";
        using var publicResources = await anonymous.GetAsync(resourceUrl);
        Assert.AreEqual(HttpStatusCode.Unauthorized, publicResources.StatusCode);
        using var memberResources = await member.GetAsync(resourceUrl);
        Assert.AreEqual(HttpStatusCode.Forbidden, memberResources.StatusCode);
        using var administratorResources = await newAdmin.GetAsync(resourceUrl);
        Assert.AreEqual(HttpStatusCode.OK, administratorResources.StatusCode);
        var resourcePage = await administratorResources.Content.ReadFromJsonAsync<BootstrapStartupResourcePage>();
        Assert.AreEqual(1, resourcePage!.Total);
        Assert.IsTrue(administratorResources.Headers.CacheControl?.NoStore);
        using var oversizedPage = await newAdmin.GetAsync(resourceUrl + "&offset=-1");
        Assert.AreEqual(HttpStatusCode.BadRequest, oversizedPage.StatusCode);
        using var staleAttempt = await newAdmin.GetAsync($"/api/bootstrap/startup/resources?attemptId={Guid.NewGuid():D}");
        Assert.AreEqual(HttpStatusCode.Conflict, staleAttempt.StatusCode);
        var correctionUrl = $"/api/bootstrap/startup/resources/unknown/model-options?attemptId={snapshot.AttemptId:D}";
        using var anonymousCorrection = await anonymous.GetAsync(correctionUrl);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousCorrection.StatusCode);
        using var deniedCorrection = await member.GetAsync(correctionUrl);
        Assert.AreEqual(HttpStatusCode.Forbidden, deniedCorrection.StatusCode);
        using var staleCorrection = await newAdmin.GetAsync(correctionUrl);
        Assert.AreEqual(HttpStatusCode.Conflict, staleCorrection.StatusCode);

        administrator.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var overview = await administrator.GetAsync("/?source=login");
        Assert.AreEqual(HttpStatusCode.Redirect, overview.StatusCode);
        Assert.AreEqual(BootstrapStartupNavigation.ProgressUrl("/?source=login"), overview.Headers.Location?.OriginalString);
        using var direct = await administrator.GetAsync("/agents");
        Assert.AreEqual(HttpStatusCode.OK, direct.StatusCode);
        StringAssert.Contains(await direct.Content.ReadAsStringAsync(), "bootstrap-startup-notice");
        using var repair = await newAdmin.GetAsync("/api/extensionregistrations");
        Assert.AreEqual(HttpStatusCode.OK, repair.StatusCode);
        using var memberRepair = await member.GetAsync("/api/extensionregistrations");
        Assert.AreEqual(HttpStatusCode.Forbidden, memberRepair.StatusCode);
        using var memberTransport = await member.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        Assert.AreEqual(HttpStatusCode.Forbidden, memberTransport.StatusCode);
        using var administratorTransport = await newAdmin.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        Assert.AreEqual(HttpStatusCode.OK, administratorTransport.StatusCode);
        using var memberHealth = await member.GetAsync("/health/live");
        Assert.AreEqual(HttpStatusCode.OK, memberHealth.StatusCode);
        newAdmin.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var tools = await newAdmin.GetAsync("/api/tools");
        Assert.AreEqual(HttpStatusCode.OK, tools.StatusCode);
        using var toolMutation = await newAdmin.PostAsJsonAsync("/api/tools", new { name = "blocked" });
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, toolMutation.StatusCode);

        if (!failed)
        {
            startup.Complete();
            using var admission = await member.GetAsync("/api/bootstrap/startup/admission");
            Assert.AreEqual(HttpStatusCode.OK, admission.StatusCode);
            Assert.IsTrue((await admission.Content.ReadFromJsonAsync<BootstrapStartupAdmission>())!.Ready);
            await LoginAsync(freshMember, "startup-member", LocalPassword);
        }
        using var logout = await member.PostAsync("/api/auth/logout", null);
        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
    }

    [TestMethod]
    public async Task LocalBffCredentialValidationUsesTheSameStartupAdmissionRule()
    {
        using var fixture = BffFixture.Create();
        await using var factory = fixture.Factory();
        using var administrator = UnredirectedClient(factory);
        await BootstrapAsync(administrator, "bff-startup-owner");
        var context = await administrator.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        _ = await CreateAccountAsync(administrator, context!.Context.WorkspaceId, "bff-startup-member", BuiltInIdentityRoles.Admin);
        var state = factory.Services.GetRequiredService<BootstrapStartupState>();
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<Agentstration.Identity.Api.Security.BffSessionAuthorityService>();
        var existing = await service.AuthenticateLocalAsync(new("bff-startup-member", LocalPassword), default);
        Assert.IsNotNull(existing.Identity);
        var session = new BffSessionValidationRequest(existing.Identity.PrincipalId,
            AuthenticationVersion: existing.Identity.AuthenticationVersion);
        Assert.IsTrue((await service.ValidateAsync(session, default)).Active);
        state.Begin(); state.EnableAdministration();
        using var trustedTransport = fixture.SignedRequest("primary");
        using var transportResponse = await administrator.SendAsync(trustedTransport);
        Assert.AreEqual(HttpStatusCode.OK, transportResponse.StatusCode);
        using var unsignedTransport = await administrator.GetAsync("/api/internal/bff/trust");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unsignedTransport.StatusCode);
        Assert.IsFalse((await service.ValidateAsync(session, default)).Active);
        var denied = await service.AuthenticateLocalAsync(new("bff-startup-member", LocalPassword), default);
        Assert.AreEqual(LocalLoginOutcome.Failed, denied.Outcome);
        Assert.IsNull(denied.Identity);
        var allowed = await service.AuthenticateLocalAsync(new("bff-startup-owner", LocalPassword), default);
        Assert.AreEqual(LocalLoginOutcome.Succeeded, allowed.Outcome);
        Assert.IsNotNull(allowed.Identity);
        state.Complete();
        Assert.IsTrue((await service.ValidateAsync(session, default)).Active);
    }
}
