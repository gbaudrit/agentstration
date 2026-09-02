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
public sealed class ModelProviderNavigationTests
{
    private static readonly ResourceNamespace ProviderNamespace = new("shared.models");

    [TestMethod]
    public void ProviderDetailsOffersModelProfileCreationWithProviderContext()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(out _);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/modelproviders/ollama%2Flocal?namespace=shared.models");

        var rendered = context.Render<ModelProviderDetails>(parameters => parameters
            .Add(component => component.Name, "ollama/local"));

        rendered.WaitForAssertion(() =>
        {
            var link = rendered.FindAll("a").Single(element => element.TextContent.Contains("Create model profile", StringComparison.Ordinal));
            Assert.AreEqual(
                "/modelprofiles/new?namespace=shared.models&provider=ollama%2Flocal&providerNamespace=shared.models",
                link.GetAttribute("href"));
        });
    }

    [TestMethod]
    public void NewModelProfilePreselectsAndLoadsSuggestedProvider()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(out var providers);
        context.Services.AddSingleton<IModelProfilesClient>(new StubModelProfilesClient());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/modelprofiles/new?namespace=shared.models&provider=ollama%2Flocal&providerNamespace=shared.models");

        var rendered = context.Render<ModelProfileEditor>();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(ProviderNamespace, providers.RequestedModelNamespace);
            Assert.AreEqual("ollama/local", providers.RequestedModelProvider);
            var provider = rendered.Find("select");
            Assert.AreEqual("shared.models:ollama/local", provider.GetAttribute("value"));
        });
    }

    private static BunitContext CreateContext(out StubModelProvidersClient providers)
    {
        var context = new BunitContext();
        providers = new StubModelProvidersClient();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IModelProvidersClient>(providers);
        context.Services.AddSingleton<IExtensionsClient>(new StubExtensionsClient());
        context.Services.AddSingleton(new NotificationState());
        return context;
    }

    private sealed class StubModelProvidersClient : IModelProvidersClient
    {
        public ResourceNamespace? RequestedModelNamespace { get; private set; }
        public string? RequestedModelProvider { get; private set; }

        public Task<IReadOnlyList<ModelProviderResponse>> GetModelProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelProviderResponse>>([
                new("shared.models/modelproviders/ollama/local", "ollama/local", new(
                    "Local Ollama", "ollama", "ollama", "ollama-extension", ProviderNamespace.Value,
                    "configuration", "available", "Local Ollama", 1), ProviderNamespace.Value)
            ]);

        public Task<ResourceSnapshot<ModelProviderResource>> GetModelProviderAsync(string providerName, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot<ModelProviderResource>(ProviderResource(providerName), "\"provider-etag\""));

        public Task<ModelProviderUsagesResponse> GetModelProviderUsagesAsync(string providerName, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelProviderUsagesResponse([], 0));

        public Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(string providerName, CancellationToken cancellationToken) =>
            GetProviderModelsAsync(ResourceNamespace.Default, providerName, cancellationToken);

        public Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken)
        {
            RequestedModelNamespace = @namespace;
            RequestedModelProvider = providerName;
            return Task.FromResult<IReadOnlyList<AvailableModelResponse>>([
                new("qwen3", "Qwen 3", "available", [], new Dictionary<string, string>())
            ]);
        }

        public Task<ModelProviderStatusResponse> GetProviderStatusAsync(string providerName, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelProviderStatusResponse(providerName, "available", DateTimeOffset.UnixEpoch, null));

        public Task<ResourceSnapshot<ModelProviderResource>> CreateModelProviderAsync(CreateModelProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProviderResource>> UpdateModelProviderAsync(string providerName, PutModelProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteModelProviderAsync(string providerName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProviderStatusResponse> TestProviderAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static ModelProviderResource ProviderResource(string name) => new()
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.ModelProvider,
            Metadata = new ResourceMetadata { Name = name, Namespace = ProviderNamespace },
            Definition = new ModelProviderProperties
            {
                DisplayName = "Local Ollama",
                Extension = new ResourceReference("ollama-extension", @namespace: ProviderNamespace),
                ContributionId = "ollama"
            }
        };
    }

    private sealed class StubExtensionsClient : IExtensionsClient
    {
        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ExtensionResponse>>([]);
        public Task<ExtensionDiscoveryResponse> DiscoverAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubModelProfilesClient : IModelProfilesClient
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
