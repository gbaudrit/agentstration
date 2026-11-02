using Agentstration.Api.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;

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
        using var context = new BunitContext();
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
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolSetsClient>(new ToolSetsClientStub());
        context.Services.AddSingleton<IExtensionsClient>(new ExtensionsClientStub());

        var rendered = context.Render<ToolSetDetails>(parameters => parameters.Add(component => component.Name, ToolSetName));

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("[data-testid=extension-managed-resource]").TextContent, "Crawl4AI Web Acquisition");
            Assert.HasCount(0, rendered.FindAll(".page-header button.button-danger"));
        });
    }

    private static ResourceGovernance Governance() => ResourceGovernance.Extension(
        ScopedResourceAddress.Create(Scope, Namespace, ExtensionKinds.ExtensionBundleInstallation, RegistrationName).ToString(),
        ExtensionId,
        "9.0.0");

    [TestMethod]
    public void ManagedResourceDoesNotLinkToAResourceWithAnotherUid()
    {
        using var context = new BunitContext();
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
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new ToolSetsClientStub();
        context.Services.AddSingleton<IToolSetsClient>(client);
        context.Services.AddSingleton<IExtensionsClient>(new ExtensionsClientStub());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/tools/sets/{ToolSetName}?scopeRef=%2Finstance&context=%2Finstance");
        var rendered = context.Render<ToolSetDetails>(parameters => parameters
            .Add(component => component.Name, ToolSetName));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(Scope, client.RequestedScope);
            Assert.AreEqual(Scope, client.RequestedVersionsScope);
            Assert.IsEmpty(rendered.FindAll(".page-header button.button-danger"));
        });
    }

    [TestMethod]
    [DataRow("en-US", "Managed · read-only")]
    [DataRow("fr-FR", "Géré · lecture seule")]
    public void ProfileListShowsExtensionAndDistinctBundleVersion(string cultureName, string managedLabel)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = new BunitContext();
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

    private static DataSourceProfileResource Profile() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1, Kind = DataSourceResourceKinds.DataSourceProfile,
        Metadata = new ResourceMetadata { Name = "crawl4ai-web" }, ScopeRef = Scope, Uid = ToolSetUid,
        Governance = Governance(), Definition = new() { DisplayName = "Crawl4AI profile", Version = "3.0.0",
            AcquisitionFlow = new() { Name = "acquire", Version = "1.0.0" } }
    };

    private sealed class ProfilesClientStub : IDataSourceProfilesClient
    {
        public Task<IReadOnlyList<DataSourceProfileResource>> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DataSourceProfileResource>>([Profile()]);
        public Task<ResourceSnapshot<DataSourceProfileResource>> GetAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DataSourceProfileRevisionResource>> GetRevisionsAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

        public Task<ResourceSnapshot<ToolSetResource>> GetToolSetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceSnapshot<ToolSetResource>(ToolSet(), "\"etag-1\""));

        public Task<IReadOnlyList<ToolSetVersionResource>> GetVersionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolSetVersionResource>>([]);

        public Task<ToolSetVersionResource> GetVersionAsync(ResourceNamespace @namespace, string name, string version, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
