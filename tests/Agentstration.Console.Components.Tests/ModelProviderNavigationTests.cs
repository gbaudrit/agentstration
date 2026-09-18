using Agentstration.Extensions.Contracts;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Contracts;
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
            var discoveredModelLink = rendered.Find("[data-testid='create-profile-for-model']");
            Assert.AreEqual(
                "/modelprofiles/new?namespace=shared.models&provider=ollama%2Flocal&providerNamespace=shared.models&model=qwen3",
                discoveredModelLink.GetAttribute("href"));
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
            var provider = rendered.FindAll("select").Single(element =>
                element.QuerySelector("option[value='shared.models:ollama/local']") is not null);
            Assert.AreEqual("shared.models:ollama/local", provider.GetAttribute("value"));
        });
    }

    [TestMethod]
    public void NewModelProfilePreselectsDiscoveredDeployment()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = CreateContext(out var providers);
        context.Services.AddSingleton<IModelProfilesClient>(new StubModelProfilesClient());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/modelprofiles/new?namespace=shared.models&provider=ollama%2Flocal&providerNamespace=shared.models&model=qwen3");

        var rendered = context.Render<ModelProfileEditor>();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("ollama/local", providers.RequestedModelProvider);
            Assert.AreEqual("qwen3", rendered.Find("[data-testid='model-profile-model']").GetAttribute("value"));
            Assert.IsTrue(rendered.Find("[data-testid='model-profile-model']").TextContent.Contains("Qwen 3", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void ModelProfileGenerationUsesLocalizedExplanatoryTooltips()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = CreateContext(out _);
        context.Services.AddSingleton<IModelProfilesClient>(new StubModelProfilesClient());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/modelprofiles/new");

        var rendered = context.Render<ModelProfileEditor>();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(7, rendered.FindAll(".metric-help").Count);
            Assert.IsTrue(rendered.FindAll(".metric-help").All(element => element.GetAttribute("tabindex") == "0"));
            Assert.IsTrue(rendered.FindAll(".metric-tooltip").All(element => element.GetAttribute("role") == "tooltip"));
            Assert.IsTrue(rendered.FindAll("label > span").Any(element => element.TextContent.StartsWith("Seed", StringComparison.Ordinal)));
            Assert.IsFalse(rendered.Markup.Contains("Graine", StringComparison.Ordinal));
            Assert.IsTrue(rendered.FindAll(".metric-tooltip").Any(element => element.TextContent.Contains("variabilité des réponses", StringComparison.Ordinal)));
            Assert.IsTrue(rendered.FindAll(".metric-tooltip").Any(element => element.TextContent.Contains("visible depuis ce périmètre", StringComparison.Ordinal)));
        });
    }

    private static BunitContext CreateContext(out StubModelProvidersClient providers)
    {
        var context = new BunitContext();
        providers = new StubModelProvidersClient();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IModelProvidersClient>(providers);
        context.Services.AddSingleton<IExtensionsClient>(new StubExtensionsClient());
        context.Services.AddSingleton<IParametersClient>(new StubParametersClient());
        context.Services.AddSingleton<ISecretsClient>(new StubSecretsClient());
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
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ModelResourceKinds.ModelProvider,
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
        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubParametersClient : IParametersClient
    {
        public Task<IReadOnlyList<ParameterResource>> GetParametersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ParameterResource>>([]);
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> GetParameterAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> CreateParameterAsync(CreateParameterRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> UpdateParameterAsync(string name, ResourceScopeRef scopeRef, PutParameterRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteParameterAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ParameterUsagesResponse> GetParameterUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubSecretsClient : ISecretsClient
    {
        public Task<IReadOnlyList<SecretResponse>> GetSecretsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SecretResponse>>([]);
        public Task<IReadOnlyList<VaultResponse>> GetVaultsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResponse>> GetVaultAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> CreateVaultAsync(CreateVaultRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> UpdateVaultAsync(string name, PutVaultRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteVaultAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VaultInitializationResponse> InitializeVaultAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResponse>> GetSecretAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResource>> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResource>> UpdateSecretAsync(string name, PutSecretRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetSecretValueAsync(string name, string value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSecretValueAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSecretAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SecretUsagesResponse> GetSecretUsagesAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
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
