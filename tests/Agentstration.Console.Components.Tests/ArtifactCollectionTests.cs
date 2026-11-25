using System.Net;
using System.Net.Http.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ArtifactCollectionTests
{
    [TestMethod]
    [DataRow("en-US", "No matching results")]
    [DataRow("fr-FR", "Aucun résultat correspondant")]
    public async Task CompleteMetadataCollectionsKeepSeparateQueriesAndUsefulFilters(string culture, string noMatches)
    {
        using var locale = new TestCultureScope(culture);
        using var handler = new Handler((_, staged) => Task.FromResult(Response(45, staged)));
        using var context = Context(handler);
        var cut = context.Render<Agentstration.Web.Components.Pages.Artifacts>();
        cut.WaitForAssertion(() => Assert.HasCount(20, cut.FindAll("[data-testid=durable-artifact-row]")));
        Assert.HasCount(20, cut.FindAll(".collection-actions"));
        Assert.HasCount(20, cut.FindAll("[data-testid=durable-artifact-preview]"));
        Assert.AreEqual(2, handler.Requests, "Row actions do not fetch resource details or content.");
        StringAssert.Contains(cut.Find("[data-testid=durable-artifact-row] .collection-identity-cell a").GetAttribute("href")!, "context=%2Finstance");
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=durable-artifact-row]"));
        await cut.Find("[data-filter-key=media] select").ChangeAsync(new() { Value = "application/json" });
        await cut.Find(".resource-toolbar button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=producer] input").InputAsync(new() { Value = "fetch" });
        await cut.Find("[data-filter-key=run] input").InputAsync(new() { Value = "flow-run-1" });
        await cut.Find("[data-filter-key=size] input").InputAsync(new() { Value = "1024" });
        await cut.FindAll(".collection-page-number")[1].ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("[data-testid=durable-artifact-row]"));
        await cut.FindAll(".resource-tabs button")[1].ClickAsync(new());
        Assert.HasCount(20, cut.FindAll("[data-testid=staged-artifact-row]"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        await cut.Find("[data-filter-key=status] select").ChangeAsync(new() { Value = "Sealed" });
        await cut.Find("[data-filter-key=kind] select").ChangeAsync(new() { Value = "FlowRun" });
        await cut.Find(".resource-toolbar button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=producer] input").InputAsync(new() { Value = "flow-run-1" });
        await cut.Find("[data-filter-key=size] input").InputAsync(new() { Value = "1024" });
        Assert.HasCount(20, cut.FindAll(".collection-card"));
        await cut.Find("input[type=search]").InputAsync(new() { Value = "absent" });
        Assert.AreEqual(noMatches, cut.Find(".empty-state h2").TextContent);
        await cut.Find(".empty-state button").ClickAsync(new());
        Assert.HasCount(20, cut.FindAll(".collection-card"));
        await cut.FindAll(".resource-tabs button")[0].ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("[data-testid=durable-artifact-row]"));
        Assert.AreEqual(2, handler.Requests, "Tab switches, filters and local pages must not read content or refetch metadata.");
    }

    [TestMethod]
    public async Task FailedRefreshRetainsBothSnapshotsAndSuccessfulShrinkClampsBothPages()
    {
        using var handler = new Handler((call, staged) => call is 3 or 4 ? throw new HttpRequestException("Offline") : Task.FromResult(Response(call <= 2 ? 45 : 3, staged)));
        using var context = Context(handler);
        var cut = context.Render<Agentstration.Web.Components.Pages.Artifacts>();
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        await cut.FindAll(".resource-tabs button")[1].ClickAsync(new());
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        await cut.Find("[data-testid=resource-artifacts-refresh]").ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=staged-artifact-row]"));
        await cut.FindAll(".resource-tabs button")[0].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=durable-artifact-row]"));
        await cut.Find("[role=alert] button").ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("[data-testid=durable-artifact-row]"));
        await cut.FindAll(".resource-tabs button")[1].ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("[data-testid=staged-artifact-row]"));
        Assert.AreEqual(6, handler.Requests);
    }

    [TestMethod]
    public async Task ReplacedOperationalWorkspaceRejectsLateMetadataAndInspectRevocationClearsBothCollections()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new WorkspaceProvider();
        using var handler = new Handler(async (call, staged) =>
        {
            if (call is 3 or 4)
            {
                await pending.Task;
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            return Response(call <= 2 ? 1 : 2, staged);
        });
        using var context = Context(handler, provider);
        var cut = context.Render<Agentstration.Web.Components.Pages.Artifacts>();
        var refresh = cut.Find("[data-testid=resource-artifacts-refresh]").ClickAsync(new());
        provider.WorkspaceId = Guid.NewGuid();
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll("[data-testid=durable-artifact-row]")));
        pending.SetResult(true);
        await refresh;
        Assert.HasCount(2, cut.FindAll("[data-testid=durable-artifact-row]"));
        provider.Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead };
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("[data-testid=durable-artifact-row], [data-testid=staged-artifact-row]")));
        await cut.FindAll(".resource-tabs button")[1].ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[data-testid=staged-artifact-row]"));
        Assert.AreEqual(6, handler.Requests);
    }

    [TestMethod]
    public void ResourceReadWithoutArtifactInspectMakesNoMetadataRequest()
    {
        var provider = new WorkspaceProvider { Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead } };
        using var handler = new Handler((_, staged) => Task.FromResult(Response(1, staged)));
        using var context = Context(handler, provider);
        var cut = context.Render<Agentstration.Web.Components.Pages.Artifacts>();
        Assert.IsEmpty(cut.FindAll("[data-testid=durable-artifact-row], [data-testid=staged-artifact-row]"));
        Assert.AreEqual(0, handler.Requests);
    }

    private sealed class WorkspaceProvider : IConsoleContextProvider
    {
        public Guid WorkspaceId { get; set; } = Guid.NewGuid();
        public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string> { AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ArtifactsInspect };
        public async Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) =>
            await new ResourceListContextProvider().GetAsync(cancellationToken) with { WorkspaceId = WorkspaceId, Permissions = Permissions };
    }
    private static HttpResponseMessage Response(int count, bool staged)
    {
        var fixture = new ArtifactAdministrationComponentTests.ArtifactClient("fixture");
        if (staged) return new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(Enumerable.Range(0, count).Select(index => fixture.Artifact with
            {
                ArtifactId = StagedArtifactId.Parse((index + 1).ToString("x32", System.Globalization.CultureInfo.InvariantCulture)),
                FileName = $"local-{index}.txt",
                Length = index % 2 == 0 ? 1024 : 2048,
                Status = index % 2 == 0 ? StagedArtifactStatus.Sealed : StagedArtifactStatus.Open,
                Producer = new() { Kind = index % 2 == 0 ? ArtifactProducerKind.FlowRun : ArtifactProducerKind.RuntimeRun, Id = "flow-run-1", FlowRunId = "flow-run-1" },
                CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(-index)
            }).ToArray())
        };
        return new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(Enumerable.Range(0, count).Select(index => fixture.Durable with
            {
                ArtifactId = FlowRunArtifactId.Parse((index + 1).ToString("x32", System.Globalization.CultureInfo.InvariantCulture)),
                CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(-index),
                Receipt = fixture.Durable.Receipt with { MediaType = index % 2 == 0 ? "application/json" : "text/plain", Length = index % 2 == 0 ? 1024 : 2048, Provenance = new Dictionary<string, string> { ["fileName"] = $"durable-{index}.txt" } }
            }).ToArray())
        };
    }
    private sealed class NavigationContext : IConsoleNavigationContext
    {
        public string ScopeRef => "/instance";
        public string AddToUri(string uri) => Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(uri, "context", ScopeRef);
    }

    private static BunitContext Context(Handler handler, IConsoleContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddSingleton<IConsoleContextProvider>(provider ?? new WorkspaceProvider());
        context.Services.AddSingleton<ConsoleContextState>();
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext());
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IArtifactsClient>(new ArtifactsApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Handler(Func<int, bool, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.IsTrue(request.RequestUri!.AbsolutePath is "/api/artifacts/staged" or "/api/artifacts/flow-run-artifacts", "The list must never read content or run a mutation.");
            return load(++Requests, request.RequestUri.AbsolutePath.EndsWith("/staged", StringComparison.Ordinal));
        }
    }
}
