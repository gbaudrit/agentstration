using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Api.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ModelProfileListTests
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task FiltersUseTheFullSnapshotWithoutProviderCatalogOrPerRowResolution(string locale)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler(_ => Task.FromResult(Summaries(45)));
        using var context = Context(handler);
        var cut = context.Render<Agentstration.Web.Components.Pages.ModelProfiles>();
        Assert.HasCount(20, cut.FindAll("tbody tr"));
        Assert.IsEmpty(cut.FindAll(".collection-actions"));
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("tbody tr"));
        await cut.Find("[data-filter-key=status] select").ChangeAsync(new() { Value = "Ready" });
        Assert.HasCount(20, cut.FindAll("tbody tr"));
        await cut.Find("[data-filter-key=provider] select").ChangeAsync(new() { Value = "first-provider" });
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        await cut.Find(".resource-toolbar button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=model] input").InputAsync(new() { Value = "model" });
        await cut.Find("[data-filter-key=usage] input").InputAsync(new() { Value = "0" });
        await cut.Find("[data-filter-key=namespace] select").ChangeAsync(new() { Value = "profiles" });
        await cut.Find("[data-filter-key=scope] select").ChangeAsync(new() { Value = "/instance" });
        Assert.HasCount(20, cut.FindAll(".collection-card"));
        await cut.Find("input[type=search]").InputAsync(new() { Value = "missing" });
        Assert.IsEmpty(cut.FindAll(".collection-card, .collection-pager"));
        await cut.Find(".empty-state button").ClickAsync(new());
        Assert.HasCount(20, cut.FindAll(".collection-card"));
        Assert.HasCount(1, handler.Requests);
        Assert.AreEqual("/api/modelprofiles", handler.Requests[0].Uri.PathAndQuery);
    }

    [TestMethod]
    public async Task FailedRefreshRetainsRowsAndSuccessfulShrinkClampsThePage()
    {
        var call = 0;
        using var handler = new Handler(_ => ++call == 2 ? throw new HttpRequestException("PRIVATE") : Task.FromResult(Summaries(call == 1 ? 45 : 3)));
        using var context = Context(handler);
        var cut = context.Render<Agentstration.Web.Components.Pages.ModelProfiles>();
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        await cut.Find("[data-testid=resource-model-profiles-refresh]").ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("tbody tr"));
        Assert.DoesNotContain("PRIVATE", cut.Markup, StringComparison.Ordinal);
        await cut.Find("[role=alert] button").ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("tbody tr"));
        Assert.IsEmpty(cut.FindAll(".collection-page-number"));
    }

    [TestMethod]
    public async Task LateReadCannotReplaceTheNewScopeAndForbiddenRefreshClearsRows()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var replacement = new TaskCompletionSource<HttpResponseMessage>();
        var call = 0;
        var provider = new ResourceListContextProvider();
        using var handler = new Handler(_ => ++call switch { 1 => Task.FromResult(Summaries(1)), 2 => old.Task, 3 => replacement.Task, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) });
        using var context = Context(handler, provider);
        var cut = context.Render<Agentstration.Web.Components.Pages.ModelProfiles>();
        var refresh = cut.Find("[data-testid=resource-model-profiles-refresh]").ClickAsync(new());
        provider.Scope = ResourceScopeRef.Workspace(Guid.NewGuid()).Value;
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".resource-name-link")));
        replacement.SetResult(Summaries(2));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll(".resource-name-link")));
        old.SetResult(Summaries(4));
        await refresh;
        Assert.HasCount(2, cut.FindAll(".resource-name-link"));
        await cut.Find("[data-testid=resource-model-profiles-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll(".resource-name-link, a[href*='/modelprofiles/new'], .collection-actions"));
    }

    [TestMethod]
    public void ReadCreateAndDeletePermissionsRemainSeparateAndManagedOrInheritedRowsCannotDelete()
    {
        using var handler = new Handler(_ => Task.FromResult(Summaries(1)));
        using (var denied = Context(handler, new() { Permissions = new HashSet<string>() }))
        {
            var cut = denied.Render<Agentstration.Web.Components.Pages.ModelProfiles>();
            Assert.IsEmpty(cut.FindAll(".resource-name-link, a[href*='/modelprofiles/new'], .collection-actions"));
            Assert.IsEmpty(handler.Requests);
        }
        using (var reader = Context(handler, new() { Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead } }))
        {
            var cut = reader.Render<Agentstration.Web.Components.Pages.ModelProfiles>();
            Assert.HasCount(1, cut.FindAll(".resource-name-link"));
            Assert.IsEmpty(cut.FindAll("a[href*='/modelprofiles/new'], .collection-actions"));
        }
        using var protectedHandler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ValueResponse<ModelProfileSummaryResponse>([Summary(0) with { IsManaged = true }, Summary(1) with { ScopeRef = ResourceScopeRef.Tenant(Guid.NewGuid()) }]))
        }));
        using var protectedContext = Context(protectedHandler, Administrator());
        var list = protectedContext.Render<Agentstration.Web.Components.Pages.ModelProfiles>();
        Assert.HasCount(2, list.FindAll(".resource-name-link"));
        Assert.IsEmpty(list.FindAll(".collection-actions"));
    }

    [TestMethod]
    public async Task DeletionRequiresConfirmationAndUsesTheExactScopeNamespaceAndLatestETag()
    {
        var deleted = false;
        using var handler = new Handler(request => Task.FromResult(request.Method == HttpMethod.Delete ? Deleted() : request.RequestUri!.AbsolutePath.EndsWith("/usages", StringComparison.Ordinal) ? Usages(false) : request.RequestUri.AbsolutePath.EndsWith("profile-0", StringComparison.Ordinal) ? Snapshot() : Summaries(deleted ? 0 : 1)));
        HttpResponseMessage Deleted() { deleted = true; return new(HttpStatusCode.NoContent); }
        using var context = Context(handler, Administrator());
        var cut = context.Render<Agentstration.Web.Components.Pages.ModelProfiles>();
        await cut.Find(".collection-actions-panel button").ClickAsync(new());
        Assert.IsNotNull(cut.Find("[role=alertdialog]"));
        Assert.IsFalse(deleted);
        await cut.Find("[role=alertdialog] .button-secondary").ClickAsync(new());
        Assert.IsFalse(deleted);
        await cut.Find(".collection-actions-panel button").ClickAsync(new());
        await cut.Find("[role=alertdialog] .button-danger").ClickAsync(new());
        Assert.IsTrue(deleted);
        Assert.IsEmpty(cut.FindAll(".resource-name-link, [role=alertdialog]"));
        var deletion = handler.Requests.Single(request => request.Method == HttpMethod.Delete);
        StringAssert.Contains(deletion.Uri.Query, "resourceNamespace=profiles");
        StringAssert.Contains(deletion.Uri.Query, "scopeRef=%2Finstance");
        Assert.AreEqual("\"latest\"", deletion.ETag);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UsageBeforeConfirmationOrConflictAtDeleteRetainsScopedConsumerLinks(bool conflict)
    {
        var usageReads = 0;
        using var handler = new Handler(request => Task.FromResult(request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { title = "PRIVATE", detail = "PRIVATE" }) } : request.RequestUri!.AbsolutePath.EndsWith("/usages", StringComparison.Ordinal) ? Usages(!conflict || ++usageReads > 1) : request.RequestUri.AbsolutePath.EndsWith("profile-0", StringComparison.Ordinal) ? Snapshot() : Summaries(1)));
        using var context = Context(handler, Administrator());
        var cut = context.Render<Agentstration.Web.Components.Pages.ModelProfiles>();
        await cut.Find(".collection-actions-panel button").ClickAsync(new());
        if (conflict) await cut.Find("[role=alertdialog] .button-danger").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[role=alertdialog]"));
        Assert.DoesNotContain("PRIVATE", cut.Markup, StringComparison.Ordinal);
        var consumer = cut.Find(".collection-page-feedback a.resource-name-link").GetAttribute("href")!;
        StringAssert.Contains(consumer, "/namespaces/agents/agents/consumer");
        StringAssert.Contains(consumer, "scopeRef=%2Finstance");
        Assert.HasCount(1, cut.FindAll("tbody tr"));
        Assert.AreEqual(conflict ? 1 : 0, handler.Requests.Count(request => request.Method == HttpMethod.Delete));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChangedContextRejectsLateUsageOrSnapshotAndCannotIssueDelete(bool snapshotPending)
    {
        var late = new TaskCompletionSource<HttpResponseMessage>();
        var provider = Administrator();
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/usages", StringComparison.Ordinal) ? snapshotPending ? Task.FromResult(Usages(false)) : late.Task : request.RequestUri.AbsolutePath.EndsWith("profile-0", StringComparison.Ordinal) ? late.Task : Task.FromResult(Summaries(1)));
        using var context = Context(handler, provider);
        var cut = context.Render<Agentstration.Web.Components.Pages.ModelProfiles>();
        Task action;
        if (snapshotPending)
        {
            await cut.Find(".collection-actions-panel button").ClickAsync(new());
            action = cut.Find("[role=alertdialog] .button-danger").ClickAsync(new());
        }
        else action = cut.Find(".collection-actions-panel button").ClickAsync(new());
        provider.Scope = ResourceScopeRef.Workspace(Guid.NewGuid()).Value;
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        late.SetResult(snapshotPending ? Snapshot() : Usages(false));
        await action;
        Assert.IsEmpty(cut.FindAll("[role=alertdialog], .collection-actions-panel button"));
        Assert.IsFalse(handler.Requests.Any(request => request.Method == HttpMethod.Delete));
    }

    private static ModelProfileSummaryResponse Summary(int index) => new($"profile-{index}", $"profile-{index}",
        new($"Profile {index}", null, new("provider", index % 2 == 0 ? "first-provider" : "second-provider", "Provider"), new("model"), new(), new(), new(), index % 2 == 0 ? "ready" : "unknown", index % 2 == 0 ? 0 : null), "profiles", ResourceScopeRef.Instance);
    private static HttpResponseMessage Summaries(int count) => new(HttpStatusCode.OK) { Content = JsonContent.Create(new ValueResponse<ModelProfileSummaryResponse>(Enumerable.Range(0, count).Select(Summary).ToArray())) };
    private static HttpResponseMessage Usages(bool used) => new(HttpStatusCode.OK) { Content = JsonContent.Create(new ModelProfileUsagesResponse(used ? [new("Agent", "consumer", "consumer", "Consumer", "agents", ResourceScopeRef.Instance, "Instance")] : [], used ? 1 : 0)) };
    private static HttpResponseMessage Snapshot()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ModelProfileResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ModelResourceKinds.ModelProfile,
                Metadata = new() { Name = "profile-0", Namespace = new("profiles") },
                ScopeRef = ResourceScopeRef.Instance,
                Definition = new() { DisplayName = "Profile", Provider = new("provider"), Model = new() { Name = "model" } }
            })
        };
        response.Headers.ETag = new EntityTagHeaderValue("\"latest\"");
        return response;
    }
    private static ResourceListContextProvider Administrator() => new()
    {
        Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite, AuthorizationPermissions.ResourcesDelete }
    };
    private static BunitContext Context(Handler handler, ResourceListContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(provider);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        context.Services.AddSingleton<IModelProfilesClient>(new ModelProfilesApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? ETag)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.IfMatch.ToString()));
            return load(request);
        }
    }
}
