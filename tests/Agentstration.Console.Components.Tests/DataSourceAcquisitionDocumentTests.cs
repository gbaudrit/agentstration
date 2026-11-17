using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DataSourceAcquisitionDocumentTests
{
    [TestMethod]
    [DataRow("en-US", "Save Query draft")]
    [DataRow("fr-FR", "Enregistrer le brouillon de Query")]
    public async Task SelectedAcquisitionQueriesItsPublicationAndSavesIndependentQueryDraft(string culture, string label)
    {
        using var currentCulture = new TestCultureScope(culture);
        using var context = new BunitContext();
        using var handler = new DocumentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<IDataSourcesClient>(new DataSourcesApiClient(http));
        var state = new ConsoleContextState(new ContextProvider());
        await state.EnsureLoadedAsync(default);
        context.Services.AddSingleton(state);
        var page = context.Render<DataSourceAcquisitionDocuments>(p => p.Add(c => c.Acquisition, handler.Acquisition));
        page.WaitForElement("[data-testid=dataset-query-save]");
        Assert.Contains(label, page.Markup);
        Assert.Contains("publication-A", page.Markup);
        await page.Find("[data-testid=dataset-document-query-run]").ClickAsync();
        Assert.AreEqual(handler.Profile, handler.Queries.Single().Profile);
        Assert.AreEqual("{}", handler.Queries.Single().Query!.Value.GetRawText());
        page.WaitForElement("[data-testid=dataset-document-query-next]");
        await page.Find("[data-testid=dataset-document-query-next]").ClickAsync();
        Assert.AreEqual("cursor-A", handler.Queries.Last().Cursor);
        Assert.IsNull(handler.Queries.Last().Query);
        await page.Find("[data-testid=dataset-document-query-payload]").InputAsync("{\"$select\":\"results\"}");
        Assert.IsEmpty(page.FindAll("[data-testid=dataset-document-result]"));
        Assert.IsEmpty(page.FindAll("[data-testid=dataset-document-query-next]"));
        await page.Find("[data-testid=dataset-query-display-name]").InputAsync("Results query");
        Assert.AreEqual("results-query", page.Find("[data-testid=dataset-query-name]").GetAttribute("value"));
        await page.Find("[data-testid=dataset-query-save]").ClickAsync();
        Assert.IsNotNull(handler.SavedQuery);
        Assert.AreEqual("results", handler.SavedQuery.Definition.Query.GetProperty("$select").GetString());
        Assert.AreEqual(handler.Profile, handler.SavedQuery.Definition.Selection.Contract.Profile);
        Assert.IsEmpty(page.FindAll("[data-testid=dataset-document-tool-publish]"));
    }

    [TestMethod]
    public async Task SharedQueryEditorRejectsInvalidPayloadsAndHonorsExecutionPermission()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        using var handler = new DocumentHandler();
        var calls = 0;
        var editor = context.Render<StructuredDocumentQueryEditor>(p => p.Add(c => c.Description, handler.Description.Documents)
            .Add(c => c.RunRequested, _ => calls++));
        await editor.Find("textarea").InputAsync("[]");
        await editor.Find("button").ClickAsync();
        Assert.AreEqual(0, calls);
        Assert.IsNotNull(editor.Find("[role=alert]"));
        await editor.Find("textarea").InputAsync("{}");
        await editor.Find("input[type=number]").ChangeAsync("51");
        await editor.Find("button").ClickAsync();
        Assert.AreEqual(0, calls);
        await editor.Find("input[type=number]").ChangeAsync("5");
        await editor.Find("button").ClickAsync();
        Assert.AreEqual(1, calls);
        editor.Render(p => p.Add(c => c.CanExecute, false));
        Assert.IsTrue(editor.Find("button").HasAttribute("disabled"));
    }

    private sealed class ContextProvider : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Operator", "/instance", ConsoleContextKind.Instance, Guid.NewGuid(), "tenant", "Tenant", Guid.NewGuid(), "workspace", "Workspace",
            new HashSet<string> { "resources/read", "resources/write", "runs/read", "runs/execute" }, [], []));
    }

    private sealed class DocumentHandler : HttpMessageHandler
    {
        public StorageQueryProfilePin Profile { get; } = new("odata/4.01", "1.0", "odata-options+json", "model");
        public DataSourceAcquisitionResource Acquisition { get; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = DataSourceResourceKinds.DataSourceAcquisition, Uid = Guid.NewGuid(),
            Metadata = new() { Name = "acquisition-A" }, DataSourceScopeRef = ResourceScopeRef.Instance,
            DataSourceUid = Guid.NewGuid(), DataSourceName = "alerts", DataSourceNamespace = ResourceNamespace.Default,
            DataSourceGeneration = 1, FlowRunId = "flow-A", State = DataSourceAcquisitionState.Succeeded,
            CorrelationId = "test", RequestHash = "hash", CreatedBy = Guid.NewGuid(), TenantId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(), CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)
        };
        public DataSourceDocumentDescription Description => new(new()
        {
            Source = new("alerts", ResourceScopeRef.Instance, ResourceNamespace.Default), SourceUid = Acquisition.DataSourceUid,
            AcquisitionScope = ResourceScopeRef.Instance, Contract = new(Profile), Mode = "exact", AcquisitionId = Acquisition.Name, AcquisitionUid = Acquisition.Uid
        }, Acquisition.Name, Acquisition.Uid, "publication-A", new(new(true, false, false, true, true,
            [new(Profile.Id, Profile.Version, Profile.Encoding)]), new(QueryProfiles: [Profile.Id]), [Profile], JsonSerializer.SerializeToElement(new { type = "object" }), "model"));
        public DataSourceQueryResource? SavedQuery { get; private set; }
        public List<DataSourceDocumentQueryRequest> Queries { get; } = [];
        public List<PublishDataSourceDocumentToolRequest> Publications { get; } = [];
        public List<string> Paths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/api/datasourcequeries")
            {
                SavedQuery = await request.Content!.ReadFromJsonAsync<DataSourceQueryResource>(cancellationToken);
                var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(SavedQuery) };
                response.Headers.ETag = new("\"query-1\""); return response;
            }
            if (path.EndsWith("/query", StringComparison.Ordinal))
            {
                Queries.Add((await request.Content!.ReadFromJsonAsync<DataSourceDocumentQueryRequest>(cancellationToken))!);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DataSourceDocumentQueryResult(Acquisition.Name, Acquisition.Uid,
                    "publication-A", new(Profile), new([], [], null), Queries.Count == 1 ? "cursor-A" : null)) };
            }
            if (path.EndsWith("/tools", StringComparison.Ordinal))
            {
                Publications.Add((await request.Content!.ReadFromJsonAsync<PublishDataSourceDocumentToolRequest>(cancellationToken))!);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ResourceReference("query-tool", ResourceScopeRef.Instance, ResourceNamespace.Default)) };
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Description) };
        }
    }
}
