using System.Net;
using System.Net.Http.Json;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class SourcesCollectionTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    [TestMethod]
    [DataRow("en-US", "No matching results")]
    [DataRow("fr-FR", "Aucun résultat correspondant")]
    public async Task CompleteCatalogHasExactPagesAndFiltersWhileMetricsDescribeTheCompleteResponse(string culture, string noMatches)
    {
        using var locale = new TestCultureScope(culture);
        using var handler = new Handler(_ => Task.FromResult(Response(45)));
        using var context = Context(handler);
        var cut = context.Render<Agentstration.Web.Components.Pages.Sources>();
        Assert.HasCount(20, cut.FindAll("[data-testid=source-row]"));
        Assert.HasCount(3, cut.FindAll(".metric-card"));
        StringAssert.Contains(cut.FindAll(".metric-card")[0].TextContent, "45");
        Assert.HasCount(20, cut.FindAll(".collection-actions"));
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=source-row]"));
        await cut.Find("[data-filter-key=health] select").ChangeAsync(new() { Value = "Rejected" });
        Assert.HasCount(15, cut.FindAll("[data-testid=source-row]"));
        await cut.Find("[data-filter-key=origin] select").ChangeAsync(new() { Value = "pasted" });
        Assert.HasCount(15, cut.FindAll("[data-testid=source-row]"));
        await cut.Find(".resource-toolbar button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=publisher] select").ChangeAsync(new() { Value = "contoso" });
        Assert.HasCount(8, cut.FindAll("[data-testid=source-row]"));
        await cut.Find("[data-filter-key=scope] select").ChangeAsync(new() { Value = $"/workspaces/{WorkspaceId:D}" });
        Assert.AreEqual(noMatches, cut.Find(".empty-state h2").TextContent);
        await cut.Find(".empty-state button").ClickAsync(new());
        await cut.Find("input[type=search]").InputAsync(new() { Value = "Source 44" });
        Assert.HasCount(1, cut.FindAll("[data-testid=source-row]"));
        StringAssert.Contains(cut.Find(".resource-name-link").GetAttribute("href"), "scopeRef=%2Finstance");
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.HasCount(1, cut.FindAll(".collection-card"));
        StringAssert.Contains(cut.FindAll(".metric-card")[0].TextContent, "45");
        Assert.AreEqual(1, handler.Requests);
    }

    [TestMethod]
    public async Task RefreshRetainsLastPageOnFailureAndClampsAfterShrink()
    {
        using var handler = new Handler(call => call == 2 ? throw new HttpRequestException("Private transport detail") : Task.FromResult(Response(call == 1 ? 45 : 3)));
        using var context = Context(handler);
        var cut = context.Render<Agentstration.Web.Components.Pages.Sources>();
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        await cut.Find("[data-testid=sources-collection-refresh]").ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=source-row]"));
        Assert.IsFalse(cut.Markup.Contains("Private transport detail", StringComparison.Ordinal));
        await cut.Find("[role=alert] button").ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("[data-testid=source-row]"));
        Assert.IsEmpty(cut.FindAll(".collection-page-number"));
    }

    [TestMethod]
    public async Task PhysicalWorkspaceReplacementRejectsLateCatalogAndApiDenialClearsMetadataAndImportCommands()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var replacement = new TaskCompletionSource<HttpResponseMessage>();
        var provider = new Provider();
        using var handler = new Handler(call => call switch { 1 => Task.FromResult(Response(1)), 2 => old.Task, 3 => replacement.Task, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) });
        using var context = Context(handler, provider);
        var cut = context.Render<Agentstration.Web.Components.Pages.Sources>();
        var refresh = cut.Find("[data-testid=sources-collection-refresh]").ClickAsync(new());
        provider.Workspace = Guid.NewGuid();
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("[data-testid=source-row], .metric-card, [data-testid=source-import-open]")));
        replacement.SetResult(Response(2));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll("[data-testid=source-row]")));
        old.SetResult(Response(4));
        await refresh;
        Assert.HasCount(2, cut.FindAll("[data-testid=source-row]"));
        await cut.Find("[data-testid=sources-collection-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[data-testid=source-row], .metric-card, [data-testid=source-import-open]"));
    }

    private static HttpResponseMessage Response(int count) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(Enumerable.Range(0, count).Select(index =>
    {
        var uid = Guid.NewGuid();
        var publisher = index % 2 == 0 ? "contoso" : "fabrikam";
        var scope = index % 2 == 0 ? ResourceScopeRef.Instance : ResourceScopeRef.Workspace(WorkspaceId);
        return new SourceConsoleListItem(new(
            new() { Uid = uid, ApiVersion = ResourceApiVersions.CoreV1, Kind = SourceResourceKinds.Source, Metadata = new() { Name = $"source-{index}", Namespace = new(publisher) }, ScopeRef = scope, Definition = new() { Publisher = publisher } },
            new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = SourceResourceKinds.SourceConfiguration, Metadata = new() { Name = $"source-{index}" }, ScopeRef = scope, Definition = new() { SourceUid = uid, DisplayName = $"Source {index}" } },
            new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = SourceResourceKinds.SourceObservedState, Metadata = new() { Name = $"source-{index}" }, ScopeRef = scope, Definition = new() { SourceUid = uid, LastAttemptAt = DateTimeOffset.UnixEpoch, LastOutcome = index % 3 == 0 ? SourceImportOutcome.Rejected : SourceImportOutcome.Created } }, 1), null);
    }).ToArray())
    };
    private sealed class Provider : IConsoleContextProvider
    {
        public Guid Workspace { get; set; } = WorkspaceId;
        public async Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => await new ResourceListContextProvider().GetAsync(cancellationToken) with { WorkspaceId = Workspace, Permissions = new HashSet<string>() };
    }
    private static BunitContext Context(Handler handler, Provider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConsoleContextProvider>(provider ?? new Provider());
        context.Services.AddSingleton<ConsoleContextState>();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton(new NotificationState());
        context.Services.AddSingleton<ISourceConsoleApiClient>(new SourceConsoleApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Handler(Func<int, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/sources/console", request.RequestUri!.PathAndQuery);
            return load(++Requests);
        }
    }
}
