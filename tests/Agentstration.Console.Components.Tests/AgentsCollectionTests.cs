using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Agentstration.Agents;
using Agentstration.Api.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using AgentsPage = Agentstration.Web.Components.Pages.Agents;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class AgentsCollectionTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly string Scope = $"/workspaces/{WorkspaceId:D}";
    [TestMethod]
    [DataRow("en-US", "Unknown", "No deployment observed")]
    [DataRow("fr-FR", "Inconnu", "Aucun déploiement observé")]
    public async Task OffsetPagesOmitUnsupportedQueriesTotalsAndUseUnknownForMissingDeployment(string culture, string unknown, string noDeployment)
    {
        using var locale = new TestCultureScope(culture);
        using var handler = new Handler((skip, take, _) => Task.FromResult(Page(skip, take, 25)));
        using var context = Context(handler);
        var cut = context.Render<AgentsPage>();
        Assert.HasCount(20, cut.FindAll("[data-testid=agent-row]"));
        Assert.IsEmpty(cut.FindAll("input[type=search], [data-filter-key], .collection-page-number, .metric-card"));
        StringAssert.Contains(cut.Find("[data-testid=agent-row]").TextContent, unknown);
        StringAssert.Contains(cut.Find("[data-testid=agent-row]").TextContent, noDeployment);
        StringAssert.Contains(cut.Find(".resource-name-link").GetAttribute("href"), $"scopeRef={Uri.EscapeDataString(Scope)}");
        await cut.Find(".collection-pager button:last-child").ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=agent-row]"));
        Assert.AreEqual(20, handler.LastSkip);
        Assert.IsTrue(cut.Find(".collection-pager button:last-child").HasAttribute("disabled"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll(".collection-card"));
    }

    [TestMethod]
    public async Task FailedNextPageRetainsSuccessfulPagerAndRetryRequestsTheFailedOffset()
    {
        using var handler = new Handler((skip, take, call) => call == 2 ? throw new HttpRequestException("Private transport detail") : Task.FromResult(Page(skip, take, 25)));
        using var context = Context(handler);
        var cut = context.Render<AgentsPage>();
        await cut.Find(".collection-pager button:last-child").ClickAsync(new());
        Assert.HasCount(20, cut.FindAll("[data-testid=agent-row]"));
        Assert.IsFalse(cut.Markup.Contains("Private transport detail", StringComparison.Ordinal));
        await cut.Find("[role=alert] button").ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=agent-row]"));
        Assert.AreEqual(20, handler.LastSkip);
        Assert.AreEqual(3, handler.PageReads);
    }

    [TestMethod]
    public async Task TerminalEmptyPageRetainsPreviousInsteadOfClaimingTheCatalogIsEmpty()
    {
        using var handler = new Handler((skip, take, _) => Task.FromResult(Page(skip, take, 20)));
        using var context = Context(handler);
        var cut = context.Render<AgentsPage>();
        await cut.Find(".collection-pager button:last-child").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[data-testid=agent-row]"));
        Assert.HasCount(1, cut.FindAll(".empty-state"));
        Assert.IsFalse(cut.FindAll(".collection-pager button")[0].HasAttribute("disabled"));
        await cut.FindAll(".collection-pager button")[0].ClickAsync(new());
        Assert.HasCount(20, cut.FindAll("[data-testid=agent-row]"));
    }

    [TestMethod]
    public async Task ContextNotificationClearsRowsInspectorAndRejectsLateResponseAndDenial()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var next = new TaskCompletionSource<HttpResponseMessage>();
        using var handler = new Handler((skip, take, call) => call switch { 1 => Task.FromResult(Page(skip, take, 1)), 2 => old.Task, 3 => next.Task, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) });
        using var context = Context(handler);
        var cut = context.Render<AgentsPage>();
        await cut.FindAll(".collection-actions-menu button").Last().ClickAsync(new());
        Assert.HasCount(1, cut.FindAll(".quick-inspector"));
        var refresh = cut.Find("[data-testid=resource-agents-refresh]").ClickAsync(new());
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("[data-testid=agent-row], .quick-inspector")));
        next.SetResult(Page(0, 20, 2));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll("[data-testid=agent-row]")));
        old.SetResult(Page(0, 20, 4));
        await refresh;
        Assert.HasCount(2, cut.FindAll("[data-testid=agent-row]"));
        await cut.Find("[data-testid=resource-agents-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[data-testid=agent-row]"));
    }

    [TestMethod]
    public async Task DeleteRequiresConfirmationAndReadsLatestScopedEtagBeforeWriting()
    {
        using var handler = new Handler((skip, take, _) => Task.FromResult(Page(skip, take, 1)));
        using var context = Context(handler);
        var cut = context.Render<AgentsPage>();
        await cut.Find(".danger-text").ClickAsync(new());
        Assert.AreEqual(0, handler.Deletes);
        await cut.Find("dialog .button-secondary").ClickAsync(new());
        Assert.AreEqual(0, handler.Deletes);
        await cut.Find(".danger-text").ClickAsync(new());
        await cut.Find("dialog .button-danger").ClickAsync(new());
        Assert.AreEqual(1, handler.Deletes);
        Assert.AreEqual(1, handler.DetailReads);
    }

    private static AgentResource Agent(int index) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = AgentResourceKinds.Agent,
        Metadata = new() { Name = $"agent-{index}" },
        ScopeRef = ResourceScopeRef.Workspace(WorkspaceId),
        Generation = 1,
        Definition = new() { DisplayName = $"Agent {index}", Instructions = "Public fixture instructions", ModelProfile = new("fixture-profile") }
    };
    private static HttpResponseMessage Page(int skip, int take, int count) => new(HttpStatusCode.OK) { Content = JsonContent.Create(new PagedResponse<AgentResource>(Enumerable.Range(0, count).Skip(skip).Take(take).Select(Agent).ToArray(), skip + take <= count ? $"/api/agents?allNamespaces=true&skip={skip + take}&top={take}" : null)) };
    private sealed class NavigationContext : IConsoleNavigationContext
    {
        public string ScopeRef => Scope;
        public string AddToUri(string uri) => QueryHelpers.AddQueryString(uri, "context", Scope);
    }
    private static BunitContext Context(Handler handler)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(new ResourceListContextProvider { Scope = Scope, Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite, AuthorizationPermissions.ResourcesDelete, AuthorizationPermissions.RunsExecute } });
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext());
        context.Services.AddSingleton(new NotificationState());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IManagementApiClient>(new ManagementApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Handler(Func<int, int, int, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public int PageReads { get; private set; }
        public int LastSkip { get; private set; }
        public int DetailReads { get; private set; }
        public int Deletes { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/api/agents")
            {
                Assert.AreEqual(HttpMethod.Get, request.Method);
                var query = QueryHelpers.ParseQuery(uri.Query);
                Assert.AreEqual("true", query["allNamespaces"].ToString());
                LastSkip = int.Parse(query["skip"].ToString(), CultureInfo.InvariantCulture);
                return load(LastSkip, int.Parse(query["top"].ToString(), CultureInfo.InvariantCulture), ++PageReads);
            }
            if (uri.AbsolutePath == "/api/deployments") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PagedResponse<AgentDeployment>([], null)) });
            Assert.AreEqual($"/api/agents/agent-0?scopeRef={Uri.EscapeDataString(Scope)}", uri.PathAndQuery);
            if (request.Method == HttpMethod.Delete)
            {
                Assert.AreEqual("\"latest\"", request.Headers.IfMatch.Single().ToString()); Deletes++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            Assert.AreEqual(HttpMethod.Get, request.Method);
            DetailReads++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Agent(0)) };
            response.Headers.ETag = new("\"latest\"");
            return Task.FromResult(response);
        }
    }
}
