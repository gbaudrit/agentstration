using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DataSourceProfileScreenTests
{
    [TestMethod]
    [DataRow("en-US", "Identity")]
    [DataRow("fr-FR", "Identité")]
    public async Task SavedOverviewAndExactPinnedFlowSurviveDefinitionEdits(string culture, string identity)
    {
        using var locale = new TestCultureScope(culture);
        var client = new Client();
        using var context = Context(client);
        var cut = context.Render<DataSourceProfileEditor>(p => p.Add(c => c.Name, "profile"));
        Assert.Contains(identity, cut.Find(".resource-identity-summary").TextContent);
        Assert.Contains("special/acquire", cut.Find(".resource-configuration-summary").TextContent);
        Assert.Contains("2.3.4", cut.Find(".resource-configuration-summary").TextContent);
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        var form = cut.FindComponent<EditForm>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        cut.Find("[data-testid=profile-display-name]").Input("Changed draft");
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        Assert.DoesNotContain("Changed draft", cut.Find(".resource-identity-summary").TextContent);
        Assert.AreSame(form, cut.FindComponent<EditForm>().Instance);
        Assert.AreEqual(1, client.Reads);
        await cut.Find("#profile-resource-form").SubmitAsync();
        Assert.AreEqual(1, client.Updates);
        Assert.AreEqual("initial-etag", client.LastEtag);
        Assert.AreEqual(client.InitialFlow, client.Saved.Definition.AcquisitionFlow);
        Assert.AreEqual(client.InitialDiscovery, client.Saved.Definition.DiscoveryFlow);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task InvalidJsonIsRetainedAcrossNavigationAndBlocksUpdatesAndPublication()
    {
        var client = new Client(); using var context = Context(client);
        var cut = context.Render<DataSourceProfileEditor>(p => p.Add(c => c.Name, "profile"));
        var nav = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => nav.NavigateSection("definition/contracts"));
        cut.Find("[data-testid=profile-toolBindingsJson]").Change("{");
        await cut.InvokeAsync(() => nav.NavigateSection("revisions"));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsTrue(cut.Find("[data-testid=profile-publish-version]").Closest("fieldset")!.HasAttribute("disabled"));
        await cut.Find("#profile-resource-form").SubmitAsync();
        Assert.AreEqual(0, client.Updates);
        Assert.Contains("#definition/contracts", context.Services.GetRequiredService<NavigationManager>().Uri);
        Assert.HasCount(1, cut.FindAll(".validation-message"));
        Assert.AreEqual("{", cut.Find("[data-testid=profile-toolBindingsJson]").GetAttribute("value"));
        cut.Find(".unsaved-changes-bar .button-secondary").Click();
        Assert.AreEqual("[]", cut.Find("[data-testid=profile-toolBindingsJson]").GetAttribute("value"));
        Assert.IsEmpty(cut.FindAll(".validation-message"));
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task HeaderActivationRemainsAnEtagProtectedDraftAndOverviewRemainsSaved()
    {
        var client = new Client(); using var context = Context(client);
        var cut = context.Render<DataSourceProfileEditor>(p => p.Add(c => c.Name, "profile"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceActivationToggle>().Instance.EnabledChanged.InvokeAsync(false));
        Assert.AreEqual(0, client.Updates);
        Assert.IsTrue(client.Saved.Definition.Enabled);
        Assert.IsNotNull(cut.Find(".unsaved-changes-bar"));
        await cut.Find("#profile-resource-form").SubmitAsync();
        Assert.AreEqual(1, client.Updates);
        Assert.IsFalse(client.Saved.Definition.Enabled);
        Assert.AreEqual("initial-etag", client.LastEtag);
    }

    [TestMethod]
    public async Task WizardAllowsIncompleteNavigationButOnlyCreatesAndPublishesAfterValidation()
    {
        var client = new Client(); using var context = Context(client, true);
        var cut = context.Render<DataSourceProfileEditor>();
        var wizard = cut.FindComponent<CreationWizard>().Instance;
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("identity", wizard.CurrentStepId);
        Assert.AreEqual(0, client.Creates);
        cut.Find("[data-testid=profile-display-name]").Input("New profile");
        cut.Find("[data-testid=profile-name]").Input("new-profile");
        await cut.InvokeAsync(() => cut.FindComponent<ResourceScopeFormField>().Instance.ValueChanged.InvokeAsync(ResourceScopeRef.Instance));
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("acquisition", wizard.CurrentStepId);
        Assert.AreEqual(0, client.Creates);
        cut.Find("[data-testid=profile-flow]").Change("/instance|default|acquire");
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual(1, client.Creates);
        Assert.AreEqual(1, client.Publications);
        Assert.AreEqual("1.0.0", client.PublishedVersion);
        Assert.AreEqual(ResourceScopeRef.Instance, client.Saved.Definition.AcquisitionFlow.ScopeRef);
        Assert.IsTrue(client.Saved.Definition.AcquisitionFlow.UseActiveVersion);
        Assert.Contains("/data-source-profiles/new-profile?", context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [TestMethod]
    public async Task UnconfirmedPublicationNavigatesToCreatedResourceForRetryWithoutCreatingAgain()
    {
        var client = new Client { FailPublication = true }; using var context = Context(client, true);
        var cut = context.Render<DataSourceProfileEditor>();
        cut.Find("[data-testid=profile-display-name]").Input("New profile");
        cut.Find("[data-testid=profile-name]").Input("new-profile");
        await cut.InvokeAsync(() => cut.FindComponent<ResourceScopeFormField>().Instance.ValueChanged.InvokeAsync(ResourceScopeRef.Instance));
        cut.Find("[data-testid=profile-flow]").Change("/instance|default|acquire");
        var wizard = cut.FindComponent<CreationWizard>().Instance;
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual(1, client.Creates);
        Assert.AreEqual(1, client.Publications);
        Assert.Contains("publicationPending=true#revisions", context.Services.GetRequiredService<NavigationManager>().Uri);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task RawYamlDraftSurvivesNavigationAndInvalidIdentityCannotWrite()
    {
        var client = new Client(); using var context = Context(client);
        var cut = context.Render<DataSourceProfileEditor>(p => p.Add(c => c.Name, "profile"));
        var nav = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => nav.NavigateSection("yaml"));
        var original = cut.Find("[data-testid=profile-yaml]").GetAttribute("value")!;
        var invalid = original.Replace("name: profile", "name: other", StringComparison.Ordinal);
        cut.Find("[data-testid=profile-yaml]").Change(invalid);
        await cut.InvokeAsync(() => nav.NavigateSection("definition/configuration"));
        Assert.IsTrue(cut.Find("#profile-resource-form fieldset").HasAttribute("disabled"));
        await cut.InvokeAsync(() => nav.NavigateSection("yaml"));
        Assert.AreEqual(invalid, cut.Find("[data-testid=profile-yaml]").GetAttribute("value"));
        cut.Find(".unsaved-changes-bar .button-primary").Click();
        Assert.AreEqual(0, client.Updates);
        Assert.IsNotNull(cut.Find("[role=alert]"));
        Assert.IsNotNull(cut.Find(".unsaved-changes-bar"));
        cut.Find(".unsaved-changes-bar .button-secondary").Click();
        Assert.AreEqual(original, cut.Find("[data-testid=profile-yaml]").GetAttribute("value"));
    }

    private static BunitContext Context(Client client, bool create = false)
    {
        var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<IDataSourceProfilesClient>(client);
        context.Services.AddSingleton<IFlowApiClient>(new FlowApiClient(new HttpClient(new FlowsHandler()) { BaseAddress = new("http://localhost") }));
        var uri = create ? "http://localhost/data-source-profiles/new" : "http://localhost/data-source-profiles/profile?namespace=default&scopeRef=%2Finstance";
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var path in new[] { "overview", "definition/configuration", "definition/acquisition", "definition/contracts", "definition/policies", "revisions", "yaml" })
            module.Setup<string>("navigate", i => i.Arguments[0] is string url && url.EndsWith("#" + path, StringComparison.Ordinal)).SetResult(uri + "#" + path);
        return context;
    }

    private sealed class FlowsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new FlowPageResponse([new("acquire", "Acquire", null, FlowKind.Workflow, "1.0.0", true, "1.0.0", DateTimeOffset.UnixEpoch) { ScopeRef = ResourceScopeRef.Instance }], null))
        });
    }

    private sealed class Client : IDataSourceProfilesClient
    {
        public DataSourceFlowTarget InitialFlow { get; } = new() { Name = "acquire", Namespace = new("special"), Version = "2.3.4", UseActiveVersion = false };
        public DataSourceFlowTarget InitialDiscovery { get; } = new() { Name = "discover", ScopeRef = ResourceScopeRef.Instance, Version = "1.2.3", UseActiveVersion = false };
        public DataSourceProfileResource Saved { get; private set; }
        public Client() => Saved = new() { Uid = Guid.NewGuid(), ApiVersion = ResourceApiVersions.CoreV1, Kind = DataSourceResourceKinds.DataSourceProfile, Metadata = new() { Name = "profile" }, ScopeRef = ResourceScopeRef.Instance, Definition = new() { DisplayName = "Saved profile", AcquisitionFlow = InitialFlow, DiscoveryFlow = InitialDiscovery } };
        public bool FailPublication { get; init; }
        public int Reads { get; private set; }
        public int Creates { get; private set; }
        public int Updates { get; private set; }
        public int Publications { get; private set; }
        public string? LastEtag { get; private set; }
        public string? PublishedVersion { get; private set; }
        public Task<IReadOnlyList<DataSourceProfileResource>> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DataSourceProfileResource>>([Saved]);
        public Task<ResourceSnapshot<DataSourceProfileResource>> GetAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) { Reads++; return Task.FromResult(new ResourceSnapshot<DataSourceProfileResource>(Saved, "initial-etag")); }
        public Task<IReadOnlyList<DataSourceProfileRevisionResource>> GetRevisionsAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DataSourceProfileRevisionResource>>([]);
        public Task<ResourceSnapshot<DataSourceProfileResource>> CreateAsync(CreateDataSourceProfileRequest request, CancellationToken cancellationToken = default)
        {
            Creates++; Saved = Saved with { Metadata = new() { Name = request.Name, Namespace = ResourceNamespace.Parse(request.Namespace) }, ScopeRef = request.ScopeRef, Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<DataSourceProfileResource>(Saved, "created-etag"));
        }
        public Task<ResourceSnapshot<DataSourceProfileResource>> UpdateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, PutDataSourceProfileRequest request, string etag, CancellationToken cancellationToken = default)
        {
            Updates++; LastEtag = etag; Assert.AreEqual(Saved.Name, name); Assert.AreEqual(Saved.Namespace, ns); Assert.AreEqual(Saved.ScopeRef, scope); Saved = Saved with { Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<DataSourceProfileResource>(Saved, "updated-etag"));
        }
        public Task<ResourceSnapshot<DataSourceProfileRevisionResource>> PublishAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, PublishDataSourceProfileRequest request, CancellationToken cancellationToken = default)
        {
            Publications++; PublishedVersion = request.Version;
            return FailPublication ? Task.FromException<ResourceSnapshot<DataSourceProfileRevisionResource>>(new HttpRequestException("Publication interrupted")) : Task.FromResult(new ResourceSnapshot<DataSourceProfileRevisionResource>(null!, "revision-etag"));
        }
        public Task<ResourceSnapshot<DataSourceProfileResource>> ActivateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, ActivateDataSourceProfileRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
