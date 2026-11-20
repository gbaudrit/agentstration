using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed partial class OrganizationMemberDetailsTests
{
    [TestMethod]
    [DataRow("en-US", "Identity", "Person")]
    [DataRow("fr-FR", "Identité", "Personne")]
    public void OverviewShowsActualPrincipalAndWorkspaceWithoutResourceCoordinates(string culture, string identity, string kind)
    {
        using var language = new CultureScope(culture);
        using var handler = new MemberHandler();
        using var context = Context(handler);
        var rendered = Render(context, handler.First.Id);
        rendered.WaitForElement(".resource-identity-summary");
        Assert.Contains(identity, rendered.Find(".resource-identity-summary").TextContent);
        Assert.Contains(kind, rendered.Find(".resource-identity-summary").TextContent);
        Assert.Contains(handler.First.Id.ToString("D"), rendered.Find(".resource-identity-summary").TextContent);
        Assert.Contains("Default workspace", rendered.Find(".resource-configuration-summary").TextContent);
        Assert.AreEqual(0, rendered.FindAll(".resource-header .resource-scope-badge, .resource-header [role=switch]").Count);
        Assert.IsEmpty(rendered.FindAll("a[data-resource-section=yaml]"));
        Assert.AreEqual(1, handler.OrganizationReads);
    }

    [TestMethod]
    public async Task MembershipDraftSurvivesSectionsAndRefreshThenWritesTheExactWorkspaceAndMember()
    {
        using var language = new CultureScope("en-US");
        using var handler = new MemberHandler();
        using var context = Context(handler);
        var rendered = Render(context, handler.First.Id);
        rendered.WaitForElement(".resource-identity-summary");
        Section(context, "membership");
        rendered.Find("select").Change(BuiltInIdentityRoleNames.Admin);
        Assert.IsTrue(rendered.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty);
        Section(context, "overview");
        Assert.Contains(BuiltInIdentityRoleNames.Viewer, rendered.Find(".resource-configuration-summary").TextContent);
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreEqual(2, handler.OrganizationReads);
        Assert.IsEmpty(rendered.FindAll("dialog"));
        Section(context, "membership");
        Assert.AreEqual(BuiltInIdentityRoleNames.Admin, rendered.Find("select").GetAttribute("value"));
        rendered.FindAll(".resource-details-content button.button-primary").Single(button => button.TextContent == "Save role").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual(BuiltInIdentityRoleNames.Admin, handler.SavedRole));
        Assert.AreEqual($"/api/identity/workspaces/{MemberHandler.WorkspaceId:D}/memberships/{handler.First.Id:D}", handler.WritePath);
        Assert.IsFalse(rendered.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty);
        Assert.AreEqual(3, handler.OrganizationReads);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PlatformOnlySectionsAndReadOnlyMembershipFollowAuthorizedCapabilities(bool administrator)
    {
        using var handler = new MemberHandler { Administrator = administrator };
        using var context = Context(handler, write: false);
        var rendered = Render(context, handler.First.Id);
        rendered.WaitForElement(".resource-identity-summary");
        Assert.AreEqual(administrator ? 1 : 0, rendered.FindAll(".resource-navigation-rail a[data-resource-section=authentication]").Count);
        Assert.AreEqual(administrator ? 1 : 0, rendered.FindAll(".resource-navigation-rail a[data-resource-section=administration]").Count);
        Assert.AreEqual(administrator ? 1 : 0, handler.AdministratorReads);
        Section(context, "membership");
        Assert.IsTrue(rendered.Find("select").HasAttribute("disabled"));
        Assert.IsTrue(rendered.FindAll(".resource-details-content button.button-primary").First().HasAttribute("disabled"));
        Assert.IsNull(handler.WritePath);
    }

    [TestMethod]
    public void MissingMemberLeavesTheLoadingStateAndCanBeRetried()
    {
        using var handler = new MemberHandler { Missing = true };
        using var context = Context(handler);
        var rendered = Render(context, handler.First.Id);
        rendered.WaitForElement(".error-panel");
        Assert.IsEmpty(rendered.FindAll(".resource-identity-summary"));
        handler.Missing = false;
        rendered.Find(".error-panel button").Click();
        rendered.WaitForElement(".resource-identity-summary");
        Assert.Contains(handler.First.Id.ToString("D"), rendered.Find(".resource-identity-summary").TextContent);
        Assert.AreEqual(2, handler.OrganizationReads);
    }

    [TestMethod]
    public async Task LateReadForThePreviousMemberCannotReplaceTheCurrentIdentityOrContinueItsQueries()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new MemberHandler { FirstOrganization = pending.Task };
        using var context = Context(handler);
        var rendered = context.Render<ObservedMember>(p => p.Add(value => value.UserId, handler.First.Id));
        var firstLoad = rendered.Instance.ParameterCompletion;
        rendered.Render(p => p.Add(value => value.UserId, handler.Second.Id));
        rendered.WaitForAssertion(() => Assert.Contains(handler.Second.Id.ToString("D"), rendered.Find(".resource-identity-summary").TextContent));
        await rendered.InvokeAsync(() => pending.SetResult(handler.OrganizationResponse()));
        await firstLoad;
        rendered.WaitForAssertion(() => Assert.Contains(handler.Second.Id.ToString("D"), rendered.Find(".resource-identity-summary").TextContent));
        Assert.DoesNotContain(handler.First.Id.ToString("D"), rendered.Find(".resource-identity-summary").TextContent);
        Assert.AreEqual(1, handler.MembershipReads);
    }

    private sealed class ObservedMember : OrganizationMemberDetails
    {
        public Task ParameterCompletion { get; private set; } = Task.CompletedTask;
        protected override Task OnParametersSetAsync() => ParameterCompletion = base.OnParametersSetAsync();
    }

    private static IRenderedComponent<OrganizationMemberDetails> Render(BunitContext context, Guid user) =>
        context.Render<OrganizationMemberDetails>(p => p.Add(value => value.UserId, user));
    private static void Section(BunitContext context, string section)
    {
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.Uri.Split('#')[0] + "#" + section);
    }
    private static BunitContext Context(MemberHandler handler, bool write = true)
    {
        var context = new BunitContext().WithCollectionPresentation();
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<string>("currentLocation", _ => true).SetResult("http://localhost/#overview");
        module.Setup<string>("navigate", _ => true).SetResult("http://localhost/#overview");
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("Blazor._internal.domWrapper.focus", _ => true).SetVoidResult();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IIdentityAdministrationApiClient>(new IdentityAdministrationApiClient(new HttpClient(handler) { BaseAddress = new("http://localhost/") }));
        context.Services.AddSingleton(new ConsoleContextState(new ContextProvider(write)));
        return context;
    }
    private sealed class ContextProvider(bool write) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            MemberHandler.OperatorId, "Operator", $"/workspaces/{MemberHandler.WorkspaceId:D}", ConsoleContextKind.Workspace,
            MemberHandler.TenantId, "acme", "Acme", MemberHandler.WorkspaceId, "default", "Default workspace",
            new HashSet<string>(write ? [AuthorizationPermissions.AuthorizationRead, AuthorizationPermissions.AuthorizationWrite] : [AuthorizationPermissions.AuthorizationRead]), [], []));
    }
    private sealed class MemberHandler : HttpMessageHandler
    {
        public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public static readonly Guid OperatorId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        public Principal First { get; } = new(Guid.NewGuid(), PrincipalKind.Human, "First member", "first@example.invalid", PrincipalStatus.Active, DateTimeOffset.UnixEpoch);
        public Principal Second { get; } = new(Guid.NewGuid(), PrincipalKind.Human, "Second member", null, PrincipalStatus.Active, DateTimeOffset.UnixEpoch);
        public bool Administrator { get; init; }
        public bool Missing { get; set; }
        public HttpStatusCode? ReadFailure { get; set; }
        public Task<HttpResponseMessage>? PendingOrganization { get; set; }
        public Guid ResponseTenant { get; set; } = TenantId;
        public Guid CapabilitiesPrincipal { get; set; } = OperatorId;
        public Guid CapabilitiesWorkspace { get; set; } = WorkspaceId;
        public string CurrentRole { get => role; set => role = value; }
        public bool WriteNetworkFailure { get; set; }
        public bool MembershipPresent { get; private set; } = true;
        public int OrganizationReads { get; private set; }
        public int MembershipReads { get; private set; }
        public int AdministratorReads { get; private set; }
        public string? WritePath { get; private set; }
        public string? SavedRole { get; private set; }
        public Task<HttpResponseMessage>? FirstOrganization { get; init; }
        private string role = BuiltInIdentityRoleNames.Viewer;
        private OrganizationMemberResponse Member(Principal principal) => new(principal, new(Guid.NewGuid(), TenantId, principal.Id, MembershipStatus.Active, DateTimeOffset.UnixEpoch), [], []);
        private WorkspaceMemberResponse Membership(Principal principal) => new(principal, new(Guid.NewGuid(), WorkspaceId, principal.Id, MembershipStatus.Active, DateTimeOffset.UnixEpoch), role, false);
        public HttpResponseMessage OrganizationResponse() => Ok(new OrganizationAdministrationResponse(new(ResponseTenant, "acme", "Acme", TenantStatus.Active, DateTimeOffset.UnixEpoch), [], Missing ? [] : [Member(First), Member(Second)]));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/identity/organization")
            {
                OrganizationReads++;
                if (ReadFailure is { } code) return new(code) { Content = JsonContent.Create(new { title = "Read unavailable", status = (int)code }) };
                if (PendingOrganization is { } pending) return await pending;
                if (OrganizationReads == 1 && FirstOrganization is not null) return await FirstOrganization;
                return OrganizationResponse();
            }
            if (path == "/api/identity/administration-capabilities") return Ok(new IdentityAdministrationCapabilitiesResponse(CapabilitiesPrincipal, CapabilitiesWorkspace, Administrator, true));
            if (path == "/api/identity/platform-administrators") { AdministratorReads++; return Ok(Array.Empty<PlatformAdministratorResponse>()); }
            if (path == $"/api/identity/workspaces/{WorkspaceId:D}/memberships") { MembershipReads++; return Ok(MembershipPresent ? new[] { Membership(First), Membership(Second) } : new[] { Membership(Second) }); }
            if (request.Method == HttpMethod.Delete && path == $"/api/identity/workspaces/{WorkspaceId:D}/memberships/{First.Id:D}")
            {
                WritePath = path; MembershipPresent = false; return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/identity/workspaces/{WorkspaceId:D}/memberships/{First.Id:D}")
            {
                if (WriteNetworkFailure) throw new HttpRequestException("Network unavailable");
                var value = await request.Content!.ReadFromJsonAsync<SetWorkspaceMembershipRequest>(cancellationToken);
                WritePath = path; role = SavedRole = value!.Role;
                return Ok(Membership(First));
            }
            return new(HttpStatusCode.NotFound);
        }
        private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo culture = CultureInfo.CurrentCulture, ui = CultureInfo.CurrentUICulture;
        public CultureScope(string name) { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name); }
        public void Dispose() { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = ui; }
    }
}
