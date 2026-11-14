using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DataSourceQueryViewsTests
{
    [TestMethod]
    [DataRow("en-US", "Latest published version")]
    [DataRow("fr-FR", "Dernière version publiée")]
    public async Task QueryDraftsAndNamedToolPublicationsReloadAcrossViews(string culture, string label)
    {
        using var currentCulture = new TestCultureScope(culture);
        using var context = new BunitContext();
        using var handler = new QueryHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<IDataSourcesClient>(new DataSourcesApiClient(http));
        var state = new ConsoleContextState(new ContextProvider()); await state.EnsureLoadedAsync(default);
        context.Services.AddSingleton(state);
        var page = context.Render<DataSourceQueries>(p => p.Add(c => c.Source, handler.Source));
        page.WaitForElement("[data-testid=dataset-query-row]");
        await page.Find("[data-testid=dataset-query-row] button").ClickAsync();
        page.WaitForElement("[data-testid=dataset-query-edit-json]");
        Assert.IsTrue(page.Find("[data-testid=dataset-query-edit-name]").HasAttribute("readonly"));
        await page.Find("[data-testid=dataset-query-edit-json]").ChangeAsync("{\"$filter\":\"dataset eq 'beta'\"}");
        await page.Find("[data-testid=dataset-query-edit-save]").ClickAsync();
        Assert.AreEqual("dataset eq 'beta'", handler.Query.Definition.Query.GetProperty("$filter").GetString());
        await page.Find("[data-testid=dataset-query-publish-version]").ChangeAsync("B");
        await page.Find("[data-testid=dataset-query-publish]").ClickAsync();
        Assert.AreEqual("B", handler.PublishedVersion);
        page.Render(p => p.Add(c => c.ShowTools, true));
        await page.Find("[data-testid=dataset-tool-query]").ChangeAsync(handler.Query.Uid.ToString());
        Assert.Contains(label, page.Markup);
        await page.Find("[data-testid=dataset-query-tool-display-name]").InputAsync("Beta query tool");
        Assert.AreEqual("beta-query-tool", page.Find("[data-testid=dataset-query-tool-name]").GetAttribute("value"));
        await page.Find("[data-testid=dataset-query-tool-publish]").ClickAsync();
        Assert.IsNotNull(handler.Publication); Assert.IsNull(handler.Publication.QueryVersion);
        page.WaitForElement("[data-testid=dataset-query-tool-row]");
        await page.Find("[data-testid=dataset-tool-query-version]").ChangeAsync("A");
        await page.Find("[data-testid=dataset-query-tool-version]").ChangeAsync("2.0.0");
        await page.Find("[data-testid=dataset-query-tool-publish]").ClickAsync();
        Assert.AreEqual("A", handler.Publication.QueryVersion);
        page.Dispose();
        var reloaded = context.Render<DataSourceQueries>(p => p.Add(c => c.Source, handler.Source).Add(c => c.ShowTools, true));
        reloaded.WaitForElement("[data-testid=dataset-query-tool-row]");
        Assert.Contains("2.0.0", reloaded.Markup);
        Assert.IsNotNull(reloaded.Find("[data-testid=dataset-query-tool-row] a[href*='/tools/']"));
    }

    private sealed class ContextProvider : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken ct) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Operator", "/instance", ConsoleContextKind.Instance, Guid.NewGuid(), "tenant", "Tenant", Guid.NewGuid(), "workspace", "Workspace",
            new HashSet<string> { "resources/read", "resources/write", "runs/read", "runs/execute" }, [], []));
    }
    private sealed class QueryHandler : HttpMessageHandler
    {
        public DataSourceResource Source { get; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = DataSourceResourceKinds.DataSource, Uid = Guid.NewGuid(),
            Metadata = new() { Name = "alerts" }, ScopeRef = ResourceScopeRef.Instance, Definition = new() { DisplayName = "Alerts" }
        };
        public DataSourceQueryResource Query { get; private set; }
        public string PublishedVersion { get; private set; } = "A";
        public PublishDataSourceQueryToolRequest? Publication { get; private set; }
        public QueryHandler()
        {
            Query = new()
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = DataSourceResourceKinds.DataSourceQuery, Uid = Guid.NewGuid(),
                Metadata = new() { Name = "only-alpha" }, ScopeRef = ResourceScopeRef.Instance,
                Definition = new() { DisplayName = "Only alpha", Query = JsonSerializer.SerializeToElement(new { }), Selection = new()
                { Source = new(Source.Name, Source.ScopeRef, Source.Namespace), SourceUid = Source.Uid, AcquisitionScope = ResourceScopeRef.Instance,
                    Contract = new(new StorageQueryProfilePin("odata/4.01", "1.0", "odata-options+json", "model")) } }
            };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (path.EndsWith("/tools", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    Publication = await request.Content!.ReadFromJsonAsync<PublishDataSourceQueryToolRequest>(ct);
                    body = new ResourceReference("agentstration.beta-query-tool.v" + Publication!.Version, ResourceScopeRef.Instance, ResourceNamespace.Default);
                }
                else body = Publication is null ? Array.Empty<DataSourceQueryToolPublication>() : new[] { new DataSourceQueryToolPublication(
                    new("agentstration.beta-query-tool.v" + Publication.Version, ResourceScopeRef.Instance, ResourceNamespace.Default),
                    Query.Uid, Query.Name, Publication.Version, Publication.QueryVersion, null) };
            }
            else if (path.EndsWith("/versions", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post) PublishedVersion = (await request.Content!.ReadFromJsonAsync<PublishDataSourceQueryRequest>(ct))!.Version;
                var version = new DataSourceQueryVersionResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1, Kind = DataSourceResourceKinds.DataSourceQueryVersion,
                    Metadata = new() { Name = "query-version" }, ScopeRef = Query.ScopeRef, Uid = Guid.NewGuid(),
                    QueryUid = Query.Uid, QueryName = Query.Name, QueryGeneration = 1, Version = PublishedVersion,
                    Definition = Query.Definition, DefinitionDigest = "definition", ContractDigest = "contract", PublishedAt = DateTimeOffset.UnixEpoch
                };
                body = request.Method == HttpMethod.Post ? version : new[] { version with { Version = "A" }, version };
            }
            else if (path == "/api/datasourcequeries") body = new[] { Query };
            else
            {
                if (request.Method == HttpMethod.Put) Query = Query with { Definition = (await request.Content!.ReadFromJsonAsync<DataSourceQueryProperties>(ct))! };
                body = Query;
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) };
            response.Headers.ETag = new("\"query\""); return response;
        }
    }
}
