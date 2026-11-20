using System.Net;
using System.Net.Http.Json;
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
public sealed class PackProjectFoundationTests
{
    [TestMethod]
    [DataRow("en-US", "Saved pack")]
    [DataRow("fr-FR", "Saved pack")]
    public async Task FrameRetainsActualGeneratedIdentityTagsAndSharedFormState(string culture, string title)
    {
        using var language = new TestCultureScope(culture); using var api = new Handler(); using var context = Context(api); var page = Render(context, api);
        page.WaitForElement("[data-testid=pack-project-identity-summary]"); var frame = page.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.AreEqual(title, frame.Name); Assert.AreEqual(api.Resource.Name, frame.Resource!.Name); Assert.AreNotEqual(api.Resource.Definition.PackName, frame.Resource.Name);
        StringAssert.Contains(page.Find("[data-testid=pack-project-identity-summary]").TextContent, "reviewed");
        CollectionAssert.AreEqual(new[] { "overview", "definition/identity", "definition/configuration", "sources", "builds", "installation", "access", "yaml" }, page.FindAll("[data-resource-section]").Select(value => value.GetAttribute("data-resource-section")).ToArray());
        await Select(page, "definition/identity"); page.WaitForElement("[data-testid=pack-project-display-name]"); var form = page.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(frame.SectionState, form.SectionState); Assert.AreSame(frame.SectionPresentations, form.Sections); Assert.IsInstanceOfType<PackProjectEditorModel>(form.EditContext.Model);
        Assert.IsEmpty(page.FindComponents<ResourceScopePicker>()); Assert.IsTrue(page.FindComponent<ResourceIdentityEditor>().Instance.LabelsReadOnly);
        await Select(page, "yaml"); var yaml = page.FindComponent<ResourceYamlEditor>().Find("textarea"); Assert.IsTrue(yaml.HasAttribute("readonly"));
        StringAssert.Contains(yaml.GetAttribute("value")!, api.Resource.Name); Assert.DoesNotContain("storageKey", yaml.GetAttribute("value")!); Assert.DoesNotContain("PRIVATE-STORAGE-KEY", yaml.GetAttribute("value")!);
    }

    [TestMethod]
    public async Task InvalidMetadataAndOriginalEtagSurviveNavigationAndChangedRefreshUntilExplicitDiscard()
    {
        using var api = new Handler(); using var context = Context(api); var page = Render(context, api); page.WaitForElement("[data-testid=pack-project-identity-summary]");
        await Select(page, "definition/configuration"); page.WaitForElement("[data-testid=pack-project-version]").Input(""); var edit = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        await page.Find("[data-testid=pack-project-form]").SubmitAsync(); Assert.AreEqual(0, api.MetadataWrites);
        api.Resource = api.Resource with { Generation = 8, Definition = api.Resource.Definition with { DisplayName = "Server name" } }; api.Etag = "\"second\"";
        await Select(page, "overview"); await Refresh(page); Assert.AreEqual("Saved pack", page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Name);
        await Select(page, "definition/configuration"); Assert.AreSame(edit, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.AreEqual("", page.Find("[data-testid=pack-project-version]").GetAttribute("value"));
        page.Find("[data-testid=pack-project-version]").Input("2.3.4"); api.WriteFailure = true; await page.Find("[data-testid=pack-project-form]").SubmitAsync();
        Assert.AreEqual("\"first\"", api.IfMatch); Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        page.Find(".unsaved-changes-bar .button-secondary").Click(); page.WaitForAssertion(() => Assert.AreEqual("Server name", page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Name));
        Assert.AreNotSame(edit, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task SourceSavePreservesUnrelatedMetadataAndAnotherSourceDraftAndAcknowledgesTheActualEtag()
    {
        using var api = new Handler(); using var context = Context(api); var page = Render(context, api); page.WaitForElement("[data-testid=pack-project-identity-summary]");
        await Select(page, "definition/identity"); page.WaitForElement("[data-testid=pack-project-display-name]").Input("Pending metadata"); var edit = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        await Select(page, "sources"); page.FindAll(".collection-actions button")[0].Click(); page.WaitForElement("[data-testid=pack-project-source-yaml]").Input("first pending source");
        page.FindAll(".collection-actions button")[1].Click(); var children = page.FindComponents<PackProjectSourceEditor>(); children[1].Find("[data-testid=pack-project-source-yaml]").Input("second pending source");
        children[0].Find("[data-testid=pack-project-source-save]").Click(); page.WaitForAssertion(() => Assert.AreEqual(1, api.SourceWrites));
        Assert.AreEqual("\"first\"", api.IfMatch); Assert.AreEqual("first pending source", api.Documents[0].Source); Assert.IsFalse(children[0].Instance.IsDirty); Assert.IsTrue(children[1].Instance.IsDirty);
        Assert.AreEqual("second pending source", children[1].Find("[data-testid=pack-project-source-yaml]").GetAttribute("value"));
        await Select(page, "definition/identity"); Assert.AreSame(edit, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.AreEqual("Pending metadata", page.Find("[data-testid=pack-project-display-name]").GetAttribute("value"));
        await page.Find("[data-testid=pack-project-form]").SubmitAsync(); Assert.AreEqual(1, api.MetadataWrites); Assert.AreEqual("\"source-saved\"", api.IfMatch); Assert.IsTrue(children[1].Instance.IsDirty);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, ResourceLoadStatus.Forbidden)]
    [DataRow(HttpStatusCode.NotFound, ResourceLoadStatus.NotFound)]
    [DataRow(HttpStatusCode.ServiceUnavailable, ResourceLoadStatus.Failed)]
    public async Task RefreshClassifiesPrivacyFailuresAndRetainsRecoverableDrafts(HttpStatusCode failure, ResourceLoadStatus status)
    {
        using var api = new Handler(); using var context = Context(api); var page = Render(context, api); page.WaitForElement("[data-testid=pack-project-identity-summary]");
        await Select(page, "definition/identity"); page.WaitForElement("[data-testid=pack-project-display-name]").Input("Retained"); api.ReadFailure = failure; await Refresh(page);
        var frame = page.FindComponent<ConsoleResourceDetailsTemplate>().Instance; Assert.AreEqual(status, frame.LoadState!.Status);
        if (failure == HttpStatusCode.ServiceUnavailable) { Assert.AreEqual("Retained", page.Find("[data-testid=pack-project-display-name]").GetAttribute("value")); Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!()); }
        else { Assert.IsNull(frame.Resource); Assert.IsEmpty(page.FindAll("[data-testid=pack-project-display-name]")); Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!()); }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReplacedUidOrOwnerCannotReplaceTheAcceptedResource(bool owner)
    {
        using var api = new Handler(); using var context = Context(api); var page = Render(context, api); page.WaitForElement("[data-testid=pack-project-identity-summary]");
        api.Resource = owner ? api.Resource with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) } : api.Resource with { Uid = Guid.NewGuid() }; await Refresh(page);
        Assert.IsNull(page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
    }

    [TestMethod]
    public async Task MissingBuildHistoryIsUnknownAndManagedAuthoringDoesNotRemoveInstallationPermission()
    {
        using var api = new Handler { BuildFailure = true }; api.Resource = api.Resource with { Governance = ResourceGovernance.Core("fixture") }; using var context = Context(api); var page = Render(context, api);
        page.WaitForElement("[data-testid=pack-project-identity-summary]"); CollectionAssert.AreEqual(new[] { "1", "4", "—", "2" }, page.FindAll(".metric-card strong").Select(value => value.TextContent).ToArray());
        Assert.IsEmpty(page.FindAll("[data-testid=pack-project-build]")); await Select(page, "definition/identity"); page.WaitForElement("[data-testid=pack-project-display-name]"); Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
        api.BuildFailure = false; await Refresh(page); await Select(page, "builds"); page.FindAll("button").Single(value => value.TextContent.Trim() == "Preview").Click();
        page.WaitForAssertion(() => Assert.IsFalse(page.Find("[data-testid=pack-project-install]").HasAttribute("disabled"))); Assert.AreEqual(0, api.MetadataWrites);
    }

    [TestMethod]
    public async Task InstallationSwitchDraftIsRetainedAndExplicitlyDiscardableWithoutChangingMetadata()
    {
        using var api = new Handler { AlreadyInstalled = true }; using var context = Context(api); var page = Render(context, api); page.WaitForElement("[data-testid=pack-project-identity-summary]");
        await Select(page, "builds"); page.FindAll("button").Single(value => value.TextContent.Trim() == "Preview").Click(); page.WaitForElement("input[role=switch]").Change(true);
        Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!()); Assert.IsFalse(page.Find("[data-testid=pack-project-install]").HasAttribute("disabled"));
        await Select(page, "overview"); await Refresh(page); await Select(page, "installation"); Assert.AreEqual("", page.Find("input[role=switch]").GetAttribute("checked"));
        page.FindAll("[data-testid=distribution-pack-project-details] button").Single(value => value.TextContent.Trim() == "Discard").Click(); Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!()); Assert.AreEqual(0, api.MetadataWrites);
    }

    private static Task Select(IRenderedComponent<PackProjectDetails> page, string path) => page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection(path));
    private static Task Refresh(IRenderedComponent<PackProjectDetails> page) => page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
    private static IRenderedComponent<PackProjectDetails> Render(BunitContext context, Handler api) => context.Render<PackProjectDetails>(p => p.Add(value => value.ProjectId, api.Resource.Uid));
    private static BunitContext Context(Handler api)
    {
        var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose; context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(new ConsoleContextState(new Provider())); context.Services.AddSingleton<IPacksClient>(new PacksApiClient(new HttpClient(api) { BaseAddress = new("http://localhost/") }));
        var navigation = context.Services.GetRequiredService<NavigationManager>(); navigation.NavigateTo("http://localhost/pack-projects/" + api.Resource.Uid);
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js"); module.Setup<string>("currentLocation", _ => true).SetResult(navigation.Uri);
        foreach (var path in new[] { "overview", "definition/identity", "definition/configuration", "sources", "builds", "installation", "access", "yaml" }) { var url = navigation.Uri + "#" + path; module.Setup<string>("navigate", call => Equals(call.Arguments[0], url)).SetResult(url); }
        return context;
    }
    private sealed class Provider : IConsoleContextProvider
    {
        private readonly Guid user = Guid.NewGuid(), tenant = Guid.NewGuid(), workspace = Guid.NewGuid();
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken token) => Task.FromResult(new ConsoleContextSnapshot(user, "Author", "/instance", ConsoleContextKind.Instance, tenant, "dev", "Development", workspace, "operations", "Operations", new HashSet<string>(["resources/read", "resources/write"]), [], []));
    }
    private sealed class Handler : HttpMessageHandler
    {
        public PackProjectResource Resource { get; set; } = new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = PackAuthoringKinds.PackProject, ScopeRef = ResourceScopeRef.Instance, Uid = Guid.NewGuid(), Generation = 4, Metadata = new() { Name = "generated-project-identity", Tags = new Dictionary<string, string> { ["team"] = "reviewed" } }, Definition = new() { Publisher = "publisher", PackName = "pack-name", Version = "1.2.3", DisplayName = "Saved pack", SourceArtifact = new("PRIVATE-STORAGE-KEY", "sha256:fixture", 123, "public.pack.zip") } };
        public PackProjectSourceDocument[] Documents { get; private set; } = [new("flows/first.yaml", "Flow", "first", "first saved source"), new("flows/second.yaml", "Flow", "second", "second saved source")];
        public string Etag { get; set; } = "\"first\""; public bool WriteFailure { get; set; }
        public bool BuildFailure { get; set; }
        public bool AlreadyInstalled { get; set; }
        public HttpStatusCode? ReadFailure { get; set; }
        public int MetadataWrites { get; private set; }
        public int SourceWrites { get; private set; }
        public string? IfMatch { get; private set; }
        private readonly Guid buildId = Guid.NewGuid();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/builds", StringComparison.Ordinal)) return BuildFailure ? Error(HttpStatusCode.ServiceUnavailable) : Ok(new[] { new PackProjectBuildResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = PackAuthoringKinds.PackProjectBuild, ScopeRef = ResourceScopeRef.Instance, Uid = buildId, Metadata = new() { Name = "immutable-build" }, Definition = new() { ProjectId = Resource.Uid, ProjectRevision = 1, Publisher = "publisher", PackName = "pack-name", Version = "1.2.3", Artifact = new("PRIVATE-STORAGE-KEY", "sha256:build", 123, "public.pack.zip"), CreatedAt = DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture) } } });
            if (path.EndsWith("/preview", StringComparison.Ordinal)) return Ok(new PackInstallationPreview(new() { Publisher = "publisher", Name = "pack-name", Version = "1.2.3" }, [], AlreadyInstalled));
            if (request.Method == HttpMethod.Get && path.EndsWith("/resources", StringComparison.Ordinal)) return Ok(Documents);
            if (request.Method == HttpMethod.Put)
            {
                IfMatch = request.Headers.IfMatch.Single().ToString();
                if (path.EndsWith("/resources", StringComparison.Ordinal)) { ++SourceWrites; var command = (await request.Content!.ReadFromJsonAsync<UpdatePackProjectSourceCommand>(token))!; Documents = Documents.Select(value => value.Path == command.Path ? value with { Source = command.Source } : value).ToArray(); Etag = "\"source-saved\""; }
                else { ++MetadataWrites; if (WriteFailure) return Error(HttpStatusCode.PreconditionFailed); var command = (await request.Content!.ReadFromJsonAsync<UpdatePackProjectCommand>(token))!; Resource = Resource with { Definition = Resource.Definition with { DisplayName = command.DisplayName, Version = command.Version } }; Etag = "\"metadata-saved\""; }
            }
            else if (ReadFailure is { } failure) return Error(failure);
            var response = Ok(Resource); response.Headers.TryAddWithoutValidation("ETag", Etag); return response;
        }
        private static HttpResponseMessage Ok(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        private static HttpResponseMessage Error(HttpStatusCode status) => new(status) { Content = JsonContent.Create(new { title = "public_fixture_failure" }) };
    }
}
