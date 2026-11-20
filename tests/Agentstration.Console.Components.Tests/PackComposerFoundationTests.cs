using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Packs.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PackComposerFoundationTests
{
    [TestMethod]
    [DataRow("en-US", "Pack identity")]
    [DataRow("fr-FR", "Identité du Pack")]
    public void WizardKeepsActualRequestScopeAndRetainedFieldsWithoutAnUnsupportedOwnerSelector(string culture, string identity)
    {
        using var language = new TestCultureScope(culture); using var api = new Handler(); using var context = Context(api); var page = context.Render<PackComposer>();
        page.WaitForElement("[data-wizard-step=review]").Click();
        CollectionAssert.AreEqual(new[] { "identity", "resources", "review" }, page.FindAll("[data-wizard-step]").Select(value => value.GetAttribute("data-wizard-step")).ToArray());
        StringAssert.Contains(page.Find("[data-wizard-step=identity]").TextContent, identity);
        Assert.AreEqual("false", page.Find("[data-wizard-step=identity]").GetAttribute("data-step-complete")); Assert.IsEmpty(page.FindComponents<ResourceScopePicker>());
        page.Find("[data-wizard-step=identity]").Click(); page.Find("[data-testid=pack-project-display-name]").Input("Retained name");
        var original = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        Assert.IsInstanceOfType<PackComposerModel>(original.Model); Assert.IsTrue(page.FindAll(".resource-scope-badge").All(value => value.GetAttribute("title") == ResourceScopeRef.Instance.Value));
        page.Find("[data-wizard-step=resources]").Click(); page.Find("[data-wizard-step=identity]").Click();
        Assert.AreSame(original, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.AreEqual("Retained name", page.Find("[data-testid=pack-project-display-name]").GetAttribute("value"));
        Assert.AreEqual(0, api.Creates); Assert.AreEqual(1, api.CatalogReads);
    }

    [TestMethod]
    public async Task CreationPreservesThePackCoordinateMetadataAndExactSelectedResourceKeys()
    {
        using var api = new Handler(); using var context = Context(api); var page = context.Render<PackComposer>(); Fill(page);
        var model = (PackComposerModel)page.FindComponent<ResourceDefinitionForm>().Instance.EditContext.Model;
        model.Categories = "automation, support, AUTOMATION"; model.Tags = "local, sample, LOCAL";
        page.Find("[data-wizard-step=resources]").Click(); page.Find(".composer-add").Click(); page.Find("[data-wizard-step=review]").Click();
        page.Find("[data-testid=pack-project-create]").Click(); page.WaitForAssertion(() => Assert.AreEqual(1, api.Creates));
        Assert.AreEqual("publisher", api.Command!.Publisher); Assert.AreEqual("composed-pack", api.Command.Name); Assert.AreEqual("1.2.3", api.Command.Version);
        CollectionAssert.AreEqual(new[] { "automation", "support" }, api.Command.Categories.ToArray()); CollectionAssert.AreEqual(new[] { "local", "sample" }, api.Command.Tags.ToArray());
        Assert.AreEqual(new PackCompositionResourceKey("Flow", "source-flow"), api.Command.Resources.Single());
        Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        StringAssert.Contains(context.Services.GetRequiredService<NavigationManager>().Uri, api.ProjectUid.ToString("D"));
        await Task.CompletedTask;
    }

    [TestMethod]
    public void ARejectedCreationRetainsTheEntireEditableWizardAndDoesNotRetryTheMutation()
    {
        using var api = new Handler { CreateFailure = true }; using var context = Context(api); var page = context.Render<PackComposer>(); Fill(page);
        page.Find("[data-wizard-step=resources]").Click(); page.Find(".composer-add").Click(); page.Find("[data-wizard-step=review]").Click();
        page.Find("[data-testid=pack-project-create]").Click(); page.WaitForAssertion(() => Assert.AreEqual(1, api.Creates));
        Assert.IsNotNull(page.Find("[data-testid=pack-composer-form]")); Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        page.Find("[data-wizard-step=identity]").Click(); Assert.AreEqual("Composed Pack", page.Find("[data-testid=pack-project-display-name]").GetAttribute("value"));
        Assert.AreEqual(1, api.Creates); StringAssert.Contains(context.Services.GetRequiredService<NavigationManager>().Uri, "/pack-projects/new");
    }

    [TestMethod]
    public void FailedPreviewCannotAuthorizeCreationAndItsReadOnlyRetryKeepsTheSelectionAndMetadata()
    {
        using var api = new Handler { PreviewFailure = true }; using var context = Context(api); var page = context.Render<PackComposer>(); Fill(page);
        page.Find("[data-wizard-step=resources]").Click(); page.Find(".composer-add").Click(); page.WaitForElement(".form-alert-danger"); StringAssert.Contains(page.Find(".composer-summary").TextContent, "Not previewed"); Assert.AreEqual("—", page.FindAll(".composer-summary strong")[1].TextContent); page.Find("[data-wizard-step=review]").Click();
        Assert.IsTrue(page.Find("[data-testid=pack-project-create]").HasAttribute("disabled")); Assert.AreEqual(0, api.Creates);
        api.PreviewFailure = false; page.Find(".form-alert-danger button").Click(); page.WaitForAssertion(() => Assert.IsFalse(page.Find("[data-testid=pack-project-create]").HasAttribute("disabled")));
        Assert.AreEqual(2, api.Previews); Assert.AreEqual(1, api.LastPreview!.Resources.Count); Assert.AreEqual(0, api.Creates);
        page.Find("[data-wizard-step=identity]").Click(); Assert.AreEqual("Composed Pack", page.Find("[data-testid=pack-project-display-name]").GetAttribute("value"));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    [DataRow(HttpStatusCode.Forbidden)]
    public void CatalogRefreshPreservesRecoverableDraftsAndClearsPrivateContentOnReadDenial(HttpStatusCode status)
    {
        using var api = new Handler(); using var context = Context(api); var page = context.Render<PackComposer>(); Fill(page); var original = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        page.Find("[data-wizard-step=resources]").Click(); page.Find(".composer-add").Click(); api.CatalogFailure = status; page.Find("[data-testid=pack-composer-refresh]").Click();
        page.WaitForAssertion(() => Assert.AreEqual(2, api.CatalogReads));
        if (status == HttpStatusCode.Forbidden) { Assert.IsEmpty(page.FindAll("[data-testid=pack-composer-form]")); Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!()); }
        else { Assert.AreSame(original, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!()); page.Find("[data-wizard-step=identity]").Click(); Assert.AreEqual("Composed Pack", page.Find("[data-testid=pack-project-display-name]").GetAttribute("value")); }
    }

    [TestMethod]
    public async Task ReadOnlyCreationCannotWriteEvenWhenTheFinishCallbackIsInvoked()
    {
        using var api = new Handler(); using var context = Context(api, write: false); var page = context.Render<PackComposer>(); page.WaitForElement("[data-testid=pack-composer-form]");
        Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly); Assert.IsFalse(page.FindComponent<CreationWizard>().Instance.CanFinish);
        await page.InvokeAsync(() => page.FindComponent<CreationWizard>().Instance.OnFinish.InvokeAsync(new CreationWizardFinish())); Assert.AreEqual(0, api.Creates);
    }

    [TestMethod]
    public async Task ChangingContextDuringCreationClearsTheOldDraftAndIgnoresItsLateAcknowledgement()
    {
        using var api = new Handler(); var provider = new Provider(true); using var context = Context(api, provider: provider); var page = context.Render<PackComposer>(); Fill(page);
        page.Find("[data-wizard-step=resources]").Click(); page.Find(".composer-add").Click(); page.Find("[data-wizard-step=review]").Click();
        var pending = new TaskCompletionSource(); api.HoldCreation = pending.Task;
        var creation = page.Find("[data-testid=pack-project-create]").ClickAsync(new()); page.WaitForAssertion(() => Assert.AreEqual(1, api.Creates));
        provider.Scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        await page.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(default));
        page.WaitForAssertion(() => Assert.AreEqual(2, api.CatalogReads));
        Assert.AreEqual(string.Empty, ((PackComposerModel)page.FindComponent<ResourceDefinitionForm>().Instance.EditContext.Model).Name);
        Assert.IsFalse(page.FindComponent<ResourceDefinitionForm>().Instance.Busy); Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await page.InvokeAsync(pending.SetResult); await creation;
        StringAssert.Contains(context.Services.GetRequiredService<NavigationManager>().Uri, "/pack-projects/new");
        Assert.AreEqual(string.Empty, ((PackComposerModel)page.FindComponent<ResourceDefinitionForm>().Instance.EditContext.Model).Name);
        Assert.IsTrue(page.FindAll(".resource-scope-badge").All(value => value.GetAttribute("title") == provider.Scope.Value));
    }

    private static void Fill(IRenderedComponent<PackComposer> page)
    {
        page.WaitForElement("[data-testid=pack-project-publisher]").Input("publisher"); page.Find("[data-testid=pack-project-display-name]").Input("Composed Pack"); page.Find("[data-testid=pack-project-name]").Input("composed-pack"); page.Find("[data-testid=pack-project-version]").Input("1.2.3");
    }
    private static BunitContext Context(Handler handler, bool write = true, Provider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose; context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(new ConsoleContextState(provider ?? new Provider(write))); context.Services.AddSingleton<IPacksClient>(new PacksApiClient(new HttpClient(handler) { BaseAddress = new("http://localhost/") }));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/pack-projects/new"); return context;
    }
    private sealed class Provider(bool write) : IConsoleContextProvider
    {
        private readonly Guid userId = Guid.NewGuid(), tenantId = Guid.NewGuid(), operationalWorkspace = Guid.NewGuid();
        public ResourceScopeRef Scope { get; set; } = ResourceScopeRef.Instance;
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken token) => Task.FromResult(new ConsoleContextSnapshot(userId, "Author", Scope.Value, Scope.Kind == ResourceScopeKind.Workspace ? ConsoleContextKind.Workspace : ConsoleContextKind.Instance, tenantId, "dev", "Development", Scope.Kind == ResourceScopeKind.Workspace ? Guid.Parse(Scope.Value.Split('/')[^1]) : operationalWorkspace, "operations", "Instance operations", new HashSet<string>(write ? ["resources/read", "resources/write"] : ["resources/read"]), [], []));
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int CatalogReads { get; private set; } public int Previews { get; private set; } public int Creates { get; private set; }
        public bool PreviewFailure { get; set; } public bool CreateFailure { get; set; } public Task? HoldCreation { get; set; } public HttpStatusCode? CatalogFailure { get; set; }
        public CreatePackProjectFromWorkspaceCommand? Command { get; private set; } public PreviewPackCompositionCommand? LastPreview { get; private set; }
        public Guid ProjectUid { get; } = Guid.NewGuid();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("composer/resources", StringComparison.Ordinal)) { ++CatalogReads; if (CatalogFailure is { } failure) return Error(failure); return Ok(new[] { new PackCompositionCatalogItem { Resource = new("Flow", "source-flow"), DisplayName = "Source Flow", Availability = PackCompositionAvailability.Selectable, Status = "Published" } }); }
            if (path.EndsWith("composer/preview", StringComparison.Ordinal)) { ++Previews; LastPreview = (await request.Content!.ReadFromJsonAsync<PreviewPackCompositionCommand>(token))!; if (PreviewFailure) return Error(HttpStatusCode.ServiceUnavailable); return Ok(new PackCompositionPreview(LastPreview.Resources.Select(value => new PackCompositionPreviewResource(value, value.Name, "flows/" + value.Name + ".yaml", true, [])).ToArray(), [], [])); }
            if (request.Method == HttpMethod.Post && path == "/api/pack-projects") { ++Creates; Command = (await request.Content!.ReadFromJsonAsync<CreatePackProjectFromWorkspaceCommand>(token))!; if (CreateFailure) return Error(HttpStatusCode.Conflict); if (HoldCreation is { } pending) await pending; var response = Ok(new PackProjectResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = PackAuthoringKinds.PackProject, Uid = ProjectUid, ScopeRef = ResourceScopeRef.Instance, Metadata = new() { Name = ProjectUid.ToString("N") }, Definition = new() { Publisher = Command.Publisher, PackName = Command.Name, Version = Command.Version, DisplayName = Command.DisplayName, SourceArtifact = new("public-key", "sha256:public-fixture", 1, "public.pack.zip") } }); response.Headers.TryAddWithoutValidation("ETag", "\"created\""); return response; }
            throw new InvalidOperationException(path);
        }
        private static HttpResponseMessage Error(HttpStatusCode status) => new(status) { Content = JsonContent.Create(new { title = "public_fixture_failure", detail = "Public fixture request rejected." }) };
        private static HttpResponseMessage Ok(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
