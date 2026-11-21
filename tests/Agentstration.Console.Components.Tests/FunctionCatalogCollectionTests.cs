using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Api.Contracts;
using Agentstration.Functions.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FunctionCatalogCollectionTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(WorkspaceId);
    private static readonly ResourceNamespace Namespace = new("catalog-tests");
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}""");

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task IndependentOffsetPagesUseOneLookaheadAndOmitUnsupportedQueriesAndTotals(string culture)
    {
        using var locale = new TestCultureScope(culture);
        using var handler = new Handler((kind, skip, take, _) => Task.FromResult(Page(kind, skip, take, 25)));
        using var context = Context(handler);
        var cut = context.Render<TransformationFunctions>();
        Assert.HasCount(20, cut.FindAll("[data-testid=transformation-function-row]"));
        Assert.HasCount(20, cut.FindAll("[data-testid=function-kind-row]"));
        Assert.IsEmpty(cut.FindAll("input[type=search], [data-filter-key], .collection-page-number, .metric-card"));
        Assert.AreEqual(21, handler.LastTake);
        var headings = cut.FindAll("[data-testid=function-catalog] th");
        Assert.HasCount(5, headings);
        Assert.AreEqual(culture == "fr-FR" ? "Type de fonction" : "Function kind", headings[1].TextContent);
        Assert.IsTrue(cut.FindAll("[data-testid=function-catalog] .function-kind").All(value => value.TextContent == "native"));
        await cut.Find("[data-testid=function-catalog] .collection-pager button:last-child").ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=transformation-function-row]"));
        Assert.HasCount(20, cut.FindAll("[data-testid=function-kind-row]"));
        Assert.AreEqual(2, handler.FunctionReads);
        Assert.AreEqual(1, handler.KindReads);
        await cut.Find("[data-testid=function-kinds-catalog] .collection-pager button:last-child").ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=function-kind-row]"));
        await cut.FindAll("[data-testid=function-catalog] .collection-view-switch button")[1].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=function-catalog] .collection-card"));
        Assert.HasCount(5, cut.FindAll("[data-testid=function-catalog] .collection-card .function-kind"));
        Assert.HasCount(2, cut.FindAll("[data-testid=function-catalog] .collection-page-size select option"));
        StringAssert.Contains(cut.Find(".resource-name-link").GetAttribute("href"), "namespace=catalog-tests");
        StringAssert.Contains(cut.Find(".resource-name-link").GetAttribute("href"), "context=" + Uri.EscapeDataString(Scope.Value));
    }

    [TestMethod]
    public async Task FailedPageRetainsSuccessfulFactsAndShrunkTerminalWindowKeepsPrevious()
    {
        using var handler = new Handler((kind, skip, take, call) => !kind && call == 2 ? throw new HttpRequestException("Private transport detail") : Task.FromResult(Page(kind, skip, take, !kind && call > 2 ? 0 : 25)));
        using var context = Context(handler);
        var cut = context.Render<TransformationFunctions>();
        await cut.Find("[data-testid=function-catalog] .collection-pager button:last-child").ClickAsync(new());
        Assert.HasCount(20, cut.FindAll("[data-testid=transformation-function-row]"));
        Assert.IsFalse(cut.Markup.Contains("Private transport detail", StringComparison.Ordinal));
        await cut.Find("[data-testid=function-catalog] [role=alert] button").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[data-testid=transformation-function-row]"));
        Assert.IsFalse(cut.FindAll("[data-testid=function-catalog] .collection-pager button")[0].HasAttribute("disabled"));
        Assert.HasCount(20, cut.FindAll("[data-testid=function-kind-row]"));
    }

    [TestMethod]
    public async Task ContextNotificationClearsBothCataloguesAndRejectsOldResponsesAndDeniedReads()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var replacement = new TaskCompletionSource<HttpResponseMessage>();
        using var handler = new Handler((kind, skip, take, call) => kind ? Task.FromResult(Page(true, skip, take, call == 1 ? 1 : 2)) : call switch { 1 => Task.FromResult(Page(false, skip, take, 1)), 2 => old.Task, 3 => replacement.Task, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) });
        using var context = Context(handler);
        var cut = context.Render<TransformationFunctions>();
        await cut.Find("[data-testid=function-kind-row] .collection-primary-actions button[aria-expanded]").ClickAsync(new());
        var refresh = cut.Find("[data-testid=function-catalog-refresh]").ClickAsync(new());
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("[data-testid=transformation-function-row], .collection-expanded")));
        replacement.SetResult(Page(false, 0, 21, 2));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll("[data-testid=transformation-function-row]")));
        old.SetResult(Page(false, 0, 21, 4));
        await refresh;
        Assert.HasCount(2, cut.FindAll("[data-testid=transformation-function-row]"));
        await cut.Find("[data-testid=function-catalog-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[data-testid=transformation-function-row]"));
        Assert.HasCount(2, cut.FindAll("[data-testid=function-kind-row]"));
    }

    [TestMethod]
    public async Task KindToggleUsesLatestExactSnapshotAndEtagWithoutChangingImmutableSchemaVersions()
    {
        using var handler = new Handler((kind, skip, take, _) => Task.FromResult(Page(kind, skip, take, 1)));
        using var context = Context(handler);
        var cut = context.Render<TransformationFunctions>();
        var toggle = cut.FindAll("[data-testid=function-kind-row] .collection-primary-actions button")[1];
        await toggle.ClickAsync(new());
        Assert.AreEqual(1, handler.Writes);
        Assert.AreEqual(1, handler.KindDetailReads);
    }

    private static TransformationFunctionResource Function(int index) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = FunctionResourceKinds.TransformationFunction,
        ScopeRef = Scope,
        Metadata = new() { Name = $"function-{index}", Namespace = Namespace },
        Definition = new($"Function {index}", [new("1.0.0", new("native", "1.0.0", Scope, Namespace), JsonSerializer.Deserialize<JsonElement>("{}"))])
    };
    private static FunctionKindResource Kind(int index) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = FunctionResourceKinds.FunctionKind,
        ScopeRef = Scope,
        Metadata = new() { Name = $"kind-{index}", Namespace = Namespace },
        Definition = new($"Kind {index}", [new("1.0.0", "native", Schema)])
    };
    private static HttpResponseMessage Page(bool kind, int skip, int take, int count) => new(HttpStatusCode.OK)
    {
        Content = kind
        ? JsonContent.Create(new ValueResponse<FunctionKindResource>(Enumerable.Range(0, count).Skip(skip).Take(take).Select(Kind).ToArray()))
        : JsonContent.Create(new ValueResponse<TransformationFunctionResource>(Enumerable.Range(0, count).Skip(skip).Take(take).Select(Function).ToArray()))
    };
    private sealed class NavigationContext : IConsoleNavigationContext
    {
        public string ScopeRef => Scope.Value;
        public string AddToUri(string uri) => QueryHelpers.AddQueryString(uri, "context", ScopeRef);
    }
    private static BunitContext Context(Handler handler)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(new ResourceListContextProvider { Scope = Scope.Value });
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext());
        context.Services.AddSingleton<ITransformationFunctionsClient>(new TransformationFunctionsApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Handler(Func<bool, int, int, int, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public int FunctionReads { get; private set; }
        public int KindReads { get; private set; }
        public int LastTake { get; private set; }
        public int KindDetailReads { get; private set; }
        public int Writes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = QueryHelpers.ParseQuery(uri.Query);
            Assert.AreEqual(Scope.Value, query["scopeRef"].ToString());
            if (uri.AbsolutePath is "/api/transformation-functions" or "/api/function-kinds")
            {
                Assert.AreEqual(HttpMethod.Get, request.Method);
                var kind = uri.AbsolutePath == "/api/function-kinds";
                var call = kind ? ++KindReads : ++FunctionReads;
                LastTake = int.Parse(query["take"].ToString(), CultureInfo.InvariantCulture);
                return await load(kind, int.Parse(query["skip"].ToString(), CultureInfo.InvariantCulture), LastTake, call);
            }
            Assert.AreEqual("/api/function-kinds/kind-0", uri.AbsolutePath);
            if (request.Method == HttpMethod.Get)
            {
                Assert.AreEqual("catalog-tests", query["namespace"].ToString()); KindDetailReads++;
                var latest = Kind(0) with { Definition = Kind(0).Definition with { Enabled = false } };
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(latest) };
                response.Headers.ETag = new("\"head\""); return response;
            }
            Assert.AreEqual(HttpMethod.Put, request.Method);
            Assert.AreEqual("\"head\"", request.Headers.IfMatch.Single().ToString());
            var body = await request.Content!.ReadFromJsonAsync<FunctionKindResource>(cancellationToken);
            Assert.IsTrue(body!.Definition.Enabled);
            Assert.AreEqual(Namespace, body.Namespace);
            Assert.AreEqual(Scope, body.ScopeRef);
            Assert.AreEqual(Schema.GetRawText(), body.Definition.Versions[0].DefinitionSchema.GetRawText());
            Writes++; return new(HttpStatusCode.OK);
        }
    }
}
