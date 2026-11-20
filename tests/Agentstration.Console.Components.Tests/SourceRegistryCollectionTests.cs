using System.Net;
using System.Net.Http.Json;
using Agentstration.Api.Contracts;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class SourceRegistryCollectionTests
{
    [TestMethod]
    [DataRow("en-US", "1,000 configured registries")]
    [DataRow("fr-FR", "1 000 registres configurés")]
    public void BoundedSnapshotHasNoInventedGlobalControlsOrMetrics(string culture, string help)
    {
        using var locale = new TestCultureScope(culture);
        using var handler = new Handler(_ => Task.FromResult(Response([Registry])));
        using var context = Context(handler);
        var cut = context.Render<SourceRegistryCollection>();
        Assert.HasCount(1, cut.FindAll("[data-testid=source-registry-row]"));
        StringAssert.Contains(cut.Markup, help);
        Assert.IsEmpty(cut.FindAll("input[type=search], [data-filter-key], .collection-pager, .metric-card"));
        Assert.HasCount(3, cut.FindAll(".resource-tabs a"));
        Assert.AreEqual(1, handler.ListReads);
    }

    [TestMethod]
    public async Task RefreshRetainsSnapshotOnFailureAndDenialClearsRowsAndCommands()
    {
        using var handler = new Handler(call => call switch { 1 => Task.FromResult(Response([Registry])), 2 => throw new HttpRequestException("Private transport detail"), _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) });
        using var context = Context(handler);
        var cut = context.Render<SourceRegistryCollection>();
        await cut.Find("[data-testid=distribution-source-registries-refresh]").ClickAsync(new());
        Assert.HasCount(1, cut.FindAll("[data-testid=source-registry-row]"));
        Assert.IsFalse(cut.Markup.Contains("Private transport detail", StringComparison.Ordinal));
        await cut.Find(".resource-load-feedback button, [role=alert] button").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[data-testid=source-registry-row], .page-header a"));
    }

    [TestMethod]
    public async Task ContextNotificationClearsCachedCatalogAndRejectsLateSnapshot()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var next = new TaskCompletionSource<HttpResponseMessage>();
        using var handler = new Handler(call => call switch { 1 => Task.FromResult(Response([Registry])), 2 => old.Task, _ => next.Task });
        using var context = Context(handler);
        var cut = context.Render<SourceRegistryCollection>();
        var refresh = cut.Find("[data-testid=distribution-source-registries-refresh]").ClickAsync(new());
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("[data-testid=source-registry-row]")));
        next.SetResult(Response([]));
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll(".empty-state")));
        old.SetResult(Response([Registry]));
        await refresh;
        Assert.IsEmpty(cut.FindAll("[data-testid=source-registry-row]"));
    }

    [TestMethod]
    public async Task ToggleReadsLatestDefinitionAndEtagBeforeWritingAndPreservesBusinessRefresh()
    {
        using var handler = new Handler(_ => Task.FromResult(Response([Registry])));
        using var context = Context(handler);
        var cut = context.Render<SourceRegistryCollection>();
        await cut.Find("button[role=switch]").ClickAsync(new());
        Assert.AreEqual(1, handler.Writes);
        await cut.Find(".collection-actions .text-button").ClickAsync(new());
        Assert.AreEqual(1, handler.Refreshes);
        Assert.AreEqual(3, handler.ListReads);
    }

    private static SourceRegistryRegistrationView Registry => new(
        new() { Uid = Guid.NewGuid(), ApiVersion = ResourceApiVersions.CoreV1, Kind = SourceRegistryKinds.SourceRegistryRegistration, Metadata = new() { Name = "fixture-registry" }, ScopeRef = ResourceScopeRef.Instance, Definition = new() { DisplayName = "Fixture registry", IndexUrl = new("https://fixture.invalid/index.json"), Enabled = true, TrustPolicy = SourceRegistryTrustPolicy.Trusted } },
        new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = SourceRegistryKinds.SourceRegistryObservedState, Metadata = new() { Name = "fixture-registry" }, ScopeRef = ResourceScopeRef.Instance, Definition = new() { RegistrationUid = Guid.NewGuid(), Status = SourceRegistryObservedStatus.NeverFetched } });
    private static HttpResponseMessage Response(IReadOnlyList<SourceRegistryRegistrationView> rows) => new(HttpStatusCode.OK) { Content = JsonContent.Create(new ValueResponse<SourceRegistryRegistrationView>(rows)) };
    private static BunitContext Context(Handler handler)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConsoleContextProvider>(new Provider());
        context.Services.AddSingleton<ConsoleContextState>();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<ISourceRegistriesClient>(new SourceRegistriesApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Provider : IConsoleContextProvider
    {
        public async Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => await new ResourceListContextProvider().GetAsync(cancellationToken) with { Permissions = new HashSet<string>() };
    }
    private sealed class Handler(Func<int, Task<HttpResponseMessage>> list) : HttpMessageHandler
    {
        public int ListReads { get; private set; }
        public int Writes { get; private set; }
        public int Refreshes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.PathAndQuery == "/api/sourceregistries") { Assert.AreEqual(HttpMethod.Get, request.Method); return await list(++ListReads); }
            if (request.RequestUri.AbsolutePath.EndsWith("/refresh", StringComparison.Ordinal)) { Assert.AreEqual(HttpMethod.Post, request.Method); Refreshes++; }
            else if (request.Method == HttpMethod.Put)
            {
                Assert.AreEqual("\"latest\"", request.Headers.IfMatch.Single().ToString());
                var body = await request.Content!.ReadFromJsonAsync<PutSourceRegistryRequest>(cancellationToken);
                // Latest server state is already disabled; use that state, not the stale displayed Enabled=true.
                Assert.IsTrue(body!.Properties.Enabled);
                Assert.AreEqual("Updated definition", body.Properties.DisplayName);
                Writes++;
            }
            else Assert.AreEqual(HttpMethod.Get, request.Method);
            var value = Registry with { Registration = Registry.Registration with { Definition = Registry.Registration.Definition with { Enabled = false, DisplayName = "Updated definition" } } };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
            response.Headers.ETag = new("\"latest\"");
            return response;
        }
    }
}
