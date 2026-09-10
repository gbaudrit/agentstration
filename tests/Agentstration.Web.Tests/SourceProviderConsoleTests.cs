using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SourceProviderConsoleTests
{
    [TestMethod]
    public void CreationFromTenantExtensionPrefillsItsScopeAndSourceContribution()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISourceProvidersClient>(new StubSourceProvidersClient());
        context.Services.AddSingleton<IExtensionsClient>(new StubExtensionsClient());
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new StubScopeInventoryClient());
        context.Services.AddSingleton(new NotificationState());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"/sourceproviders/new?extension=tenant-extension&extensionNamespace=default&extensionScopeRef={Uri.EscapeDataString(StubScopeInventoryClient.TenantScope.Value)}&contributionId=git&displayName=Tenant%20Git");

        var rendered = context.Render<SourceProviderDetails>();

        rendered.WaitForAssertion(() =>
        {
            var selects = rendered.FindAll("select");
            Assert.HasCount(3, selects);
            Assert.AreEqual(StubScopeInventoryClient.TenantScope.Value, selects[0].GetAttribute("value"));
            StringAssert.Contains(selects[1].GetAttribute("value"), "tenant-extension");
            Assert.IsNotNull(selects[1].QuerySelector("option"));
            Assert.AreEqual("git", selects[2].GetAttribute("value"));
            Assert.IsFalse(rendered.Markup.Contains("instance-owned", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void ListShowsConfiguredProviderAndObservedStatus()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISourceProvidersClient>(new StubSourceProvidersClient(includeProvider: true));

        var rendered = context.Render<SourceProviders>();

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Git Source Provider");
            StringAssert.Contains(rendered.Markup, "Available");
            Assert.AreEqual("/sourceproviders/git-local?namespace=default&scopeRef=%2Finstance", rendered.Find("a.text-link").GetAttribute("href"));
        });
    }

    private sealed class StubSourceProvidersClient(bool includeProvider = false) : ISourceProvidersClient
    {
        public Task<IReadOnlyList<SourceProviderSummaryResponse>> GetSourceProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceProviderSummaryResponse>>(includeProvider
                ? [new("provider-id", "git-local", "Git Source Provider", "git-extension", "default", "git", "available", null, ScopeRef: ResourceScopeRef.Instance)]
                : []);
        public Task<ResourceSnapshot<SourceProviderResource>> GetSourceProviderAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SourceProviderResource>> CreateSourceProviderAsync(CreateSourceProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SourceProviderResource>> UpdateSourceProviderAsync(ResourceNamespace @namespace, string name, PutSourceProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSourceProviderAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SourceProviderStatusResponse> GetStatusAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SourceProviderUsagesResponse> GetUsagesAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubExtensionsClient : IExtensionsClient
    {
        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionResponse>>([
                Extension("git-extension", "Git Source Provider", ResourceScopeRef.Instance),
                Extension("tenant-extension", "Tenant Git", StubScopeInventoryClient.TenantScope)
            ]);
        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionRegistrationResource>>([
                Registration("git-extension", ResourceScopeRef.Instance),
                Registration("tenant-extension", StubScopeInventoryClient.TenantScope)
            ]);
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static ExtensionResponse Extension(string name, string displayName, ResourceScopeRef scopeRef) => new(
            name, "default", new("http://127.0.0.1:5290"), "available",
            new(name, displayName, "1.0.0", null), [new("source-provider", "git")], [], [], [], null, "configuration", RegistrationScopeRef: scopeRef);
        private static ExtensionRegistrationResource Registration(string name, ResourceScopeRef scopeRef) => new()
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.ExtensionRegistration,
            Metadata = new ResourceMetadata { Name = name },
            ScopeRef = scopeRef,
            Definition = new ExtensionRegistrationProperties { DisplayName = name, Endpoint = new("http://127.0.0.1:5290") }
        };
    }

    private sealed class StubScopeInventoryClient : IResourceScopeInventoryClient
    {
        public static readonly ResourceScopeRef TenantScope = ResourceScopeRef.Tenant(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([
                new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", true),
                new(TenantScope, ResourceScopeKind.Tenant, "Development", true)
            ]);
    }
}
