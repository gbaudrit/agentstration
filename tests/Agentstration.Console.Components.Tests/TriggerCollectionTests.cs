using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Triggers;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class TriggerCollectionTests
{
    [TestMethod]
    [DataRow("en-US", "No matching results")]
    [DataRow("fr-FR", "Aucun résultat correspondant")]
    public async Task FiltersUseTheWholeCatalogAndCardsSurviveNoMatches(string cultureName, string noMatches)
    {
        using var culture = new TestCultureScope(cultureName);
        using var handler = new Handler(_ => Task.FromResult(Response(45)));
        using var context = Context(handler);
        var cut = context.Render<Agentstration.Web.Components.Pages.Triggers>();
        cut.WaitForAssertion(() => Assert.HasCount(20, cut.FindAll("tbody tr")));
        Assert.IsEmpty(cut.FindAll(".collection-actions"));
        Assert.AreEqual("45", cut.FindAll(".metric-card strong").First().TextContent.Trim());
        var originalUrl = cut.Find(".resource-name-link").GetAttribute("href");
        StringAssert.Contains(originalUrl!, "/triggers/trigger-0?namespace=schedules&scopeRef=%2Finstance");
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("tbody tr"));
        await cut.Find("[data-filter-key=enabled] select").ChangeAsync(new() { Value = "true" });
        Assert.HasCount(20, cut.FindAll("tbody tr"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        await cut.Find(".resource-toolbar button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=scope] select").ChangeAsync(new() { Value = "/instance" });
        await cut.Find("[data-filter-key=namespace] select").ChangeAsync(new() { Value = "schedules" });
        await cut.Find("[data-filter-key=target] input").InputAsync(new() { Value = "flow" });
        await cut.Find("[data-filter-key=schedule] select").ChangeAsync(new() { Value = "Interval" });
        Assert.HasCount(20, cut.FindAll(".collection-card"));
        await cut.Find("[data-filter-key=outcome] select").ChangeAsync(new() { Value = "Failed" });
        Assert.HasCount(8, cut.FindAll(".collection-card"));
        Assert.AreEqual("45", cut.FindAll(".metric-card strong").First().TextContent.Trim());
        await cut.Find("input[type=search]").InputAsync(new() { Value = "absent" });
        Assert.AreEqual(noMatches, cut.Find(".empty-state h2").TextContent);
        Assert.IsEmpty(cut.FindAll(".collection-card, .collection-pager"));
        await cut.Find(".empty-state button").ClickAsync(new());
        Assert.HasCount(20, cut.FindAll(".collection-card"));
        Assert.AreEqual(1, handler.Requests);
    }

    [TestMethod]
    public async Task RefreshFailureKeepsRowsAndShrinkClampsTheSuccessfulPage()
    {
        using var handler = new Handler(call => call == 2 ? throw new HttpRequestException("Offline") : Task.FromResult(Response(call == 1 ? 45 : 3)));
        using var context = Context(handler);
        var cut = context.Render<Agentstration.Web.Components.Pages.Triggers>();
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        await cut.Find("[data-testid=resource-triggers-refresh]").ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("tbody tr"));
        Assert.IsNotNull(cut.Find("[role=alert]"));
        await cut.Find("[role=alert] button").ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("tbody tr"));
        Assert.IsEmpty(cut.FindAll(".collection-page-number"));
        Assert.AreEqual(3, handler.Requests);
    }

    [TestMethod]
    public async Task ContextReplacementRejectsLateResultsAndForbiddenRefreshClearsCachedRows()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var replacement = new TaskCompletionSource<HttpResponseMessage>();
        var provider = new ResourceListContextProvider();
        using var handler = new Handler(call => call switch
        {
            1 => Task.FromResult(Response(1)),
            2 => old.Task,
            3 => replacement.Task,
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))
        });
        using var context = Context(handler, provider);
        var cut = context.Render<Agentstration.Web.Components.Pages.Triggers>();
        var refresh = cut.Find("[data-testid=resource-triggers-refresh]").ClickAsync(new());
        provider.Scope = $"/workspaces/{Guid.NewGuid():D}";
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".resource-name-link")));
        replacement.SetResult(Response(2));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll(".resource-name-link")));
        old.SetResult(Response(4));
        await refresh;
        Assert.HasCount(2, cut.FindAll(".resource-name-link"));
        await cut.Find("[data-testid=resource-triggers-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll(".resource-name-link, a[href*='/triggers/new']"));
    }

    [TestMethod]
    public void DeniedReadMakesNoRequestAndReaderHasNoCreateCommand()
    {
        var provider = new ResourceListContextProvider { Permissions = new HashSet<string>() };
        using var handler = new Handler(_ => Task.FromResult(Response(1)));
        using (var context = Context(handler, provider))
        {
            var cut = context.Render<Agentstration.Web.Components.Pages.Triggers>();
            Assert.IsEmpty(cut.FindAll(".resource-name-link, a[href*='/triggers/new']"));
            Assert.AreEqual(0, handler.Requests);
        }
        provider.Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead };
        using var reader = Context(handler, provider);
        var list = reader.Render<Agentstration.Web.Components.Pages.Triggers>();
        Assert.HasCount(1, list.FindAll(".resource-name-link"));
        Assert.IsEmpty(list.FindAll("a[href*='/triggers/new']"));
    }

    [TestMethod]
    public async Task OperationalWorkspaceChangeReloadsEvenWhenLogicalScopeIsUnchanged()
    {
        var provider = new WorkspaceProvider();
        using var handler = new Handler(call => Task.FromResult(Response(call)));
        using var context = Context(handler, provider);
        var cut = context.Render<Agentstration.Web.Components.Pages.Triggers>();
        Assert.HasCount(1, cut.FindAll(".resource-name-link"));
        provider.WorkspaceId = Guid.NewGuid();
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll(".resource-name-link")));
        Assert.AreEqual(2, handler.Requests);
    }

    private sealed class WorkspaceProvider : IConsoleContextProvider
    {
        public Guid WorkspaceId { get; set; } = Guid.NewGuid();
        public async Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) =>
            await new ResourceListContextProvider().GetAsync(cancellationToken) with { WorkspaceId = WorkspaceId };
    }

    private static HttpResponseMessage Response(int count) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(Enumerable.Range(0, count).Select(index => new TriggerResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = TriggerResourceKinds.Trigger,
            Metadata = new() { Name = $"trigger-{index}", Namespace = new("schedules") },
            ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("99999999-0000-0000-0000-000000000001")),
            Definition = new()
            {
                DisplayName = $"Trigger {index}",
                Enabled = index % 2 == 0,
                Source = new() { Schedule = new() { Type = TriggerScheduleType.Interval, Every = "PT1H", StartAt = DateTimeOffset.UnixEpoch } },
                Target = new() { Flow = new() { Name = "flow", ScopeRef = ResourceScopeRef.Instance, Namespace = new("flows"), Version = "1.0.0" } }
            },
            Observed = new() { NextOccurrenceAt = DateTimeOffset.UnixEpoch.AddMinutes(index), LastOutcome = index % 3 == 0 ? TriggerLastOutcome.Failed : TriggerLastOutcome.None }
        }).ToArray())
    };
    private static BunitContext Context(Handler handler, IConsoleContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddSingleton<IConsoleContextProvider>(provider ?? new ResourceListContextProvider());
        context.Services.AddSingleton<ConsoleContextState>();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ITriggerApiClient>(new TriggerApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Handler(Func<int, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => load(++Requests);
    }
}
