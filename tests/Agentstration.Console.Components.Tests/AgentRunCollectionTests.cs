using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class AgentRunCollectionTests
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task BoundedPagesExposeNoLocalFiltersOrInventedTotalAndRetainCards(string culture)
    {
        using var locale = new TestCultureScope(culture);
        using var handler = new Handler((_, uri) => Task.FromResult(Response(uri.Query.Contains("skip=20", StringComparison.Ordinal) ? "second" : "first", true)));
        using var context = Context(handler);
        var cut = context.Render<AgentRuns>();
        Assert.AreEqual(1, handler.Requests.Count);
        StringAssert.Contains(cut.Markup, "Deterministic output");
        StringAssert.Contains(cut.Markup, "0:00:03");
        Assert.AreEqual(DateTimeOffset.UnixEpoch.ToString("O"), cut.Find("time").GetAttribute("datetime"));
        StringAssert.Contains(handler.Requests[0].Query, "scopeRef=%2Finstance&skip=0&top=20");
        Assert.IsEmpty(cut.FindAll("input[type=search], [data-filter-key], .collection-page-number, .collection-pager [role=status]"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.AreEqual(1, handler.Requests.Count);
        await cut.Find(".collection-pager button:last-child").ClickAsync(new());
        Assert.AreEqual(2, handler.Requests.Count);
        StringAssert.Contains(handler.Requests[1].Query, "skip=20&top=20");
        Assert.HasCount(1, cut.FindAll(".collection-card"));
        StringAssert.Contains(cut.Find(".resource-name-link").GetAttribute("href")!, "/runs/second?scopeRef=%2Finstance");
        await cut.Find(".collection-page-size select").ChangeAsync(new() { Value = "50" });
        StringAssert.Contains(handler.Requests[2].Query, "skip=0&top=50");
    }

    [TestMethod]
    public async Task FailedPageKeepsPreviousSuccessfulFactsAndRetriesTheActualRequest()
    {
        using var handler = new Handler((call, _) => call == 2 ? throw new HttpRequestException("Offline") : Task.FromResult(Response(call == 1 ? "first" : "second", true)));
        using var context = Context(handler);
        var cut = context.Render<AgentRuns>();
        await cut.Find(".collection-pager button:last-child").ClickAsync(new());
        Assert.IsTrue(cut.Find(".collection-pager button:first-of-type").HasAttribute("disabled"));
        StringAssert.Contains(cut.Find(".resource-name-link").TextContent, "first");
        await cut.Find("[role=alert] button").ClickAsync(new());
        StringAssert.Contains(handler.Requests[2].Query, "skip=20&top=20");
        StringAssert.Contains(cut.Find(".resource-name-link").TextContent, "second");
        Assert.IsFalse(cut.Find(".collection-pager button:first-of-type").HasAttribute("disabled"));
    }

    [TestMethod]
    public async Task EmptyTerminalWindowRetainsPreviousControlWithoutInventingAnEmptyCatalog()
    {
        using var handler = new Handler((call, _) => Task.FromResult(call == 2 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new RuntimeRunPageResponse([], null)) } : Response("first", true)));
        using var context = Context(handler);
        var cut = context.Render<AgentRuns>();
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        await cut.Find(".collection-pager button:last-child").ClickAsync(new());
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.IsFalse(cut.Find(".collection-pager button:first-of-type").HasAttribute("disabled"));
        Assert.IsTrue(cut.Find(".collection-pager button:last-child").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll(".resource-name-link"));
        await cut.Find(".collection-pager button:first-of-type").ClickAsync(new());
        Assert.HasCount(1, cut.FindAll(".collection-card"));
        StringAssert.Contains(handler.Requests[2].Query, "skip=0&top=20");
    }

    [TestMethod]
    public async Task ContextReplacementRejectsLatePageAndForbiddenRefreshRemovesRowsAndCommands()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var replacement = new TaskCompletionSource<HttpResponseMessage>();
        var provider = new ResourceListContextProvider { Permissions = new HashSet<string> { AuthorizationPermissions.RunsRead } };
        using var handler = new Handler((call, _) => call switch { 1 => Task.FromResult(Response("first", true)), 2 => old.Task, 3 => replacement.Task, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) });
        using var context = Context(handler, provider);
        var cut = context.Render<AgentRuns>();
        var next = cut.Find(".collection-pager button:last-child").ClickAsync(new());
        provider.Scope = ResourceScopeRef.Workspace(Guid.NewGuid()).Value;
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".resource-name-link")));
        replacement.SetResult(Response("replacement", false));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".resource-name-link").TextContent, "replacement"));
        StringAssert.Contains(handler.Requests[2].Query, "skip=0&top=20");
        old.SetResult(Response("old", true));
        await next;
        StringAssert.Contains(cut.Find(".resource-name-link").TextContent, "replacement");
        await cut.Find("[data-testid=flow-observability-agent-runs-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll(".resource-name-link, .collection-pager, .collection-actions"));
    }

    [TestMethod]
    public void MissingRunsReadMakesNoRequestAndAuthorizedReadsHaveNoRowMutations()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response("readable", false)));
        var provider = new ResourceListContextProvider { Permissions = new HashSet<string>() };
        using (var context = Context(handler, provider))
        {
            var cut = context.Render<AgentRuns>();
            Assert.IsEmpty(cut.FindAll(".resource-name-link, .collection-actions"));
            Assert.AreEqual(0, handler.Requests.Count);
        }
        provider.Permissions = new HashSet<string> { AuthorizationPermissions.RunsRead };
        using var reader = Context(handler, provider);
        var list = reader.Render<AgentRuns>();
        Assert.HasCount(1, list.FindAll(".resource-name-link"));
        Assert.IsEmpty(list.FindAll(".collection-actions"));
    }

    private static HttpResponseMessage Response(string name, bool more) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new RuntimeRunPageResponse([new()
        {
            Id = name,
            Name = name,
            Scope = RuntimeRunScope.Instance(Guid.Empty),
            Properties = new() { Agent = new("agent", 2), Input = new() { Messages = [] }, Execution = new() },
            Status = new() { State = RuntimeRunState.Succeeded, CreatedAt = DateTimeOffset.UnixEpoch, StartedAt = DateTimeOffset.UnixEpoch, CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(3), Response = "Deterministic output" }
        }], more ? "https://untrusted.invalid/never-follow" : null))
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
    private sealed class Handler(Func<int, Uri, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Assert.AreEqual("agentstration.test", request.RequestUri!.Host);
            return load(Requests.Count, request.RequestUri);
        }
    }
}
