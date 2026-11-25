using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ResourcePlanCollectionTests
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task BoundedPagesExposeNoLocalFiltersOrInventedTotalAndRetainCards(string culture)
    {
        using var locale = new TestCultureScope(culture);
        using var handler = new Handler((_, uri) => Task.FromResult(Response(uri.Query.Contains("skip=20", StringComparison.Ordinal) ? "second" : "first", true)));
        using var context = Context(handler);
        var cut = context.Render<ResourcePlans>();
        Assert.AreEqual(1, handler.Requests.Count);
        StringAssert.Contains(cut.Markup, "Coordinate support");
        Assert.AreEqual(DateTimeOffset.UnixEpoch.ToString("O"), cut.Find("time").GetAttribute("datetime"));
        StringAssert.Contains(handler.Requests[0].Query, "skip=0&take=20");
        Assert.IsEmpty(cut.FindAll("input[type=search], [data-filter-key=changes], .collection-page-number, .collection-pager [role=status]"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.AreEqual(1, handler.Requests.Count);
        await cut.Find(".collection-pager button:last-child").ClickAsync(new());
        Assert.AreEqual(2, handler.Requests.Count);
        StringAssert.Contains(handler.Requests[1].Query, "skip=20&take=20");
        Assert.HasCount(1, cut.FindAll(".collection-card"));
        StringAssert.Contains(cut.Find(".resource-name-link").TextContent, "second");
        await cut.InvokeAsync(() => cut.Find(".collection-page-size select").ChangeAsync(new() { Value = "50" }));
        StringAssert.Contains(handler.Requests[2].Query, "skip=0&take=50");
        await cut.InvokeAsync(() => cut.Find("[data-filter-key=status] select").ChangeAsync(new() { Value = "Ready" }));
        StringAssert.Contains(handler.Requests[3].Query, "skip=0&take=50&status=Ready");
        Assert.IsEmpty(cut.FindAll("input[type=search], .collection-page-number, .collection-pager [role=status]"));
    }

    [TestMethod]
    public async Task FailedPageKeepsPreviousSuccessfulFactsAndRetriesTheActualRequest()
    {
        using var handler = new Handler((call, _) => call == 2 ? throw new HttpRequestException("Offline") : Task.FromResult(Response(call == 1 ? "first" : "second", true)));
        using var context = Context(handler);
        var cut = context.Render<ResourcePlans>();
        await cut.Find(".collection-pager button:last-child").ClickAsync(new());
        Assert.IsTrue(cut.Find(".collection-pager button:first-of-type").HasAttribute("disabled"));
        StringAssert.Contains(cut.Find(".resource-name-link").TextContent, "first");
        await cut.Find("[role=alert] button").ClickAsync(new());
        StringAssert.Contains(handler.Requests[2].Query, "skip=20&take=20");
        StringAssert.Contains(cut.Find(".resource-name-link").TextContent, "second");
        Assert.IsFalse(cut.Find(".collection-pager button:first-of-type").HasAttribute("disabled"));
    }

    [TestMethod]
    public async Task EmptyTerminalWindowRetainsPreviousControlWithoutInventingAnEmptyCatalog()
    {
        using var handler = new Handler((call, _) => Task.FromResult(call == 2 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ResourcePlanPage([], false)) } : Response("first", true)));
        using var context = Context(handler);
        var cut = context.Render<ResourcePlans>();
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        await cut.Find(".collection-pager button:last-child").ClickAsync(new());
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.IsFalse(cut.Find(".collection-pager button:first-of-type").HasAttribute("disabled"));
        Assert.IsTrue(cut.Find(".collection-pager button:last-child").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll(".resource-name-link"));
        await cut.Find(".collection-pager button:first-of-type").ClickAsync(new());
        Assert.HasCount(1, cut.FindAll(".collection-card"));
        StringAssert.Contains(handler.Requests[2].Query, "skip=0&take=20");
    }

    [TestMethod]
    public async Task ContextReplacementRejectsLatePageAndForbiddenRefreshRemovesRowsAndCommands()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var replacement = new TaskCompletionSource<HttpResponseMessage>();
        var provider = new ResourceListContextProvider { Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead } };
        using var handler = new Handler((call, _) => call switch { 1 => Task.FromResult(Response("first", true)), 2 => old.Task, 3 => replacement.Task, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) });
        using var context = Context(handler, provider);
        var cut = context.Render<ResourcePlans>();
        var next = cut.Find(".collection-pager button:last-child").ClickAsync(new());
        provider.Scope = ResourceScopeRef.Workspace(Guid.NewGuid()).Value;
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".resource-name-link")));
        replacement.SetResult(Response("replacement", false));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".resource-name-link").TextContent, "replacement"));
        StringAssert.Contains(handler.Requests[2].Query, "skip=0&take=20");
        old.SetResult(Response("old", true));
        await next;
        StringAssert.Contains(cut.Find(".resource-name-link").TextContent, "replacement");
        await cut.Find("[data-testid=resource-plans-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll(".resource-name-link, .collection-pager, .collection-actions"));
    }

    [TestMethod]
    public void MissingResourceReadMakesNoRequestAndAuthorizedReadsHaveNoRowMutations()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response("readable", false)));
        var provider = new ResourceListContextProvider { Permissions = new HashSet<string>() };
        using (var context = Context(handler, provider))
        {
            var cut = context.Render<ResourcePlans>();
            Assert.IsEmpty(cut.FindAll(".resource-name-link, .collection-actions"));
            Assert.AreEqual(0, handler.Requests.Count);
        }
        provider.Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead };
        using var reader = Context(handler, provider);
        var list = reader.Render<ResourcePlans>();
        Assert.HasCount(1, list.FindAll(".resource-name-link"));
        Assert.HasCount(1, list.FindAll(".collection-actions"));
        Assert.HasCount(1, list.FindAll("[data-testid=resource-plan-preview]"));
    }

    [TestMethod]
    public async Task LateReviewUnderAChangedOperationalWorkspaceCannotClearNewRows()
    {
        using var culture = new TestCultureScope("en-US");
        var oldReview = new TaskCompletionSource<HttpResponseMessage>();
        var provider = new WorkspaceProvider();
        using var handler = new Handler((call, _) => Task.FromResult(Response(call == 1 ? "first" : "replacement", false)),
            call => call == 1 ? oldReview.Task : call == 2 ? Task.FromResult(EmptyReview()) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var context = Context(handler, provider);
        var cut = context.Render<ResourcePlans>();
        // Review discovery runs independently of rendering; await the HTTP boundary itself.
        await handler.FirstReviewRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, handler.ReviewRequests);
        Assert.IsFalse(cut.Markup.Contains("Proposal not checked yet", StringComparison.Ordinal));
        provider.WorkspaceId = Guid.NewGuid();
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".resource-name-link").TextContent, "replacement"));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Proposal not checked yet"));
        oldReview.SetResult(new(HttpStatusCode.Forbidden));
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll(".resource-name-link")));
        await cut.Find("[data-testid=resource-plans-refresh]").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".resource-name-link")));
    }

    [TestMethod]
    public async Task FailedReviewRetainsPrimaryRowsAndRetriesThePageWithoutInventingReviewFacts()
    {
        using var culture = new TestCultureScope("en-US");
        using var handler = new Handler((_, _) => Task.FromResult(Response("first", false)),
            call => call == 1 ? throw new HttpRequestException("Private review transport message") : Task.FromResult(EmptyReview()));
        using var context = Context(handler);
        var cut = context.Render<ResourcePlans>();
        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find("[role=alert]")));
        Assert.HasCount(1, cut.FindAll(".resource-name-link"));
        Assert.IsFalse(cut.Markup.Contains("Private review transport message", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("Proposal not checked yet", StringComparison.Ordinal));
        await cut.Find("[role=alert] button").ClickAsync(new());
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Proposal not checked yet"));
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(2, handler.ReviewRequests);
    }

    [TestMethod]
    public void KnownReviewRetainsActualCountReadinessAndStaleValidationMarker()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response("first", false)), _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ResourceChangeSetPage([new(KnownChangeSet(), "\"set\"")], false))
        }));
        using var context = Context(handler);
        var cut = context.Render<ResourcePlans>();
        var strings = context.Services.GetRequiredService<IStringLocalizer<ResourcePlansStrings>>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("0", cut.Find("tbody tr td:nth-child(2) strong").TextContent);
            StringAssert.Contains(cut.Find("tbody tr td:nth-child(3)").TextContent, strings["Readiness.Ready"].Value);
            StringAssert.Contains(cut.Find("tbody tr td:nth-child(2)").TextContent, strings["Stale"].Value);
        });
        Assert.AreEqual(1, handler.ValidationRequests);
        Assert.AreEqual(1, handler.ReviewRequests);
    }

    private static ResourceChangeSet KnownChangeSet() => new(new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001")),
        new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")), 2,
        new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"))),
        "contract", "materializer", "materialization", "new-digest", ResourceChangeSetStatus.Validated, [], [], Guid.Empty, DateTimeOffset.UnixEpoch);

    private sealed class WorkspaceProvider : IConsoleContextProvider
    {
        public Guid WorkspaceId { get; set; } = Guid.NewGuid();
        public async Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) =>
            await new ResourceListContextProvider().GetAsync(cancellationToken) with { WorkspaceId = WorkspaceId };
    }

    private static HttpResponseMessage EmptyReview() => new(HttpStatusCode.OK) { Content = JsonContent.Create(new ResourceChangeSetPage([], false)) };
    private static HttpResponseMessage Response(string name, bool more) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ResourcePlanPage([new(new ResourcePlan(new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")),
            new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"))),
            name, "Coordinate support", null, new(ResourcePlanningContractVersions.V1, JsonSerializer.SerializeToElement(new { })),
            ResourcePlanStatus.Ready, 2, new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004")), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), "\"plan\"")], more))
    };
    private static BunitContext Context(Handler handler, IConsoleContextProvider? provider = null)
    {
        provider ??= new ResourceListContextProvider { Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead } };
        var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddSingleton(provider);
        context.Services.AddSingleton<ConsoleContextState>();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourcePlansApiClient>(new ResourcePlansApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Handler(Func<int, Uri, Task<HttpResponseMessage>> load, Func<int, Task<HttpResponseMessage>>? review = null) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public int ReviewRequests { get; private set; }
        public TaskCompletionSource FirstReviewRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ValidationRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/validations", StringComparison.Ordinal))
            {
                ValidationRequests++;
                var set = KnownChangeSet();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[] { new ResourceChangeSetValidation(Guid.NewGuid(), set.Id, "old-digest", set.PlanId, set.PlanRevision, set.Scope, ResourceChangeSetReadiness.Ready, [], Guid.Empty, DateTimeOffset.UnixEpoch) })
                });
            }
            if (uri.AbsolutePath.EndsWith("/change-sets", StringComparison.Ordinal))
            {
                ReviewRequests++;
                FirstReviewRequested.TrySetResult();
                return review?.Invoke(ReviewRequests) ?? Task.FromResult(EmptyReview());
            }
            Requests.Add(uri);
            return load(Requests.Count, uri);
        }
    }
}
