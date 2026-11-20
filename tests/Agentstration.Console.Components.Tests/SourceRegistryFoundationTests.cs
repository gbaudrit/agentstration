using System.Net;
using System.Net.Http.Json;
using Agentstration.Resources;
using Agentstration.Secrets.Contracts;
using Agentstration.Sources.Contracts;
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
public sealed class SourceRegistryFoundationTests
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task PersistedFrameKeepsIdentityTagsAndSharedSectionsWithRealFields(string culture)
    {
        using var language = new TestCultureScope(culture); using var api = new Handler(); using var context = Context(api); var page = Render(context); page.WaitForElement("[data-testid=source-registry-identity-summary]");
        var frame = page.FindComponent<ConsoleResourceDetailsTemplate>().Instance; Assert.AreEqual(api.Resource.Uid, frame.Resource!.Uid); StringAssert.Contains(page.Find("[data-testid=source-registry-identity-summary]").TextContent, "reviewed");
        CollectionAssert.AreEqual(new[] { "overview", "definition/identity", "definition/configuration", "definition/refresh", "origin", "history", "access", "yaml" }, page.FindAll("[data-resource-section]").Select(value => value.GetAttribute("data-resource-section")).ToArray());
        await Select(page, "definition/identity"); page.WaitForElement("[data-testid=source-registry-display-name]"); var form = page.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(frame.SectionState, form.SectionState); Assert.AreSame(frame.SectionPresentations, form.Sections); Assert.IsInstanceOfType<SourceRegistryEditorModel>(form.EditContext.Model); Assert.IsTrue(page.FindComponent<ResourceIdentityEditor>().Instance.LabelsReadOnly); Assert.IsTrue(page.FindComponent<ResourceIdentityEditor>().Instance.DescriptionReadOnly);
        await Select(page, "yaml"); var yaml = page.FindComponent<ResourceYamlEditor>().Find("textarea"); Assert.IsTrue(yaml.HasAttribute("readonly")); StringAssert.Contains(yaml.GetAttribute("value")!, "credentials"); Assert.AreEqual(50, api.HistoryBound);
    }
    [TestMethod]
    [DataRow("en-US", 0)]
    [DataRow("fr-FR", 0)]
    [DataRow("en-US", 1)]
    [DataRow("fr-FR", 1)]
    public async Task ClearedRequiredSelectionsExposeTheirAssociatedInlineErrorWithoutWriting(string culture, int index)
    {
        using var language = new TestCultureScope(culture);
        using var api = new Handler();
        using var context = Context(api);
        var page = Render(context);
        await Select(page, "definition/configuration");
        var select = page.WaitForElements("[data-testid=source-registry-form] select")[index];
        select.Change(string.Empty);
        var messages = page.Find($"#{select.GetAttribute("id")}-messages");
        Assert.IsFalse(string.IsNullOrWhiteSpace(messages.TextContent));
        StringAssert.Contains(select.GetAttribute("aria-describedby")!, messages.Id);
        Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.EditContext.GetValidationMessages().Any());
        Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.AreEqual(0, api.Writes);
    }

    [TestMethod]
    public async Task InvalidNumericAndMetadataDraftsKeepTheirContextAndOriginalEtagUntilExplicitDiscard()
    {
        using var api = new Handler(); using var context = Context(api); var page = Render(context); page.WaitForElement("[data-testid=source-registry-identity-summary]");
        await Select(page, "definition/identity"); page.WaitForElement("[data-testid=source-registry-display-name]").Input("Pending name"); var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        await Select(page, "definition/refresh"); page.Find("input[type=number]").Change("invalid"); api.Resource = api.Resource with { Definition = api.Resource.Definition with { DisplayName = "Server name" } }; api.Etag = "\"second\"";
        await Refresh(page); Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.AreEqual("invalid", page.Find("input[type=number]").GetAttribute("value")); Assert.AreEqual("Saved registry", page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Name);
        await Select(page, "definition/identity"); Assert.AreEqual("Pending name", page.Find("[data-testid=source-registry-display-name]").GetAttribute("value"));
        page.Find(".unsaved-changes-bar .button-secondary").Click(); Assert.AreEqual("Server name", page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Name); Assert.AreNotSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }
    [TestMethod]
    public async Task HeaderActivationAcknowledgesOnlyItsSavedPropertyAndPreservesTheOtherDraftAndCredentialIdentity()
    {
        using var api = new Handler(); using var context = Context(api); var page = Render(context); page.WaitForElement("[data-testid=source-registry-enabled]"); await Select(page, "definition/identity"); page.WaitForElement("[data-testid=source-registry-display-name]").Input("Pending name"); var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        page.Find("[data-testid=source-registry-enabled]").Change(false); page.WaitForAssertion(() => Assert.AreEqual(1, api.Writes)); Assert.AreEqual("\"first\"", api.IfMatch); Assert.AreEqual("Saved registry", api.Command!.DisplayName); Assert.IsFalse(api.Command.Enabled); Assert.AreEqual(api.Resource.Definition.Credential, api.Command.Credential);
        Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.AreEqual("Pending name", page.Find("[data-testid=source-registry-display-name]").GetAttribute("value"));
        await page.Find("[data-testid=source-registry-form]").SubmitAsync(); Assert.AreEqual(2, api.Writes); Assert.AreEqual("\"saved-1\"", api.IfMatch); Assert.AreEqual("Pending name", api.Command.DisplayName); Assert.IsFalse(api.Command.Enabled); Assert.AreEqual(new ResourceNamespace("credentials"), api.Command.Credential!.Namespace);
    }
    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, ResourceLoadStatus.Forbidden)]
    [DataRow(HttpStatusCode.NotFound, ResourceLoadStatus.NotFound)]
    [DataRow(HttpStatusCode.ServiceUnavailable, ResourceLoadStatus.Failed)]
    public async Task RefreshKeepsRecoverableDraftsAndClearsPrivateReadLoss(HttpStatusCode failure, ResourceLoadStatus expected)
    {
        using var api = new Handler(); using var context = Context(api); var page = Render(context); page.WaitForElement("[data-testid=source-registry-identity-summary]"); await Select(page, "definition/identity"); page.WaitForElement("[data-testid=source-registry-display-name]").Input("Private draft"); api.Failure = failure; await Refresh(page);
        Assert.AreEqual(expected, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
        if (failure == HttpStatusCode.ServiceUnavailable) Assert.AreEqual("Private draft", page.Find("[data-testid=source-registry-display-name]").GetAttribute("value"));
        else { Assert.IsNull(page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource); Assert.IsEmpty(page.FindAll("[data-testid=source-registry-display-name]")); Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!()); }
    }
    [TestMethod]
    public async Task ChangedUidCannotReplaceTheViewedResourceAndHistoryFailureIsUnknown()
    {
        using var api = new Handler { DetailsFailure = true }; using var context = Context(api); var page = Render(context); page.WaitForElement("[data-testid=source-registry-identity-summary]"); Assert.AreEqual("—", page.FindAll(".metric-card strong")[2].TextContent);
        api.Resource = api.Resource with { Uid = Guid.NewGuid() }; api.DetailsFailure = false; await Refresh(page); Assert.IsNull(page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
    }
    [TestMethod]
    public async Task CreationWizardAllowsIncompleteNavigationAndFinalValidationReturnsToTheFirstRealField()
    {
        using var api = new Handler(); using var context = Context(api, create: true); var page = context.Render<SourceRegistries>(); page.WaitForElement("[data-wizard-step=refresh]").Click();
        Assert.AreEqual("false", page.Find("[data-wizard-step=identity]").GetAttribute("data-step-complete")); page.Find("[data-testid=source-registry-create]").Click(); page.WaitForAssertion(() => Assert.AreEqual("identity", page.FindComponent<CreationWizard>().Instance.CurrentStepId)); Assert.AreEqual(0, api.Creates);
        page.Find("[data-testid=source-registry-display-name]").Input("Created registry"); page.Find("[data-testid=source-registry-name]").Change("created-registry"); page.Find("[data-wizard-step=configuration]").Click(); page.Find("[data-testid=source-registry-index-url]").Input("https://registry.example/index.json"); page.Find("[data-wizard-step=refresh]").Click(); page.Find("[data-testid=source-registry-create]").Click(); page.WaitForAssertion(() => Assert.AreEqual(1, api.Creates)); Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty);
        StringAssert.Contains(context.Services.GetRequiredService<NavigationManager>().Uri, "/settings/source-registries/created-registry");
    }
    [TestMethod]
    public async Task OfficialRegistryRemainsEditableWithItsExistingDeleteRestrictionAndManagedAccessStaysIndependent()
    {
        using var api = new Handler(); api.Resource = api.Resource with { Metadata = api.Resource.Metadata with { Name = SourceRegistryWellKnown.OfficialName } }; using var context = Context(api, name: SourceRegistryWellKnown.OfficialName); var page = context.Render<SourceRegistries>(p => p.Add(value => value.Name, SourceRegistryWellKnown.OfficialName)); page.WaitForElement("[data-testid=source-registry-identity-summary]"); Assert.IsEmpty(page.FindAll("button.button-danger")); Assert.IsFalse(page.Find("[data-testid=source-registry-enabled]").HasAttribute("disabled"));
        api.Resource = api.Resource with { Governance = ResourceGovernance.Core("fixture") }; await Refresh(page); await Select(page, "definition/identity"); page.WaitForElement("[data-testid=source-registry-display-name]"); Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly); Assert.IsTrue(page.FindAll("[data-resource-section]").Any(value => value.GetAttribute("data-resource-section") == "access"));
    }
    private static IRenderedComponent<SourceRegistries> Render(BunitContext context) => context.Render<SourceRegistries>(p => p.Add(value => value.Name, "team"));
    private static Task Select(IRenderedComponent<SourceRegistries> page, string path) => page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection(path));
    private static Task Refresh(IRenderedComponent<SourceRegistries> page) => page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
    private static BunitContext Context(Handler api, bool create = false, string name = "team")
    {
        var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose; context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var provider = new ResourceListContextProvider { Permissions = new HashSet<string>(["resources/read", "resources/write", "resources/delete"]) }; context.Services.AddSingleton(new ConsoleContextState(provider)); context.Services.AddSingleton<NotificationState>(); context.Services.AddSingleton(TimeProvider.System);
        var http = new HttpClient(api) { BaseAddress = new("http://localhost/") }; context.Services.AddSingleton<ISourceRegistriesClient>(new SourceRegistriesApiClient(http)); context.Services.AddSingleton<ISecretsClient>(new SecretsApiClient(http));
        var navigation = context.Services.GetRequiredService<NavigationManager>(); navigation.NavigateTo("http://localhost/settings/source-registries/" + (create ? "new" : name));
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js"); module.Setup<string>("currentLocation", _ => true).SetResult(navigation.Uri);
        foreach (var path in new[] { "overview", "definition/identity", "definition/configuration", "definition/refresh", "origin", "history", "access", "yaml" }) { var url = navigation.Uri + "#" + path; module.Setup<string>("navigate", call => Equals(call.Arguments[0], url)).SetResult(url); }
        return context;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public SourceRegistryRegistrationResource Resource { get; set; } = new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = SourceRegistryKinds.SourceRegistryRegistration, Uid = Guid.NewGuid(), ScopeRef = ResourceScopeRef.Instance, Generation = 3, Metadata = new() { Name = "team", Tags = new Dictionary<string, string> { ["team"] = "reviewed" } }, Definition = new() { DisplayName = "Saved registry", IndexUrl = new("https://registry.example/index.json"), Enabled = true, AuthenticationMode = SourceRegistryAuthenticationMode.StaticBearer, Credential = new("registry-token", ResourceScopeRef.Instance, new ResourceNamespace("credentials")) } };
        public string Etag { get; set; } = "\"first\""; public HttpStatusCode? Failure { get; set; }
        public bool DetailsFailure { get; set; }
        public int Writes { get; private set; }
        public int Creates { get; private set; }
        public int HistoryBound { get; private set; }
        public string? IfMatch { get; private set; }
        public SourceRegistryRegistrationProperties? Command { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/secrets") return Ok(Array.Empty<SecretResponse>());
            if (path.EndsWith("/refreshes", StringComparison.Ordinal)) { HistoryBound = int.Parse(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["take"]!, System.Globalization.CultureInfo.InvariantCulture); return DetailsFailure ? Error(HttpStatusCode.ServiceUnavailable) : Ok(new SourceRegistryRefreshHistoryResponse([], 0)); }
            if (path.EndsWith("/trust", StringComparison.Ordinal)) return DetailsFailure ? Error(HttpStatusCode.ServiceUnavailable) : Ok(new SourceRegistryOriginTrustView(Resource.Uid, Resource.Name, Resource.Definition.IndexUrl, SourceRegistryOriginClassification.External, SourceRegistryTrustPolicy.Untrusted, "public_fixture", DateTimeOffset.UnixEpoch, null, null));
            if (request.Method == HttpMethod.Put) { ++Writes; IfMatch = request.Headers.IfMatch.Single().ToString(); Command = (await request.Content!.ReadFromJsonAsync<PutSourceRegistryRequest>(token))!.Properties; Resource = Resource with { Definition = Command, Generation = Resource.Generation + 1 }; Etag = "\"saved-" + Writes + "\""; }
            else if (request.Method == HttpMethod.Post) { ++Creates; var command = (await request.Content!.ReadFromJsonAsync<CreateSourceRegistryRequest>(token))!; Resource = Resource with { Metadata = Resource.Metadata with { Name = command.Name }, Definition = command.Properties }; }
            else if (Failure is { } failure) return Error(failure);
            var view = new SourceRegistryRegistrationView(Resource, new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = SourceRegistryKinds.SourceRegistryObservedState, ScopeRef = ResourceScopeRef.Instance, Metadata = new() { Name = Resource.Name }, Definition = new() { RegistrationUid = Resource.Uid } }); var response = Ok(view); response.Headers.TryAddWithoutValidation("ETag", Etag); return response;
        }
        private static HttpResponseMessage Ok(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        private static HttpResponseMessage Error(HttpStatusCode code) => new(code) { Content = JsonContent.Create(new { title = "public_fixture_failure" }) };
    }
}
