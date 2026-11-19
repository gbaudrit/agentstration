using System.Net;
using System.Net.Http.Json;
using Agentstration.Api.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class StorageTypeCollectionTests
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task BoundedPagesExposeNoLocalFiltersOrInventedTotalAndRetainCards(string culture)
    {
        using var locale = new TestCultureScope(culture);
        using var handler = new Handler((_, uri) => Task.FromResult(Response(uri.Query.Contains("skip=20", StringComparison.Ordinal) ? "second" : "first", true)));
        using var context = Context(handler);
        var cut = context.Render<StorageTypes>();
        Assert.AreEqual(1, handler.Requests.Count);
        StringAssert.Contains(handler.Requests[0].Query, "scopeRef=%2Finstance&skip=0&take=20");
        Assert.IsEmpty(cut.FindAll("input[type=search], [data-filter-key], .collection-page-number, .collection-pager [role=status]"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.AreEqual(1, handler.Requests.Count);
        await cut.Find(".collection-pager button:last-child").ClickAsync(new());
        Assert.AreEqual(2, handler.Requests.Count);
        StringAssert.Contains(handler.Requests[1].Query, "skip=20&take=20");
        Assert.HasCount(1, cut.FindAll(".collection-card"));
        StringAssert.Contains(cut.Find(".resource-name-link").GetAttribute("href")!, "/storage-types/second?scopeRef=%2Finstance&namespace=catalog");
        await cut.Find(".collection-page-size select").ChangeAsync(new() { Value = "50" });
        StringAssert.Contains(handler.Requests[2].Query, "skip=0&take=50");
    }

    [TestMethod]
    public async Task FailedPageKeepsPreviousSuccessfulFactsAndRetriesTheActualRequest()
    {
        using var handler = new Handler((call, _) => call == 2 ? throw new HttpRequestException("Offline") : Task.FromResult(Response(call == 1 ? "first" : "second", true)));
        using var context = Context(handler);
        var cut = context.Render<StorageTypes>();
        await cut.Find(".collection-pager button:last-child").ClickAsync(new());
        Assert.IsTrue(cut.Find(".collection-pager button:first-of-type").HasAttribute("disabled"));
        StringAssert.Contains(cut.Find(".resource-name-link").TextContent, "first");
        await cut.Find("[role=alert] button").ClickAsync(new());
        StringAssert.Contains(handler.Requests[2].Query, "skip=20&take=20");
        StringAssert.Contains(cut.Find(".resource-name-link").TextContent, "second");
        Assert.IsFalse(cut.Find(".collection-pager button:first-of-type").HasAttribute("disabled"));
    }

    [TestMethod]
    public async Task RemovedTerminalPageReturnsToFirstWithOnlyOneAdditionalRequest()
    {
        using var handler = new Handler((call, _) => Task.FromResult(call == 3 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PagedResponse<StorageTypeResource>([], null)) } : Response(call == 1 ? "first" : "last", call == 1)));
        using var context = Context(handler);
        var cut = context.Render<StorageTypes>();
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        await cut.Find(".collection-pager button:last-child").ClickAsync(new());
        await cut.Find("[data-testid=storage-types-page-refresh]").ClickAsync(new());
        Assert.AreEqual(4, handler.Requests.Count);
        StringAssert.Contains(handler.Requests[3].Query, "skip=0&take=20");
        Assert.HasCount(1, cut.FindAll(".collection-card"));
        Assert.IsTrue(cut.Find(".collection-pager button:first-of-type").HasAttribute("disabled"));
    }

    [TestMethod]
    public async Task ContextReplacementRejectsLatePageAndForbiddenRefreshRemovesRowsAndCommands()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var replacement = new TaskCompletionSource<HttpResponseMessage>();
        var provider = new ResourceListContextProvider();
        using var handler = new Handler((call, _) => call switch { 1 => Task.FromResult(Response("first", true)), 2 => old.Task, 3 => replacement.Task, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) });
        using var context = Context(handler, provider);
        var cut = context.Render<StorageTypes>();
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
        await cut.Find("[data-testid=storage-types-page-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll(".resource-name-link, .collection-pager, [data-testid=storage-type-create]"));
    }

    [TestMethod]
    public void DeniedReadMakesNoRequestAndReadOnlyHasNoCreateCommand()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response("readable", false)));
        var provider = new ResourceListContextProvider { Permissions = new HashSet<string>() };
        using (var context = Context(handler, provider))
        {
            var cut = context.Render<StorageTypes>();
            Assert.IsEmpty(cut.FindAll(".resource-name-link, [data-testid=storage-type-create]"));
            Assert.AreEqual(0, handler.Requests.Count);
        }
        provider.Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead };
        using var reader = Context(handler, provider);
        var list = reader.Render<StorageTypes>();
        Assert.HasCount(1, list.FindAll(".resource-name-link"));
        Assert.IsEmpty(list.FindAll("[data-testid=storage-type-create]"));
    }

    private static HttpResponseMessage Response(string name, bool more) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new PagedResponse<StorageTypeResource>([new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.StorageType,
            Metadata = new() { Name = name, Namespace = new("catalog") },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new() { DisplayName = name, PreferredVersion = "1.0.0", Versions = [new StorageTypeVersionDraft().Build()] },
            ObservedVersions = [new("1.0.0", StorageAvailability.Ready)]
        }], more ? "https://untrusted.invalid/never-follow" : null))
    };
    private static BunitContext Context(Handler handler, ResourceListContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(provider);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IStorageTypesClient>(new StoragesApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
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
