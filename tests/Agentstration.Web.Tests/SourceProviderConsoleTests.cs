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
    public void CreationFromExtensionPrefillsOnlyTheInstanceSourceContribution()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISourceProvidersClient>(new StubSourceProvidersClient());
        context.Services.AddSingleton<IExtensionsClient>(new StubExtensionsClient());
        context.Services.AddSingleton(new NotificationState());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/sourceproviders/new?extension=git-extension&extensionNamespace=default&contributionId=git&displayName=Git%20Source%20Provider");

        var rendered = context.Render<SourceProviderDetails>();

        rendered.WaitForAssertion(() =>
        {
            var selects = rendered.FindAll("select");
            Assert.HasCount(2, selects);
            Assert.AreEqual("default:git-extension", selects[0].GetAttribute("value"));
            Assert.IsNotNull(selects[0].QuerySelector("option[value='default:git-extension']"));
            Assert.IsNull(selects[0].QuerySelector("option[value='default:tenant-extension']"));
            Assert.AreEqual("git", selects[1].GetAttribute("value"));
            StringAssert.Contains(rendered.Markup, "Source Providers are instance-owned");
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
            Assert.AreEqual("/sourceproviders/git-local?namespace=default", rendered.Find("a.text-link").GetAttribute("href"));
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
                Extension("git-extension", "Git Source Provider"),
                Extension("tenant-extension", "Tenant Git")
            ]);
        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionRegistrationResource>>([
                Registration("git-extension", ResourceScopeRef.Instance),
                Registration("tenant-extension", ResourceScopeRef.Tenant(Guid.NewGuid()))
            ]);
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static ExtensionResponse Extension(string name, string displayName) => new(
            name, "default", new("http://127.0.0.1:5290"), "available",
            new(name, displayName, "1.0.0", null), [new("source-provider", "git")], [], [], [], null, "configuration");
        private static ExtensionRegistrationResource Registration(string name, ResourceScopeRef scopeRef) => new()
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.ExtensionRegistration,
            Metadata = new ResourceMetadata { Name = name },
            ScopeRef = scopeRef,
            Definition = new ExtensionRegistrationProperties { DisplayName = name, Endpoint = new("http://127.0.0.1:5290") }
        };
    }
}
