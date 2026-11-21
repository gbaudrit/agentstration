using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Packs.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SourcesPage = Agentstration.Web.Components.Pages.Sources;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SourceDetailsFoundationTests
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task CommonFrameKeepsSavedIdentityLabelsAndTheExactFormDeclarations(string culture)
    {
        using var locale = new TestCultureScope(culture); var api = new Client(); using var context = Context(api); var page = Render(context);
        page.WaitForElement("[data-testid=source-identity-summary]"); var template = page.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.AreEqual(api.View.Source.Source.Uid, template.Resource!.Uid); Assert.AreEqual("public", template.Resource.Metadata.Tags["team"]);
        CollectionAssert.AreEqual(new[] { "overview", "definition/identity", "definition/configuration", "definition/refresh", "content", "versions", "access", "yaml" }, page.FindAll("[data-resource-section]").Select(value => value.GetAttribute("data-resource-section")).ToArray());
        Assert.IsNull(template.Enabled); Assert.IsTrue(template.YamlReadOnly); Assert.AreEqual(api.View.SelectedVersion.Definition.RawManifest, template.Yaml);
        Assert.IsTrue(page.Find(".resource-overview").InnerHtml.IndexOf("resource-overview-metrics", StringComparison.Ordinal) < page.Find(".resource-overview").InnerHtml.IndexOf("source-identity-summary", StringComparison.Ordinal));
        await Select(page, "definition/identity"); var form = page.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(template.SectionState, form.SectionState); Assert.AreSame(template.SectionPresentations, form.Sections);
        var identity = page.FindComponent<ResourceIdentityEditor>().Instance; Assert.IsTrue(identity.LabelsReadOnly); Assert.IsTrue(identity.DescriptionReadOnly);
        page.Find("[data-testid=source-display-name]").Input("Unsaved local label"); await Select(page, "overview");
        Assert.AreEqual("Saved Source", page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Name);
    }

    [TestMethod]
    public async Task InvalidPolicyAndIdentityDraftsSurviveNavigationRefreshAndExplicitDiscard()
    {
        using var locale = new TestCultureScope("en-US"); var api = new Client(); using var context = Context(api); var page = Render(context);
        await Select(page, "definition/identity"); page.Find("[data-testid=source-display-name]").Input("Pending local label"); var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        await Select(page, "definition/refresh"); page.Find("input[type=number]").Change("invalid");
        api.SetConfiguration(api.View.Source.Configuration.Definition with { DisplayName = "New saved label" }, "\"external\"");
        await Refresh(page); Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.AreEqual("invalid", page.Find("input[type=number]").GetAttribute("value"));
        await Select(page, "definition/identity"); Assert.AreEqual("Pending local label", page.Find("[data-testid=source-display-name]").GetAttribute("value")); Assert.AreEqual("Saved Source", page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Name);
        page.Find(".unsaved-changes-bar .button-secondary").Click(); Assert.AreEqual("New saved label", page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Name); Assert.AreNotSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task SavesOnlyChangedGroupsAndConfirmsEachConfigurationEtagBeforeTheNextWrite()
    {
        using var locale = new TestCultureScope("en-US"); var api = new Client(); using var context = Context(api); var page = Render(context);
        await Select(page, "definition/identity"); page.Find("[data-testid=source-display-name]").Input("Saved new local label");
        await Select(page, "definition/refresh"); page.FindAll("input[type=number]")[1].Change("41");
        await page.Find("[data-testid=source-definition-form]").SubmitAsync();
        CollectionAssert.AreEqual(new[] { "display:\"first\"", "refresh:\"write-1\"" }, api.Writes.ToArray());
        Assert.AreEqual("Saved new local label", api.View.Source.Configuration.Definition.DisplayName); Assert.AreEqual(41, api.View.Source.Configuration.Definition.Refresh.Source.TimeoutSeconds);
        Assert.AreEqual(91, api.View.Source.Configuration.Definition.Refresh.Source.IntervalSeconds); Assert.AreEqual(91, api.View.Source.Configuration.Definition.Refresh.ChannelOverrides["removed-channel"].IntervalSeconds);
        Assert.AreEqual(api.OriginalTarget, api.View.Source.Configuration.Definition.Bindings.Single().Target);
        Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task SaveCapturesEverySubmittedGroupAndRetainsInputReceivedWhileAnEarlierWriteIsPending()
    {
        using var locale = new TestCultureScope("en-US");
        var api = new Client { DisplayWriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var context = Context(api); var page = Render(context);
        await Select(page, "definition/identity"); page.Find("[data-testid=source-display-name]").Input("Submitted label");
        await Select(page, "definition/refresh"); page.FindAll("input[type=number]")[1].Change("41");
        var editContext = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        var submit = page.Find("[data-testid=source-definition-form]").SubmitAsync();
        page.WaitForAssertion(() => Assert.IsTrue(api.DisplayWriteStarted.Task.IsCompleted));
        // A browser event already in transit can arrive before the disabled fieldset is rendered.
        await page.FindAll("input[type=number]")[1].ChangeAsync(new() { Value = "59" });
        api.DisplayWriteGate!.SetResult(true); await submit;
        CollectionAssert.AreEqual(new[] { "display:\"first\"", "refresh:\"write-1\"" }, api.Writes.ToArray());
        Assert.AreEqual("Submitted label", api.View.Source.Configuration.Definition.DisplayName);
        Assert.AreEqual(41, api.View.Source.Configuration.Definition.Refresh.Source.TimeoutSeconds);
        Assert.AreEqual("59", page.FindAll("input[type=number]")[1].GetAttribute("value"));
        Assert.AreSame(editContext, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await page.Find("[data-testid=source-definition-form]").SubmitAsync();
        Assert.AreEqual(59, api.View.Source.Configuration.Definition.Refresh.Source.TimeoutSeconds);
        Assert.AreEqual("refresh:\"write-2\"", api.Writes.Last());
        Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task AcceptedWriteWithUnconfirmedReceiptKeepsOtherDraftsAndCannotBeReplayed()
    {
        using var locale = new TestCultureScope("en-US"); var api = new Client { FailReadAfterWrite = true }; using var context = Context(api); var page = Render(context);
        await Select(page, "definition/identity"); page.Find("[data-testid=source-display-name]").Input("Accepted label"); await Select(page, "definition/refresh"); page.FindAll("input[type=number]")[1].Change("41");
        var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext; await page.Find("[data-testid=source-definition-form]").SubmitAsync();
        Assert.HasCount(1, api.Writes); Assert.IsTrue(page.Find("[data-testid=source-save]").HasAttribute("disabled")); Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.AreEqual("41", page.FindAll("input[type=number]")[1].GetAttribute("value"));
        api.ReadFailure = null; api.FailReadAfterWrite = false; await Refresh(page); await page.Find("[data-testid=source-definition-form]").SubmitAsync();
        CollectionAssert.AreEqual(new[] { "display:\"first\"", "refresh:\"write-1\"" }, api.Writes.ToArray());
        Assert.AreEqual(41, api.View.Source.Configuration.Definition.Refresh.Source.TimeoutSeconds);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, ResourceLoadStatus.Failed)]
    [DataRow(HttpStatusCode.Forbidden, ResourceLoadStatus.Forbidden)]
    [DataRow(HttpStatusCode.NotFound, ResourceLoadStatus.NotFound)]
    public async Task RefreshRetainsRecoverableDraftsAndPurgesPrivateReadLoss(HttpStatusCode status, ResourceLoadStatus expected)
    {
        using var locale = new TestCultureScope("en-US"); var api = new Client(); using var context = Context(api); var page = Render(context);
        await Select(page, "definition/identity"); page.Find("[data-testid=source-display-name]").Input("Private pending label"); api.ReadFailure = status; await Refresh(page);
        Assert.AreEqual(expected, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
        if (status == HttpStatusCode.ServiceUnavailable) Assert.AreEqual("Private pending label", page.Find("[data-testid=source-display-name]").GetAttribute("value"));
        else { Assert.IsNull(page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource); Assert.IsEmpty(page.FindAll("[data-testid=source-display-name]")); Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!()); }
    }

    [TestMethod]
    public async Task ReplacedSourceUidIsRejectedBeforeAuxiliaryPackReads()
    {
        var api = new Client(); using var context = Context(api); var page = Render(context); page.WaitForElement("[data-testid=source-identity-summary]"); var packs = context.Services.GetRequiredService<PackHandler>(); var calls = packs.Calls;
        api.View = api.View with { Source = api.View.Source with { Source = api.View.Source.Source with { Uid = Guid.NewGuid() } } }; await Refresh(page);
        Assert.AreEqual(ResourceLoadStatus.NotFound, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status); Assert.AreEqual(calls, packs.Calls); Assert.IsEmpty(page.FindAll("[data-testid=source-identity-summary]"));
    }

    [TestMethod]
    public async Task InvalidNestedParsingRoutesToRefreshAndRetainsTheOtherIdentitySection()
    {
        using var locale = new TestCultureScope("fr-FR"); var api = new Client(); using var context = Context(api); var page = Render(context);
        await Select(page, "definition/identity"); page.Find("[data-testid=source-display-name]").Input(""); await Select(page, "definition/refresh"); page.Find("input[type=number]").Change("");
        await page.Find("[data-testid=source-definition-form]").SubmitAsync(); page.WaitForAssertion(() => Assert.AreEqual("definition/identity", page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.SectionState.CurrentPath));
        page.Find("[data-testid=source-display-name]").Input("Corrected label"); await page.Find("[data-testid=source-definition-form]").SubmitAsync(); page.WaitForAssertion(() => Assert.AreEqual("definition/refresh", page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.SectionState.CurrentPath));
        Assert.AreEqual("", page.Find("input[type=number]").GetAttribute("value")); Assert.IsEmpty(api.Writes);
    }

    [TestMethod]
    public async Task ImportWizardAllowsIncompleteNavigationAndKeepsTheManifestUntilExplicitImport()
    {
        using var locale = new TestCultureScope("en-US"); var api = new Client(); using var context = Context(api, collection: true); var page = context.Render<SourcesPage>(); page.Find("[data-testid=source-import-open]").Click();
        page.Find("[data-wizard-step=review]").Click(); Assert.AreEqual("false", page.Find("[data-wizard-step=manifest]").GetAttribute("data-step-complete")); page.Find("[data-testid=source-import-submit]").Click();
        page.WaitForAssertion(() => Assert.AreEqual("manifest", page.FindComponent<CreationWizard>().Instance.CurrentStepId)); Assert.AreEqual(0, api.Imports);
        page.Find("[data-testid=source-import-yaml-mode]").Click(); page.Find("[data-testid=source-import-yaml]").Input("public fixture manifest"); page.Find("[data-wizard-step=destination]").Click(); page.Find("[data-wizard-step=manifest]").Click();
        Assert.AreEqual("public fixture manifest", page.Find("[data-testid=source-import-yaml]").GetAttribute("value")); page.Find("[data-wizard-step=review]").Click(); page.Find("[data-testid=source-import-submit]").Click();
        page.WaitForAssertion(() => Assert.AreEqual(1, api.Imports)); Assert.AreEqual(ResourceScopeRef.Instance, api.ImportScope); Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public void PolicyModelPreservesSecondPrecisionAndRemovedOverridesAndKeepsCrossFieldRules()
    {
        var value = SourceEditorModel.From(new Client().View); value.DisplayName = "Only local name";
        Assert.AreEqual(91, value.ToRefreshConfiguration().Source.IntervalSeconds); Assert.AreEqual(91, value.ToRefreshConfiguration().ChannelOverrides["removed-channel"].IntervalSeconds);
        value.SourceRefresh.MaximumBackoffSeconds = 2; value.SourceRefresh.JitterSeconds = 50;
        Assert.IsTrue(value.ValidatePolicies().Any(value => value.MessageKey == "RefreshBackoffOrder")); Assert.IsTrue(value.ValidatePolicies().Any(value => value.MessageKey == "RefreshJitterBound"));
    }

    private static IRenderedComponent<SourcesPage> Render(BunitContext context) => context.Render<SourcesPage>(p => p.Add(value => value.Publisher, "public").Add(value => value.Name, "source"));
    private static Task Select(IRenderedComponent<SourcesPage> page, string path) => page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection(path));
    private static Task Refresh(IRenderedComponent<SourcesPage> page) => page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
    private static BunitContext Context(Client api, bool collection = false)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources"); context.Services.AddSingleton<ISourceConsoleApiClient>(api); context.Services.AddSingleton(new NotificationState()); context.Services.AddSingleton(TimeProvider.System);
        var packs = new PackHandler(); context.Services.AddSingleton(packs); context.Services.AddSingleton<IPacksClient>(new PacksApiClient(new HttpClient(packs) { BaseAddress = new("http://localhost/") })); context.Services.AddSingleton<IResourceScopeInventoryClient>(new ScopeClient());
        var navigation = context.Services.GetRequiredService<NavigationManager>(); var url = "http://localhost/settings/sources" + (collection ? "" : "/public/source?scopeRef=%2Finstance"); navigation.NavigateTo(url);
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js"); module.Setup<string>("currentLocation", _ => true).SetResult(url);
        foreach (var path in new[] { "overview", "definition/identity", "definition/configuration", "definition/refresh", "content", "versions", "access", "yaml" }) { var target = url + "#" + path; module.Setup<string>("navigate", call => Equals(call.Arguments[0], target)).SetResult(target); }
        return context;
    }
    private sealed class PackHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<InstalledPackResource>()) }); }
    }
    private sealed class ScopeClient : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken token) => Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", true)]);
    }
    private sealed class Client : ISourceConsoleApiClient
    {
        public ResourceReference OriginalTarget { get; } = new("provider", ResourceScopeRef.Instance, new ResourceNamespace("providers"));
        public SourceConsoleDetailView View { get; set; }
        public HttpStatusCode? ReadFailure { get; set; }
        public bool FailReadAfterWrite { get; set; }
        public TaskCompletionSource<bool>? DisplayWriteGate { get; set; }
        public TaskCompletionSource<bool> DisplayWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Writes { get; } = [];
        public int Imports { get; private set; }
        public ResourceScopeRef? ImportScope { get; private set; }
        public Client()
        {
            var uid = Guid.NewGuid(); var scope = ResourceScopeRef.Instance; var ns = new ResourceNamespace("public"); var versionUid = Guid.NewGuid();
            var source = new SourceResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = SourceResourceKinds.Source, Uid = uid, ScopeRef = scope, ETag = "\"source\"", Metadata = new() { Name = "source", Namespace = ns, Tags = new Dictionary<string, string> { ["team"] = "public" } }, Definition = new() { Publisher = "public" } };
            var policy = new SourceRefreshPolicy { IntervalSeconds = 91 };
            var configuration = new SourceConfigurationResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = SourceResourceKinds.SourceConfiguration, Uid = Guid.NewGuid(), ScopeRef = scope, ETag = "\"first\"", Metadata = source.Metadata, Definition = new() { SourceUid = uid, DisplayName = "Saved Source", Bindings = [new() { Name = "main", TargetKind = SourceResourceKinds.SourceProvider, Target = OriginalTarget }], Refresh = new() { Source = policy, ChannelOverrides = new Dictionary<string, SourceRefreshPolicy> { ["removed-channel"] = policy } } } };
            var observed = new SourceObservedResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = SourceResourceKinds.SourceObservedState, Uid = Guid.NewGuid(), ScopeRef = scope, Metadata = source.Metadata, Definition = new() { SourceUid = uid, LastAttemptAt = DateTimeOffset.UnixEpoch, LastOutcome = SourceImportOutcome.Created } };
            var published = new PublishedSourceVersionDefinition { Version = "1.0.0", Description = "Published description", Publisher = new() { Name = "public" } };
            var version = new SourceVersionResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = SourceResourceKinds.SourceVersion, Uid = versionUid, ScopeRef = scope, Metadata = source.Metadata, Definition = new() { SourceUid = uid, SourceName = "source", Publisher = "public", Version = "1.0.0", ManifestDigest = "public-digest", RawManifest = "kind: SourceVersion", PublishedDefinition = published, ImportedAt = DateTimeOffset.UnixEpoch } };
            View = new(new(source, configuration, observed, 1), [version], version, new(SourceVerificationStatus.Unverified, "public_fixture", published.Publisher, null, null, []), new(versionUid, "1.0.0", configuration.ETag!, true, [], []), [], [], []);
        }
        public void SetConfiguration(SourceConfigurationProperties value, string etag) => View = View with { Source = View.Source with { Configuration = View.Source.Configuration with { Definition = value, ETag = etag } }, Bindings = View.Bindings with { ConfigurationETag = etag } };
        public Task<IReadOnlyList<SourceConsoleListItem>> ListAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<SourceConsoleListItem>>([]);
        public Task<SourceConsoleDetailView> GetAsync(ResourceScopeRef scope, string publisher, string name, Guid? versionUid, string? locale, CancellationToken token) => ReadFailure is { } status ? Task.FromException<SourceConsoleDetailView>(new HttpRequestException("public fixture read failure", null, status)) : Task.FromResult(View);
        public async Task UpdateDisplayNameAsync(ResourceScopeRef scope, string publisher, string name, string displayName, string etag, CancellationToken token)
        {
            DisplayWriteStarted.TrySetResult(true);
            if (DisplayWriteGate is { } gate) await gate.Task.WaitAsync(token);
            Write("display", etag, View.Source.Configuration.Definition with { DisplayName = displayName.Trim() });
        }
        public Task UpdateRefreshConfigurationAsync(ResourceScopeRef scope, string publisher, string name, SourceRefreshConfiguration refresh, string etag, CancellationToken token) { Write("refresh", etag, View.Source.Configuration.Definition with { Refresh = refresh }); return Task.CompletedTask; }
        private void Write(string operation, string etag, SourceConfigurationProperties value) { Assert.AreEqual(View.Source.Configuration.ETag, etag); Writes.Add(operation + ":" + etag); SetConfiguration(value, "\"write-" + Writes.Count + "\""); if (FailReadAfterWrite) ReadFailure = HttpStatusCode.ServiceUnavailable; }
        public Task<SourceImportResult> ImportYamlAsync(string manifest, ResourceScopeRef scope, CancellationToken token) { Imports++; ImportScope = scope; return Task.FromResult(new SourceImportResult(View.Source, View.SelectedVersion, SourceImportOutcome.Created, View.Verification)); }
        public Task<SourceImportResult> ImportUrlAsync(Uri url, ResourceScopeRef scope, CancellationToken token) => ImportYamlAsync(url.AbsoluteUri, scope, token);
        public Task DeleteSourceAsync(ResourceScopeRef scope, string publisher, string name, string etag, CancellationToken token) => throw new NotSupportedException();
        public Task ConfigureBindingsAsync(ResourceScopeRef scope, string publisher, string name, Guid versionUid, IReadOnlyList<SourceConsoleBindingSelection> selections, string etag, CancellationToken token) => throw new NotSupportedException();
        public Task RefreshChannelAsync(ResourceScopeRef scope, string publisher, string name, Guid versionUid, string channel, CancellationToken token) => throw new NotSupportedException();
        public Task<SourceImportResult> RefreshSourceAsync(ResourceScopeRef scope, string publisher, string name, CancellationToken token) => throw new NotSupportedException();
        public Task<SourcePackInstallationPreview> PreviewPackAsync(SourcePackSelection selection, IReadOnlyList<PackBindingSelection> bindings, bool replaceExisting, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<InstalledPackResource>> InstallPackAsync(SourcePackSelection selection, string digest, bool replaceExisting, IReadOnlyList<PackBindingSelection> bindings, CancellationToken token) => throw new NotSupportedException();
    }
}
