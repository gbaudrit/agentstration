using System.Net;
using System.Net.Http.Json;
using Agentstration.Api.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ExtensionManagedResourcePresentationTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Instance;
    private static readonly ResourceNamespace Namespace = ResourceNamespace.Default;
    private static readonly Guid ToolSetUid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string ExtensionId = "Agentstration.Extensions.Crawl4AI";
    private const string RegistrationName = "crawl4ai";
    private const string ToolSetName = "crawl4ai-web-acquisition";

    [TestMethod]
    [DataRow("en-US", "Bundle version")]
    [DataRow("fr-FR", "Version du bundle")]
    public void ManagedResourcePanelResolvesReadableExtensionAndReciprocalLink(string cultureName, string bundleVersionLabel)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new ExtensionsClientStub());

        var rendered = context.Render<ExtensionManagedResourcePanel>(parameters => parameters
            .Add(component => component.Governance, Governance())
            .Add(component => component.ScopeRef, Scope)
            .Add(component => component.Namespace, Namespace)
            .Add(component => component.Kind, ToolResourceKinds.ToolSet)
            .Add(component => component.Name, ToolSetName)
            .Add(component => component.Uid, ToolSetUid));

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("h2").TextContent, "Crawl4AI Web Acquisition");
            var link = rendered.Find("a.text-button");
            Assert.AreEqual(ExtensionId, link.TextContent);
            Assert.AreEqual("/extensions/crawl4ai?namespace=default&scopeRef=%2Finstance&context=%2Finstance", link.GetAttribute("href"));
            StringAssert.Contains(rendered.Markup, bundleVersionLabel);
            StringAssert.Contains(rendered.Markup, "9.0.0");
            StringAssert.Contains(rendered.Markup, "2.4.0");
        });
    }

    [TestMethod]
    public void ManagedToolSetShowsExtensionAndHidesDeleteAction()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolSetsClient>(new ToolSetsClientStub());
        context.Services.AddSingleton<IExtensionsClient>(new ExtensionsClientStub());

        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js")
            .Setup<string>("navigate", _ => true).SetResult(context.Services.GetRequiredService<NavigationManager>().Uri.Split('#')[0] + "#overview");
        var rendered = context.Render<ToolSetDetails>(parameters => parameters.Add(component => component.Name, ToolSetName));

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("[data-testid=extension-managed-resource]").TextContent, "Crawl4AI Web Acquisition");
            Assert.HasCount(0, rendered.FindAll(".resource-header button.button-danger"));
        });
    }

    [TestMethod]
    [DataRow("en-US", "Enabled")]
    [DataRow("fr-FR", "Activé")]
    public async Task ToolSetOverviewHasSavedIdentityAndRetainsItsExactResourceAcrossSections(string locale, string enabled)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        var client = new ToolSetsClientStub();
        client.Resource = client.Resource with { Definition = client.Resource.Definition with { Category = new("same-category", ResourceScopeRef.Instance, new("team-a")), Members = [new() { Tool = new("same-tool", ResourceScopeRef.Instance, new("team-b")), Capability = "search", Route = "search.documents" }] } };
        context.Services.AddSingleton<IToolSetsClient>(client);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "versions" }) module.Setup<string>("navigate", call => call.Arguments[0] is string url && url.EndsWith("#" + section, StringComparison.Ordinal)).SetResult("http://localhost/#" + section);
        var cut = context.Render<ToolSetDetails>(p => p.Add(c => c.Name, ToolSetName));
        Assert.Contains("Crawl4AI Web acquisition", cut.Find(".resource-identity-summary").TextContent);
        Assert.Contains("team-a/same-category", cut.Find(".resource-configuration-summary").TextContent);
        Assert.Contains("team-b/same-tool", cut.Find(".resource-configuration-summary").TextContent);
        Assert.Contains("search.documents", cut.Find(".resource-configuration-summary").TextContent);
        Assert.Contains(enabled, cut.Find(".resource-header .resource-enabled-toggle").TextContent);
        Assert.IsTrue(cut.Find(".resource-header input[role=switch]").HasAttribute("disabled"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("versions"));
        Assert.AreEqual("versions", cut.FindComponent<ResourceNavigation>().Instance.CurrentPath);
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("overview"));
        Assert.AreEqual(1, client.Reads);
        Assert.IsEmpty(cut.FindAll(".resource-header button.button-danger"));
    }

    [TestMethod]
    public void ToolSetResponseFromAnotherOwnerFailsClosed()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolSetsClient>(new ToolSetsClientStub()); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var requested = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/tools/sets/{ToolSetName}?scopeRef={Uri.EscapeDataString(requested.Value)}");
        var cut = context.Render<ToolSetDetails>(p => p.Add(c => c.Name, ToolSetName));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
        Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
        Assert.IsEmpty(cut.FindAll(".resource-form, .resource-identity-summary"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ToolSetUsesStandardReadOnlyIdentityConfigurationAndYamlWithIndependentAccess(bool managed)
    {
        var client = new ToolSetsClientStub(); if (!managed) client.Resource = client.Resource with { Governance = ResourceGovernance.User };
        client.Resource = client.Resource with { Metadata = client.Resource.Metadata with { Tags = new Dictionary<string, string> { ["environment"] = "fixture" } } };
        using var context = ToolSetContext(client); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = context.Render<ToolSetDetails>(p => p.Add(c => c.Name, ToolSetName));
        Assert.Contains("environment: fixture", cut.Find(".resource-identity-summary .labels-summary").TextContent);
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance; var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.AreSame(adapter.SectionState, form.SectionState); Assert.AreSame(adapter.SectionPresentations, form.Sections);
        Assert.IsTrue(form.ReadOnly); Assert.IsTrue(cut.FindComponent<ResourceIdentityEditor>().Instance.ReadOnly);
        Assert.AreEqual("environment=fixture", cut.FindComponent<LabelsEditor>().Instance.Value);
        var original = form.EditContext;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        await cut.InvokeAsync(() => navigation.NavigateSection("yaml"));
        Assert.IsNotNull(cut.Find(".resource-yaml-editor textarea[readonly]")); Assert.Contains(ToolSetName, cut.Find(".resource-yaml-editor textarea").GetAttribute("value")!);
        await cut.InvokeAsync(() => navigation.NavigateSection("access"));
        Assert.AreEqual(client.Resource.Uid, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance.Parameters.Get(x => x.ResourceUid));
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        Assert.AreSame(original, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar")); Assert.AreEqual(1, client.Reads);
    }

    [TestMethod]
    public async Task ToolSetRefreshPreservesPolicyDraftsAndFailedRefreshRetainsSavedContent()
    {
        var client = new ToolSetsClientStub(); using var context = ToolSetContext(client); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = context.Render<ToolSetDetails>(p => p.Add(c => c.Name, ToolSetName));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("access"));
        var policy = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        await cut.InvokeAsync(() => policy.Parameters.Get(x => x.DraftChanged).InvokeAsync(true));
        client.Resource = client.Resource with { Generation = 2, Definition = client.Resource.Definition with { DisplayName = "New saved name" } }; client.ReadEtag = "\"newer\"";
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.AreEqual(2L, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance.Parameters.Get(x => x.Generation));
        client.ReadError = new AgentstrationApiException("Unavailable", "fixture", HttpStatusCode.ServiceUnavailable);
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.AreEqual(ResourceLoadStatus.Failed, adapter.LoadState!.Status); Assert.IsTrue(adapter.LoadState.HasContent);
        Assert.AreEqual("New saved name", adapter.Name); Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        client.ReadError = null; await cut.InvokeAsync(() => adapter.OnRetry.InvokeAsync());
        Assert.AreEqual(ResourceLoadStatus.Ready, cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ToolSetRefreshClearsPrivateContentOnLostAccessOrReplacement(bool replace)
    {
        var client = new ToolSetsClientStub(); using var context = ToolSetContext(client); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = context.Render<ToolSetDetails>(p => p.Add(c => c.Name, ToolSetName));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("access"));
        await cut.InvokeAsync(() => cut.FindComponent<Stub<ResourceAccessPanel>>().Instance.Parameters.Get(x => x.DraftChanged).InvokeAsync(true));
        if (replace) client.Resource = client.Resource with { Uid = Guid.NewGuid() };
        else client.ReadError = new AgentstrationApiException("Denied", "fixture", HttpStatusCode.Forbidden);
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
        Assert.IsEmpty(cut.FindAll(".resource-form, .resource-identity-summary, .resource-yaml-editor"));
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task ToolSetConfirmedDeletionReleasesItsPendingPolicyDraft()
    {
        var client = new ToolSetsClientStub(); client.Resource = client.Resource with { Governance = ResourceGovernance.User, ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) };
        using var context = ToolSetContext(client); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = context.Render<ToolSetDetails>(p => p.Add(c => c.Name, ToolSetName));
        await ResourceScreenDeletionAssertions.ConfirmAndReleasePolicyDraftAsync(cut, context.Services.GetRequiredService<NavigationManager>());
        Assert.AreEqual(1, client.Deletes); Assert.AreEqual("/tools/sets", new Uri(context.Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
    }

    [TestMethod]
    public async Task ToolSetRejectedDeletionRetainsItsSavedIdentityAndPolicyDraft()
    {
        var client = new ToolSetsClientStub(); client.Resource = client.Resource with { Governance = ResourceGovernance.User };
        client.DeleteError = new AgentstrationApiException("Existing deletion guard rejected the operation.", "fixture", HttpStatusCode.UnprocessableEntity);
        using var context = ToolSetContext(client); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = context.Render<ToolSetDetails>(p => p.Add(c => c.Name, ToolSetName));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("access"));
        await cut.InvokeAsync(() => cut.FindComponent<Stub<ResourceAccessPanel>>().Instance.Parameters.Get(x => x.DraftChanged).InvokeAsync(true));
        var snapshot = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource;
        cut.Find(".resource-header button.button-danger").Click(); await cut.Find("[role=alertdialog] .button-danger").ClickAsync(new());
        Assert.AreSame(snapshot, cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.Contains("Existing deletion guard rejected", cut.Find(".form-alert-danger").TextContent);
        Assert.AreEqual($"/tools/sets/{ToolSetName}", new Uri(context.Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
    }

    private static BunitContext ToolSetContext(ToolSetsClientStub client)
    {
        var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources"); context.Services.AddSingleton<IToolSetsClient>(client);
        var uri = $"http://localhost/tools/sets/{ToolSetName}?scopeRef={Uri.EscapeDataString(client.Resource.ScopeRef!.Value.Value)}";
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "definition/identity", "definition/configuration", "versions", "access", "yaml" })
            module.Setup<string>("navigate", c => c.Arguments[0] is string value && value.EndsWith("#" + section, StringComparison.Ordinal)).SetResult(uri + "#" + section);
        return context;
    }

    private static ResourceGovernance Governance() => ResourceGovernance.Extension(
        ScopedResourceAddress.Create(Scope, Namespace, ExtensionKinds.ExtensionBundleInstallation, RegistrationName).ToString(),
        ExtensionId,
        "9.0.0");

    [TestMethod]
    public void ManagedResourceDoesNotLinkToAResourceWithAnotherUid()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new ExtensionsClientStub());
        var rendered = context.Render<ExtensionManagedResourcePanel>(parameters => parameters
            .Add(component => component.Governance, Governance()).Add(component => component.ScopeRef, Scope)
            .Add(component => component.Namespace, Namespace).Add(component => component.Kind, ToolResourceKinds.ToolSet)
            .Add(component => component.Name, ToolSetName).Add(component => component.Uid, Guid.NewGuid()));
        Assert.IsEmpty(rendered.FindAll("a"));
    }

    [TestMethod]
    public void ToolSetDetailsRequestsTheExactScopeForTheResourceAndVersions()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new ToolSetsClientStub();
        context.Services.AddSingleton<IToolSetsClient>(client);
        context.Services.AddSingleton<IExtensionsClient>(new ExtensionsClientStub());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/tools/sets/{ToolSetName}?scopeRef=%2Finstance&context=%2Finstance");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js")
            .Setup<string>("navigate", _ => true).SetResult(context.Services.GetRequiredService<NavigationManager>().Uri.Split('#')[0] + "#overview");
        var rendered = context.Render<ToolSetDetails>(parameters => parameters
            .Add(component => component.Name, ToolSetName));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(Scope, client.RequestedScope);
            Assert.AreEqual(Scope, client.RequestedVersionsScope);
            Assert.IsEmpty(rendered.FindAll(".resource-header button.button-danger"));
        });
    }

    [TestMethod]
    [DataRow("en-US", "Managed / read-only")]
    [DataRow("fr-FR", "Géré / lecture seule")]
    public void ProfileListShowsExtensionAndDistinctBundleVersion(string cultureName, string managedLabel)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IDataSourceProfilesClient>(new ProfilesClientStub());
        context.Services.AddSingleton<IExtensionsClient>(new ExtensionsClientStub());
        var rendered = context.Render<DataSourceProfiles>();
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Crawl4AI Web Acquisition");
            StringAssert.Contains(rendered.Markup, "9.0.0");
            StringAssert.Contains(rendered.Markup, "2.4.0");
            StringAssert.Contains(rendered.Markup, managedLabel);
            Assert.IsNotNull(rendered.Find("a[href='/extensions/crawl4ai?namespace=default&scopeRef=%2Finstance&context=%2Finstance']"));
        });
    }

    [TestMethod]
    [DataRow("en-US", "Access")]
    [DataRow("fr-FR", "Accès")]
    public async Task ManagedProfileKeepsAccessAdministrationAvailable(string cultureName, string accessLabel)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IDataSourceProfilesClient>(new ProfilesClientStub());
        context.Services.AddSingleton<IExtensionsClient>(new ExtensionsClientStub());
        using var http = new HttpClient(new EmptyFlowsHandler()) { BaseAddress = new Uri("http://localhost") };
        context.Services.AddSingleton<IFlowApiClient>(new FlowApiClient(http));
        var policies = new ProfilePolicies();
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        context.Services.AddSingleton<IResourceAccessPolicyClient>(policies);
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new ProfileScopes(tenant));
        context.Services.AddSingleton<IConsoleNavigationContext>(new ProfileNavigationContext(Scope.Value));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/data-source-profiles/crawl4ai-web?scopeRef=%2Finstance&context=%2Finstance");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var path in new[] { "overview", "definition/identity", "access" })
            module.Setup<string>("navigate", i => i.Arguments[0] is string url && url.EndsWith("#" + path, StringComparison.Ordinal)).SetResult("http://localhost/data-source-profiles/crawl4ai-web?scopeRef=%2Finstance&context=%2Finstance#" + path);
        var rendered = context.Render<DataSourceProfileEditor>(parameters => parameters.Add(component => component.Name, "crawl4ai-web"));
        rendered.WaitForAssertion(() => Assert.IsEmpty(rendered.FindAll("button.button-danger")));
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("access"));
        Assert.AreEqual(accessLabel, rendered.Find("a[data-resource-section=access]").TextContent.Trim());
        rendered.WaitForElement("[data-testid=resource-access-target]").Change(tenant.Value);
        rendered.Find("[data-testid=resource-access-add]").Click();
        rendered.Find("[data-testid=resource-access-save]").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(policies.Saved);
            Assert.AreEqual(ToolSetUid, policies.Saved.Resource.Uid);
            Assert.IsNull(policies.Saved.Resource.Version);
            Assert.AreEqual(tenant, policies.Saved.Grants.Single().TargetScopeRef);
        });
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("overview"));
        Assert.IsNotNull(rendered.Find("a[data-resource-section='definition/identity']"));
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        Assert.IsTrue(rendered.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
        Assert.IsTrue(rendered.Find("form > fieldset").HasAttribute("disabled"));
        Assert.IsEmpty(rendered.FindAll("button[type=submit]"));
    }

    private sealed class ProfileNavigationContext(string scopeRef) : IConsoleNavigationContext
    {
        public string? ScopeRef => scopeRef;
        public string AddToUri(string uri) => uri;
    }

    private sealed class EmptyFlowsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { value = Array.Empty<object>() }) });
    }

    private sealed class ProfilePolicies : IResourceAccessPolicyClient
    {
        public Task<ResourceAccessDecision> EvaluateAsync(EvaluateResourceAccessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceAccessDecision(false, ResourceAccessDecisionReasons.GrantMissing));
        public ResourceAccessPolicy? Saved { get; private set; }
        public Task<ResourceAccessPolicySnapshot?> GetAsync(Guid resourceUid, CancellationToken cancellationToken) => Task.FromResult<ResourceAccessPolicySnapshot?>(null);
        public Task<ResourceAccessPolicySnapshot> PutAsync(ResourceAccessPolicy policy, string? etag, CancellationToken cancellationToken)
        {
            Saved = policy;
            return Task.FromResult(new ResourceAccessPolicySnapshot(policy, "policy"));
        }
    }

    private sealed class ProfileScopes(ResourceScopeRef tenant) : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => Task.FromResult(
            new ResourceScopeInventoryResponse([
                new(Scope, ResourceScopeKind.Instance, "Instance", null, false,
                    [new(ToolSetUid, Namespace, DataSourceResourceKinds.DataSourceProfile, "crawl4ai-web", DateTimeOffset.UnixEpoch)]),
                new(tenant, ResourceScopeKind.Tenant, "Tenant", Scope, true, [])
            ]));
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(Scope, ResourceScopeKind.Instance, "Instance", true)]);
    }

    private static DataSourceProfileResource Profile() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = DataSourceResourceKinds.DataSourceProfile,
        Metadata = new ResourceMetadata { Name = "crawl4ai-web" },
        ScopeRef = Scope,
        Uid = ToolSetUid,
        Governance = Governance(),
        Definition = new()
        {
            DisplayName = "Crawl4AI profile",
            Version = "3.0.0",
            AcquisitionFlow = new() { Name = "acquire", Version = "1.0.0" }
        }
    };

    private sealed class ProfilesClientStub : IDataSourceProfilesClient
    {
        public Task<IReadOnlyList<DataSourceProfileResource>> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DataSourceProfileResource>>([Profile()]);
        public Task<ResourceSnapshot<DataSourceProfileResource>> GetAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => Task.FromResult(new ResourceSnapshot<DataSourceProfileResource>(Profile(), "profile-etag"));
        public Task<IReadOnlyList<DataSourceProfileRevisionResource>> GetRevisionsAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DataSourceProfileRevisionResource>>([]);
        public Task<ResourceSnapshot<DataSourceProfileResource>> CreateAsync(CreateDataSourceProfileRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceProfileResource>> UpdateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, PutDataSourceProfileRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceProfileRevisionResource>> PublishAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, PublishDataSourceProfileRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceProfileResource>> ActivateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, ActivateDataSourceProfileRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static ToolSetResource ToolSet() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.ToolSet,
        Metadata = new ResourceMetadata { Name = ToolSetName },
        ScopeRef = Scope,
        Uid = ToolSetUid,
        Governance = Governance(),
        Definition = new ToolSetProperties
        {
            DisplayName = "Crawl4AI Web acquisition",
            Version = "1.0.0",
            Publish = true,
            Members = []
        }
    };

    private sealed class ToolSetsClientStub : IToolSetsClient
    {
        public ToolSetResource Resource { get; set; } = ToolSet();
        public int Reads { get; private set; }
        public string ReadEtag { get; set; } = "\"etag-1\"";
        public Exception? ReadError { get; set; }
        public int Deletes { get; private set; }
        public Exception? DeleteError { get; set; }
        public ResourceScopeRef? RequestedScope { get; private set; }
        public ResourceScopeRef? RequestedVersionsScope { get; private set; }
        public Task<ResourceSnapshot<ToolSetResource>> GetToolSetAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default)
        {
            RequestedScope = scopeRef;
            return GetToolSetAsync(@namespace, name, cancellationToken);
        }
        public Task<IReadOnlyList<ToolSetVersionResource>> GetVersionsAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default)
        {
            RequestedVersionsScope = scopeRef;
            return GetVersionsAsync(@namespace, name, cancellationToken);
        }
        public Task<IReadOnlyList<ToolSetResource>> GetToolSetsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolSetResource>>([ToolSet()]);

        public Task<ResourceSnapshot<ToolSetResource>> GetToolSetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default)
        {
            Reads++;
            return ReadError is { } error ? Task.FromException<ResourceSnapshot<ToolSetResource>>(error) : Task.FromResult(new ResourceSnapshot<ToolSetResource>(Resource, ReadEtag));
        }

        public Task<IReadOnlyList<ToolSetVersionResource>> GetVersionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolSetVersionResource>>([]);

        public Task<ToolSetVersionResource> GetVersionAsync(ResourceNamespace @namespace, string name, string version, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(Resource.ScopeRef, scopeRef); return DeleteAsync(@namespace, name, etag, cancellationToken);
        }
        public Task DeleteAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(Resource.Namespace, @namespace); Assert.AreEqual(Resource.Name, name); Assert.AreEqual(ReadEtag, etag); Deletes++; if (DeleteError is { } error) return Task.FromException(error); ReadError = new AgentstrationApiException("Deleted", "fixture", HttpStatusCode.NotFound); return Task.CompletedTask;
        }
    }

    private sealed class ExtensionsClientStub : IExtensionsClient
    {
        public Task<IReadOnlyList<ExtensionInventoryItemResponse>> GetExtensionInventoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionInventoryItemResponse>>([Inventory()]);

        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionResponse>>([Extension()]);

        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionRegistrationResource>>([]);

        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static ExtensionInventoryItemResponse Inventory() => new(
            "registration:default/crawl4ai",
            RegistrationName,
            Namespace.Value,
            Scope,
            null,
            "Crawl4AI Web Acquisition",
            ExtensionId,
            "1.0.0",
            new Uri("http://127.0.0.1:5290"),
            "aep",
            true,
            "available",
            null,
            null,
            Extension(),
            []);

        private static ExtensionResponse Extension() => new(
            RegistrationName,
            Namespace.Value,
            new Uri("http://127.0.0.1:5290"),
            "available",
            new(ExtensionId, "Crawl4AI Web Acquisition", "1.0.0", null),
            [],
            [],
            [],
            [],
            null,
            "aep",
            RegistrationScopeRef: Scope,
            BundleInstallations:
            [
                new(
                    "crawl4ai",
                    Scope,
                    ExtensionId,
                    "9.0.0",
                    "crawl4ai-web",
                    "2.4.0",
                    "digest",
                    ExtensionBundleInstallationState.Installed,
                    new Dictionary<string, ResourceReference>(),
                    [new(ToolResourceKinds.ToolSet, Namespace.Value, ToolSetName, Scope, ToolSetUid, 1, "1.0.0", "hash", false),
                     new(DataSourceResourceKinds.DataSourceProfile, Namespace.Value, "crawl4ai-web", Scope, ToolSetUid, 1, "3.0.0", "profile-hash", false)],
                    null)
            ]);
    }
}
