using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class OrganizationMembersCollectionTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid FirstRole = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid SecondRole = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly string TenantScope = $"/tenants/{TenantId:D}";
    private static readonly string WorkspaceScope = $"/workspaces/{WorkspaceId:D}";

    [TestMethod]
    [DataRow("en-US", "No matching results", "No members")]
    [DataRow("fr-FR", "Aucun résultat correspondant", "Aucun membre")]
    public async Task CompleteSnapshotSupportsExactPagesAndCombinedFiltersOnOneAssignment(string culture, string noMatches, string empty)
    {
        using var locale = new TestCultureScope(culture);
        var count = 45;
        using var handler = new Handler(_ => Task.FromResult(Response(count)));
        using var context = Context(handler);
        var cut = context.Render<OrganizationMembers>();
        Assert.HasCount(20, cut.FindAll("[data-testid=organization-member-row]"));
        Assert.HasCount(5, cut.FindAll(".organization-tabs a"));
        Assert.IsEmpty(cut.FindAll(".collection-actions"));
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=organization-member-row]"));
        await cut.Find("[data-filter-key=membership] select").ChangeAsync(new() { Value = "Suspended" });
        Assert.HasCount(15, cut.FindAll("[data-testid=organization-member-row]"));
        await cut.Find("[data-filter-key=role] select").ChangeAsync(new() { Value = FirstRole.ToString("D") });
        Assert.HasCount(15, cut.FindAll("[data-testid=organization-member-row]"));
        await cut.Find(".resource-toolbar button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=scope] select").ChangeAsync(new() { Value = WorkspaceScope });
        // Every member has first-role/Tenant and second-role/Workspace. Separate Any predicates would produce false matches.
        Assert.AreEqual(noMatches, cut.Find(".empty-state h2").TextContent);
        await cut.Find(".empty-state button").ClickAsync(new());
        await cut.Find("input[type=search]").InputAsync(new() { Value = "member44@example.test" });
        Assert.HasCount(1, cut.FindAll("[data-testid=organization-member-row]"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.HasCount(1, cut.FindAll(".collection-card"));
        Assert.AreEqual(1, handler.Requests);
        await cut.Find(".collection-filter-chip").ClickAsync(new());
        count = 0;
        await cut.Find("[data-testid=console-admin-organization-members-refresh]").ClickAsync(new());
        Assert.AreEqual(empty, cut.Find(".empty-state h2").TextContent);
    }

    [TestMethod]
    public async Task FailedRefreshRetainsMembersAndSuccessfulShrinkClampsThePage()
    {
        using var handler = new Handler(call => call == 2 ? throw new HttpRequestException("Private transport detail") : Task.FromResult(Response(call == 1 ? 45 : 3)));
        using var context = Context(handler);
        var cut = context.Render<OrganizationMembers>();
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        await cut.Find("[data-testid=console-admin-organization-members-refresh]").ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=organization-member-row]"));
        Assert.IsFalse(cut.Markup.Contains("Private transport detail", StringComparison.Ordinal));
        await cut.Find("[role=alert] button").ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("[data-testid=organization-member-row]"));
        Assert.IsEmpty(cut.FindAll(".collection-page-number"));
    }

    [TestMethod]
    public async Task TenantReplacementRejectsLateMembersAndDeniedRefreshClearsRows()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var replacement = new TaskCompletionSource<HttpResponseMessage>();
        var provider = new Provider();
        using var handler = new Handler(call => call switch { 1 => Task.FromResult(Response(1)), 2 => old.Task, 3 => replacement.Task, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) });
        using var context = Context(handler, provider);
        var cut = context.Render<OrganizationMembers>();
        var refresh = cut.Find("[data-testid=console-admin-organization-members-refresh]").ClickAsync(new());
        provider.Tenant = Guid.NewGuid();
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("[data-testid=organization-member-row]")));
        replacement.SetResult(Response(2));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll("[data-testid=organization-member-row]")));
        old.SetResult(Response(4));
        await refresh;
        Assert.HasCount(2, cut.FindAll("[data-testid=organization-member-row]"));
        await cut.Find("[data-testid=console-admin-organization-members-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[data-testid=organization-member-row]"));
    }

    [TestMethod]
    public async Task LocalAccountSectionRetainsItsCreateAndEnableCommandsAndClearsDraftsOnContextNotification()
    {
        var provider = new Provider();
        using var handler = new Handler(_ => Task.FromResult(Response(1))) { Administrator = true };
        using var context = Context(handler, provider);
        var cut = context.Render<OrganizationMembers>();
        Assert.HasCount(1, cut.FindAll("#organization-local-accounts .collection-actions"));
        await cut.Find("#organization-local-accounts .collection-actions button").ClickAsync(new());
        Assert.AreEqual(1, handler.StatusWrites);
        var inputs = cut.FindAll("#organization-local-accounts input");
        await inputs[0].ChangeAsync(new() { Value = "fixture-new" });
        await inputs[1].ChangeAsync(new() { Value = "Fixture new" });
        await cut.Find("input[type=password]").ChangeAsync(new() { Value = "public-fixture-password" });
        await cut.Find("#organization-local-accounts .form-actions button").ClickAsync(new());
        Assert.AreEqual(1, handler.CreateWrites);
        Assert.AreEqual("", cut.Find("input[type=password]").GetAttribute("value") ?? "");
        await cut.Find("input[type=password]").ChangeAsync(new() { Value = "discard-on-reauthorization" });
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.AreEqual("", cut.Find("input[type=password]").GetAttribute("value") ?? ""));
        handler.AccountsDenied = true;
        await cut.Find("[data-testid=console-admin-organization-members-refresh]").ClickAsync(new());
        Assert.HasCount(1, cut.FindAll("[data-testid=organization-member-row]"));
        Assert.IsEmpty(cut.FindAll("#organization-local-accounts input"));
    }

    [TestMethod]
    public void AuthorizationReadIsRequiredWithoutAnInventedResourceReadGate()
    {
        var provider = new Provider { Permissions = new HashSet<string>() };
        using var handler = new Handler(_ => Task.FromResult(Response(1)));
        using var context = Context(handler, provider);
        var cut = context.Render<OrganizationMembers>();
        Assert.IsEmpty(cut.FindAll("[data-testid=organization-member-row]"));
        Assert.AreEqual(0, handler.Requests);
    }

    private static HttpResponseMessage Response(int count) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new OrganizationAdministrationResponse(new(TenantId, "tenant", "Tenant", TenantStatus.Active, DateTimeOffset.UnixEpoch),
            [new(WorkspaceId, TenantId, "team", "Team", WorkspaceStatus.Active, DateTimeOffset.UnixEpoch)],
            Enumerable.Range(0, count).Select(index =>
            {
                var principal = Guid.ParseExact((index + 1).ToString("x32", CultureInfo.InvariantCulture), "N");
                return new OrganizationMemberResponse(new(principal, PrincipalKind.Human, $"Member {index}", $"member{index}@example.test", PrincipalStatus.Active, DateTimeOffset.UnixEpoch),
                    new(Guid.NewGuid(), TenantId, principal, index % 3 == 0 ? MembershipStatus.Suspended : MembershipStatus.Active, DateTimeOffset.UnixEpoch), [],
                    [new(new(FirstRole, "first", "First role", [], false), TenantScope), new(new(SecondRole, "second", "Second role", [], false), WorkspaceScope)]);
            }).ToArray()))
    };
    private sealed class Provider : IConsoleContextProvider
    {
        public Guid Tenant { get; set; } = TenantId;
        public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string> { AuthorizationPermissions.AuthorizationRead };
        public async Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) =>
            await new ResourceListContextProvider().GetAsync(cancellationToken) with { TenantId = Tenant, Permissions = Permissions };
    }
    private static BunitContext Context(Handler handler, Provider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddSingleton<IConsoleContextProvider>(provider ?? new Provider());
        context.Services.AddSingleton<ConsoleContextState>();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IIdentityAdministrationApiClient>(new IdentityAdministrationApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Handler(Func<int, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public int StatusWrites { get; private set; }
        public int CreateWrites { get; private set; }
        public bool Administrator { get; set; }
        public bool AccountsDenied { get; set; }
        private static LocalAccountResponse Account => new(Guid.NewGuid(), Guid.NewGuid(), "fixture", "Fixture", null, PrincipalStatus.Active, 0, null, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/identity/organization") { Assert.AreEqual(HttpMethod.Get, request.Method); return await load(++Requests); }
            if (path == "/api/identity/administration-capabilities") return new(HttpStatusCode.OK) { Content = JsonContent.Create(new IdentityAdministrationCapabilitiesResponse(Guid.NewGuid(), WorkspaceId, Administrator, Administrator)) };
            if (path == "/api/identity/accounts/" && request.Method == HttpMethod.Get) return AccountsDenied ? new(HttpStatusCode.Forbidden) : new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { Account }) };
            if (path.EndsWith("/status", StringComparison.Ordinal)) { Assert.AreEqual(HttpMethod.Put, request.Method); StatusWrites++; return new(HttpStatusCode.OK) { Content = JsonContent.Create(Account) }; }
            Assert.AreEqual("/api/identity/accounts/", path);
            Assert.AreEqual(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadFromJsonAsync<CreateLocalAccountRequest>(cancellationToken);
            Assert.AreEqual("fixture-new", body!.UserName);
            Assert.AreEqual(WorkspaceId, body.WorkspaceId);
            Assert.AreEqual(BuiltInIdentityRoleNames.Member, body.Role);
            CreateWrites++;
            return new(HttpStatusCode.Created) { Content = JsonContent.Create(Account) };
        }
    }
}
