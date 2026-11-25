using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class WorkerCollectionTests
{
    [TestMethod]
    [DataRow("en-US", "No matching results")]
    [DataRow("fr-FR", "Aucun résultat correspondant")]
    public async Task FiltersUseTheWholeCatalogAndCardsSurviveNoMatches(string cultureName, string noMatches)
    {
        using var culture = new TestCultureScope(cultureName);
        using var handler = new Handler(_ => Task.FromResult(Response(45)));
        using var context = Context(handler);
        var cut = context.Render<RuntimeWorkers>();
        cut.WaitForAssertion(() => Assert.HasCount(20, cut.FindAll("tbody tr")));
        Assert.HasCount(20, cut.FindAll(".collection-actions"));
        var originalUrl = cut.Find(".resource-name-link").GetAttribute("href");
        StringAssert.Contains(originalUrl!, "/settings/runtime-workers/00000001-0000-0000-0000-000000000000");
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("tbody tr"));
        await cut.Find("[data-filter-key=presence] select").ChangeAsync(new() { Value = "Online" });
        Assert.HasCount(20, cut.FindAll("tbody tr"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        await cut.Find(".resource-toolbar button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=enrollment] select").ChangeAsync(new() { Value = "Active" });
        await cut.Find("[data-filter-key=capability] select").ChangeAsync(new() { Value = "microsoft-agent-framework" });
        await cut.Find("[data-filter-key=version] input").InputAsync(new() { Value = "1.4.0" });
        Assert.HasCount(20, cut.FindAll(".collection-card"));
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
        var cut = context.Render<RuntimeWorkers>();
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        await cut.Find("[data-testid=runtime-workers-refresh]").ClickAsync(new());
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
        var cut = context.Render<RuntimeWorkers>();
        var refresh = cut.Find("[data-testid=runtime-workers-refresh]").ClickAsync(new());
        provider.Scope = $"/workspaces/{Guid.NewGuid():D}";
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".resource-name-link")));
        replacement.SetResult(Response(2));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll(".resource-name-link")));
        old.SetResult(Response(4));
        await refresh;
        Assert.HasCount(2, cut.FindAll(".resource-name-link"));
        await cut.Find("[data-testid=runtime-workers-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll(".resource-name-link, .collection-pager"));
    }

    [TestMethod]
    public void ApiPlatformAdminPolicyRemainsAuthoritativeAndDeniedAccessShowsNoCachedWorkers()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var context = Context(handler);
        var cut = context.Render<RuntimeWorkers>();
        Assert.IsEmpty(cut.FindAll(".resource-name-link, .collection-pager"));
        Assert.IsTrue(cut.FindAll("[data-filter-key] input, [data-filter-key] select").All(control => control.HasAttribute("disabled")));
        Assert.HasCount(1, cut.FindAll("[data-filter-key=enrollment] option"));
        Assert.AreEqual(1, handler.Requests);
    }

    [TestMethod]
    public async Task SameIdentityContextNotificationClearsPrivilegedRowsBeforeApiReauthorization()
    {
        var next = new TaskCompletionSource<HttpResponseMessage>();
        using var handler = new Handler(call => call == 1 ? Task.FromResult(Response(1)) : next.Task);
        using var context = Context(handler);
        var cut = context.Render<RuntimeWorkers>();
        Assert.HasCount(1, cut.FindAll(".resource-name-link"));
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".resource-name-link")));
        next.SetResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("[data-testid=runtime-workers-refresh]").HasAttribute("disabled")));
        Assert.IsEmpty(cut.FindAll(".resource-name-link, .collection-pager"));
        Assert.HasCount(1, cut.FindAll("[data-filter-key=enrollment] option"));
    }

    private static HttpResponseMessage Response(int count) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(Enumerable.Range(0, count).Select(index => new RuntimeWorkerSummaryResponse(
            new Guid(index + 1, 0, 0, new byte[8]), $"Worker {index}", "1", "Active",
            index % 2 == 0 ? RuntimeWorkerPresenceState.Online : RuntimeWorkerPresenceState.Offline,
            Guid.NewGuid(), "1.4.0+build", 4, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [new("microsoft-agent-framework", "1.0", ["1.0"], "maf-1.0")])).ToArray())
    };
    private static BunitContext Context(Handler handler, ResourceListContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(provider);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IRuntimeApiClient>(new RuntimeApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Handler(Func<int, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => load(++Requests);
    }
}
