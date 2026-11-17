using System.Net;
using System.Net.Http.Json;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class StorageDocumentExecutionTests
{
    [TestMethod]
    [DataRow(ConsoleContextKind.Instance)]
    [DataRow(ConsoleContextKind.Tenant)]
    [DataRow(ConsoleContextKind.Workspace)]
    public async Task AuthorizedContextExecutesWithTheInitiallyDisplayedQueryProfile(ConsoleContextKind kind)
    {
        using var context = new BunitContext();
        using var fixture = new StructuredStorageUiFixture(true);
        using var handler = new DocumentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<IStoragesClient>(new StoragesApiClient(http));
        context.Services.AddSingleton(TimeProvider.System);
        var state = new ConsoleContextState(new ContextProvider(kind, true));
        await state.EnsureLoadedAsync(default);
        context.Services.AddSingleton(state);
        var page = context.Render<StorageDocuments>(p => p.Add(c => c.Resource, fixture.Storage));
        page.Find("select").Change(fixture.Collection.ToString());
        await page.Find("button").ClickAsync();
        Assert.IsFalse(page.Find(".button-primary").HasAttribute("disabled"));
        await page.Find(".button-primary").ClickAsync();
        Assert.IsNotNull(handler.Query);
        Assert.AreEqual("odata/4.01", handler.Query.Profile.Id);
        Assert.AreEqual(50, handler.Query.PageSize);
    }

    [TestMethod]
    [DataRow("en-US", "Executing a document operation requires")]
    [DataRow("fr-FR", "L’exécution d’une opération documentaire nécessite")]
    public async Task MissingExecutionPermissionRemainsBlockedWithAnExplanation(string culture, string help)
    {
        using var currentCulture = new TestCultureScope(culture);
        using var context = new BunitContext();
        using var fixture = new StructuredStorageUiFixture(true);
        using var handler = new DocumentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<IStoragesClient>(new StoragesApiClient(http));
        context.Services.AddSingleton(TimeProvider.System);
        var state = new ConsoleContextState(new ContextProvider(ConsoleContextKind.Instance, false));
        await state.EnsureLoadedAsync(default);
        context.Services.AddSingleton(state);
        var page = context.Render<StorageDocuments>(p => p.Add(c => c.Resource, fixture.Storage));
        page.Find("select").Change(fixture.Collection.ToString());
        await page.Find("button").ClickAsync();
        Assert.IsTrue(page.Find(".button-primary").HasAttribute("disabled"));
        Assert.Contains(help, page.Markup);
        Assert.IsNull(handler.Query);
    }

    private sealed class ContextProvider(ConsoleContextKind kind, bool canExecute) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Operator", "/instance", kind, Guid.NewGuid(), "tenant", "Tenant", Guid.NewGuid(), "workspace", "Workspace",
            canExecute ? new HashSet<string> { "resources/read", "runs/execute" } : new HashSet<string> { "resources/read" }, [], []));
    }

    private sealed class DocumentHandler : HttpMessageHandler
    {
        public StorageDocumentQuery? Query { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/query", StringComparison.Ordinal))
            {
                Query = await request.Content!.ReadFromJsonAsync<StorageDocumentQuery>(cancellationToken);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new StorageDocumentPage([], [], null)) };
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new StorageDocumentDescription(
                new(true, false, false, true, false, [new("odata/4.01", "1.0", "odata-options+json")]),
                new(QueryProfiles: ["odata/4.01"]), [new("odata/4.01", "1.0", "odata-options+json", "model")],
                System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }), "model")) };
        }
    }
}
