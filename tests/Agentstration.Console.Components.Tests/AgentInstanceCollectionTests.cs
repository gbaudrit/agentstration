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
public sealed class AgentInstanceCollectionTests
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task BoundedSnapshotShowsLoadedRowsWithoutInventingGlobalQueriesOrPaging(string cultureName)
    {
        using var culture = new TestCultureScope(cultureName);
        using var handler = new Handler(call => Task.FromResult(Response(call == 2 ? 0 : 45)));
        using var context = Context(handler);
        var cut = context.Render<Deployments>();
        Assert.HasCount(45, cut.FindAll("tbody tr"));
        Assert.IsEmpty(cut.FindAll("input[type=search], [data-filter-key], .collection-pager, .collection-actions"));
        StringAssert.Contains(cut.Find(".resource-name-link").GetAttribute("href")!, "/flow-runs/run-0?scopeRef=%2Finstance");
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.HasCount(45, cut.FindAll(".collection-card"));
        Assert.AreEqual(1, handler.Requests);
        await cut.Find("[data-testid=runtime-agent-instances-refresh]").ClickAsync(new());
        Assert.IsNotNull(cut.Find("[data-testid=runtime-agent-instances-empty]"));
        Assert.IsEmpty(cut.FindAll(".collection-card, .collection-pager"));
        await cut.Find("[data-testid=runtime-agent-instances-refresh]").ClickAsync(new());
        Assert.HasCount(45, cut.FindAll(".collection-card"));
        Assert.AreEqual(3, handler.Requests);
    }

    [TestMethod]
    public async Task RefreshFailureRetainsObservedCardsAndRetryReplacesTheSnapshot()
    {
        using var handler = new Handler(call => call == 2 ? throw new HttpRequestException("Offline") : Task.FromResult(Response(call == 1 ? 45 : 3)));
        using var context = Context(handler);
        var cut = context.Render<Deployments>();
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        await cut.Find("[data-testid=runtime-agent-instances-refresh]").ClickAsync(new());
        Assert.HasCount(45, cut.FindAll(".collection-card"));
        await cut.Find("[role=alert] button").ClickAsync(new());
        Assert.HasCount(3, cut.FindAll(".collection-card"));
        Assert.AreEqual(3, handler.Requests);
    }

    [TestMethod]
    public async Task ContextReplacementRejectsLateResultsAndForbiddenRefreshClearsCachedRows()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var replacement = new TaskCompletionSource<HttpResponseMessage>();
        var provider = new ResourceListContextProvider { Permissions = new HashSet<string> { AuthorizationPermissions.RunsRead } };
        using var handler = new Handler(call => call switch
        {
            1 => Task.FromResult(Response(1)),
            2 => old.Task,
            3 => replacement.Task,
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))
        });
        using var context = Context(handler, provider);
        var cut = context.Render<Deployments>();
        var refresh = cut.Find("[data-testid=runtime-agent-instances-refresh]").ClickAsync(new());
        provider.Scope = $"/workspaces/{Guid.NewGuid():D}";
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".resource-name-link")));
        replacement.SetResult(Response(2));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll(".resource-name-link")));
        old.SetResult(Response(4));
        await refresh;
        Assert.HasCount(2, cut.FindAll(".resource-name-link"));
        await cut.Find("[data-testid=runtime-agent-instances-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll(".resource-name-link, .collection-actions"));
    }

    [TestMethod]
    public void DeniedRunsReadMakesNoRequestAndAuthorizedReadsHaveNoMutationCommands()
    {
        var provider = new ResourceListContextProvider { Permissions = new HashSet<string>() };
        using var handler = new Handler(_ => Task.FromResult(Response(1)));
        using (var context = Context(handler, provider))
        {
            var cut = context.Render<Deployments>();
            Assert.IsEmpty(cut.FindAll(".resource-name-link, .collection-actions"));
            Assert.AreEqual(0, handler.Requests);
        }
        provider.Permissions = new HashSet<string> { AuthorizationPermissions.RunsRead };
        using var reader = Context(handler, provider);
        var list = reader.Render<Deployments>();
        Assert.HasCount(1, list.FindAll(".resource-name-link"));
        Assert.IsEmpty(list.FindAll(".collection-actions"));
    }

    private static HttpResponseMessage Response(int count) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(Enumerable.Range(0, count).Select(index => new AgentInstanceResponse(
            $"instance-{index}", new("actors"), $"Agent {index}", 3, "revision-3", "maf-local",
            index % 2 == 0 ? "FlowRun" : "RuntimeRun", $"run-{index}", "participant", Guid.Empty, 2,
            Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(30), ResourceScopeRef.Instance)).ToArray())
    };
    private static BunitContext Context(Handler handler, ResourceListContextProvider? provider = null)
    {
        provider ??= new ResourceListContextProvider { Permissions = new HashSet<string> { AuthorizationPermissions.RunsRead } };
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
