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
public sealed class OrganizationAccessCollectionTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid FirstRole = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid SecondRole = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    [TestMethod]
    [DataRow("en-US", "No matching results", "No role assignments")]
    [DataRow("fr-FR", "Aucun résultat correspondant", "Aucune attribution de rôle")]
    public async Task CompleteRoleSnapshotSupportsExactFiltersAndDoesNotConfuseMembersWithAssignments(string culture, string noMatches, string noAssignments)
    {
        using var locale = new TestCultureScope(culture);
        var count = 45;
        using var handler = new Handler(_ => Task.FromResult(Response(count, TenantId)));
        using var context = Context(handler);
        var cut = context.Render<OrganizationAccess>();
        Assert.HasCount(20, cut.FindAll("[data-testid=organization-access-row]"));
        Assert.HasCount(5, cut.FindAll(".organization-tabs a"));
        StringAssert.StartsWith(cut.Find(".resource-name-link").GetAttribute("href"), "/settings/organization/members/");
        Assert.IsEmpty(cut.FindAll(".collection-actions"));
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=organization-access-row]"));
        await cut.Find("[data-filter-key=inheritance] select").ChangeAsync(new() { Value = "inherited" });
        Assert.HasCount(15, cut.FindAll("[data-testid=organization-access-row]"));
        await cut.Find("[data-filter-key=role] select").ChangeAsync(new() { Value = FirstRole.ToString("D") });
        Assert.HasCount(8, cut.FindAll("[data-testid=organization-access-row]"));
        await cut.Find(".resource-toolbar button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=scope] select").ChangeAsync(new() { Value = $"/tenants/{TenantId:D}" });
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.HasCount(8, cut.FindAll(".collection-card"));
        await cut.Find("input[type=search]").InputAsync(new() { Value = "absent" });
        Assert.AreEqual(noMatches, cut.Find(".empty-state h2").TextContent);
        await cut.Find(".empty-state button").ClickAsync(new());
        Assert.HasCount(20, cut.FindAll(".collection-card"));
        Assert.AreEqual(1, handler.Requests);
        count = 0; // One member remains in the response, with no assigned role.
        await cut.Find("[data-testid=console-admin-organization-access-refresh]").ClickAsync(new());
        Assert.AreEqual(noAssignments, cut.Find(".empty-state h2").TextContent);
        Assert.AreEqual(2, handler.Requests);
    }

    [TestMethod]
    public async Task FailedRefreshRetainsRowsAndSuccessfulShrinkClampsThePage()
    {
        using var handler = new Handler(call => call == 2 ? throw new HttpRequestException("Private transport detail") : Task.FromResult(Response(call == 1 ? 45 : 3, TenantId)));
        using var context = Context(handler);
        var cut = context.Render<OrganizationAccess>();
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        await cut.Find("[data-testid=console-admin-organization-access-refresh]").ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=organization-access-row]"));
        Assert.IsFalse(cut.Markup.Contains("Private transport detail", StringComparison.Ordinal));
        await cut.Find("[role=alert] button").ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("[data-testid=organization-access-row]"));
        Assert.IsEmpty(cut.FindAll(".collection-page-number"));
        Assert.AreEqual(3, handler.Requests);
    }

    [TestMethod]
    public async Task TenantReplacementUnderTheSameLogicalScopeRejectsLateRowsAndForbiddenRefreshClearsThem()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var replacement = new TaskCompletionSource<HttpResponseMessage>();
        var provider = new Provider();
        using var handler = new Handler(call => call switch { 1 => Task.FromResult(Response(1, TenantId)), 2 => old.Task, 3 => replacement.Task, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) });
        using var context = Context(handler, provider);
        var cut = context.Render<OrganizationAccess>();
        var refresh = cut.Find("[data-testid=console-admin-organization-access-refresh]").ClickAsync(new());
        provider.Tenant = Guid.NewGuid();
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("[data-testid=organization-access-row]")));
        replacement.SetResult(Response(2, provider.Tenant));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll("[data-testid=organization-access-row]")));
        old.SetResult(Response(4, TenantId));
        await refresh;
        Assert.HasCount(2, cut.FindAll("[data-testid=organization-access-row]"));
        await cut.Find("[data-testid=console-admin-organization-access-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[data-testid=organization-access-row]"));
    }

    [TestMethod]
    public void AuthorizationReadIsRequiredAndResourceReadIsNotInvented()
    {
        var provider = new Provider { Permissions = new HashSet<string>() };
        using var handler = new Handler(_ => Task.FromResult(Response(1, TenantId)));
        using (var context = Context(handler, provider))
        {
            var cut = context.Render<OrganizationAccess>();
            Assert.IsEmpty(cut.FindAll("[data-testid=organization-access-row]"));
            Assert.AreEqual(0, handler.Requests);
        }
        provider.Permissions = new HashSet<string> { AuthorizationPermissions.AuthorizationRead };
        using var reader = Context(handler, provider);
        var list = reader.Render<OrganizationAccess>();
        Assert.HasCount(1, list.FindAll("[data-testid=organization-access-row]"));
        Assert.AreEqual(1, handler.Requests);
    }

    private static HttpResponseMessage Response(int count, Guid tenant) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new OrganizationAdministrationResponse(new(tenant, "tenant", "Tenant", TenantStatus.Active, DateTimeOffset.UnixEpoch),
            [new(WorkspaceId, tenant, "team", "Team", WorkspaceStatus.Active, DateTimeOffset.UnixEpoch)],
            Enumerable.Range(0, Math.Max(1, count)).Select(index =>
            {
                var principal = Guid.ParseExact((index + 1).ToString("x32", CultureInfo.InvariantCulture), "N");
                return new OrganizationMemberResponse(new(principal, PrincipalKind.Human, $"Member {index}", null, PrincipalStatus.Active, DateTimeOffset.UnixEpoch),
                    new(Guid.NewGuid(), tenant, principal, MembershipStatus.Active, DateTimeOffset.UnixEpoch), [], count == 0 ? [] :
                    [new(new(index % 2 == 0 ? FirstRole : SecondRole, index % 2 == 0 ? "first" : "second", index % 2 == 0 ? "First role" : "Second role", [], false), index % 3 == 0 ? $"/tenants/{tenant:D}" : $"/workspaces/{WorkspaceId:D}")]);
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/identity/organization", request.RequestUri!.PathAndQuery);
            return load(++Requests);
        }
    }
}
