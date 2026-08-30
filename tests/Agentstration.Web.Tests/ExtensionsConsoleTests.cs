using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ExtensionsConsoleTests
{
    [TestMethod]
    public void ConfiguredModelProviderContributionKeepsAnAccessibleAction()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: true));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());

        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();

        rendered.WaitForAssertion(() =>
        {
            var action = rendered.Find(".panel-actions a.button-secondary");
            Assert.AreEqual("Open llama-cpp-local provider", action.TextContent.Trim());
            Assert.AreEqual("/modelproviders/llama-cpp-local?namespace=default", action.GetAttribute("href"));
        });
    }

    [TestMethod]
    public void DiscoveredModelProviderContributionOffersConfiguration()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: false));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());

        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();

        rendered.WaitForAssertion(() =>
        {
            var action = rendered.Find(".panel-actions a.button-primary");
            Assert.AreEqual("Configure llama.cpp provider", action.TextContent.Trim());
            StringAssert.StartsWith(action.GetAttribute("href"), "/modelproviders/new?");
        });
    }

    [TestMethod]
    public async Task DiscoverButtonInvokesDiscoveryAndShowsResult()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();

        rendered.WaitForAssertion(() => Assert.AreEqual("Discover extensions", rendered.Find("button.button-secondary").TextContent.Trim()));
        await rendered.Find("button.button-secondary").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, client.DiscoveryCalls);
            StringAssert.Contains(rendered.Find(".inline-info").TextContent, "1 source(s)");
        });
    }

    private sealed class FakeExtensionsClient(bool configured) : IExtensionsClient
    {
        public int DiscoveryCalls { get; private set; }

        public Task<ExtensionDiscoveryResponse> DiscoverAsync(CancellationToken cancellationToken)
        {
            DiscoveryCalls++;
            return Task.FromResult(new ExtensionDiscoveryResponse(1, 1, 0, 0));
        }

        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionResponse>>([
                new(
                    "llama-cpp-local",
                    "default",
                    new Uri("http://localhost:5270/"),
                    "available",
                    new ExtensionIdentityResponse("Agentstration.Extensions.LlamaCpp", "llama.cpp", "1.0.0", "Local provider"),
                    [new ExtensionContributionResponse("model-provider", "llama.cpp")],
                    [],
                    [],
                    configured ? [new ExtensionProviderBindingResponse("llama-cpp-local", "default", "llama.cpp")] : [],
                    null,
                    "configuration")
            ]);

        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionRegistrationResource>>([]);

        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeModelProfilesClient : IModelProfilesClient
    {
        public Task<IReadOnlyList<ModelProfileSummaryResponse>> GetModelProfilesAsync(string? search, string? provider, string? status, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> GetModelProfileAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> CreateModelProfileAsync(CreateModelProfileRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> UpdateModelProfileAsync(string profileName, PutModelProfileRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteModelProfileAsync(string profileName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProfileUsagesResponse> GetModelProfileUsagesAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProfileResolutionResponse> GetModelProfileResolutionAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileOptionMigrationPreviewResponse>> PreviewOptionMigrationAsync(ResourceNamespace @namespace, string profileName, string targetVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> ApplyOptionMigrationAsync(ResourceNamespace @namespace, string profileName, string targetVersion, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
