using System.Text.Json;
using Agentstration.Extensions.Contracts;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Contracts;
using Agentstration.Web.Components.Models;
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
    private static readonly ResourceScopeRef WorkspaceScope = ResourceScopeRef.Workspace(Guid.Parse("b07e7249-b525-4c32-a7bb-5e805fc768e7"));

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
            Assert.AreEqual(
                "/models/ollama-local.qwen3-deadbeef?namespace=shared.models",
                rendered.Find("[data-testid='open-model-details']").GetAttribute("href"));
            CollectionAssert.AreEqual(new[] { "Overview", "Configuration" }, rendered.FindAll("[role='tab']").Select(item => item.TextContent.Trim()).ToArray());
            Assert.IsFalse(rendered.Markup.Contains("data-testid=\"model-provider-form\"", StringComparison.Ordinal));
        });

        rendered.Find("#model-provider-configuration-tab").Click();
        Assert.IsNotNull(rendered.Find("[data-testid='model-provider-form']"));
        Assert.AreEqual("true", rendered.Find("#model-provider-configuration-tab").GetAttribute("data-interactive"));
        Assert.IsFalse(rendered.Markup.Contains("data-testid=\"model-provider-refresh\"", StringComparison.Ordinal));
        rendered.Find("#model-provider-overview-tab").Click();
        rendered.Find("[data-testid='model-provider-refresh']").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, context.Services.GetRequiredService<IModelProvidersClient>() is StubModelProvidersClient stub ? stub.RefreshCalls : -1);
            StringAssert.Contains(rendered.Find("[data-testid='model-discovery-result']").TextContent, "1 updated");
            Assert.AreEqual(1, rendered.FindAll("[data-testid='open-model-details']").Count);
        });
    }

    [TestMethod]
    public async Task CreatingProviderRunsInitialDiscoveryAndKeepsCreationSuccessful()
    {
        using var culture = new TestCultureScope("en-US");
        ExtensionResponse extension = new(
            RegistrationName: "ollama-extension", RegistrationNamespace: ResourceNamespace.DefaultValue,
            Endpoint: new Uri("http://localhost:5260"), Status: "available",
            Extension: new ExtensionIdentityResponse("ollama.extension", "Ollama", "1.0.0", null),
            Contributions: [new ExtensionContributionResponse("model-provider", "ollama")],
            OptionSets: [], Usages: [], Providers: [], Details: null, DiscoverySource: "manual");
        using var context = CreateContext(out var providers, [extension]);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/modelproviders/new?extension=ollama-extension&extensionNamespace=default&contributionId=ollama");
        var rendered = context.Render<ModelProviderDetails>();
        rendered.WaitForElement("[data-testid='model-provider-name']").Change("ollama-local");
        rendered.Find("[data-testid='model-provider-display-name']").Change("Ollama local");

        await rendered.Find("[data-testid='model-provider-form']").SubmitAsync();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, providers.RefreshCalls);
            Assert.AreEqual("ollama-local", providers.RefreshedProvider);
            Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.Contains("/modelproviders/ollama-local", StringComparison.Ordinal));
            Assert.IsTrue(context.Services.GetRequiredService<NotificationState>().Items.Any(item => item.Title == "Initial model discovery complete"));
            StringAssert.Contains(rendered.Find("[data-testid='model-discovery-result']").TextContent, "2 total");
            Assert.AreEqual(1, rendered.FindAll("[data-testid='open-model-details']").Count);
        });
    }

    [TestMethod]
    public async Task CreatingProviderKeepsCreationSuccessfulWhenInitialDiscoveryFails()
    {
        using var culture = new TestCultureScope("en-US");
        ExtensionResponse extension = new(
            RegistrationName: "ollama-extension", RegistrationNamespace: ResourceNamespace.DefaultValue,
            Endpoint: new Uri("http://localhost:5260"), Status: "available",
            Extension: new ExtensionIdentityResponse("ollama.extension", "Ollama", "1.0.0", null),
            Contributions: [new ExtensionContributionResponse("model-provider", "ollama")],
            OptionSets: [], Usages: [], Providers: [], Details: null, DiscoverySource: "manual");
        using var context = CreateContext(out var providers, [extension]);
        providers.FailRefresh = true;
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/modelproviders/new?extension=ollama-extension&extensionNamespace=default&contributionId=ollama");
        var rendered = context.Render<ModelProviderDetails>();
        rendered.WaitForElement("[data-testid='model-provider-name']").Change("ollama-local");
        rendered.Find("[data-testid='model-provider-display-name']").Change("Ollama local");

        await rendered.Find("[data-testid='model-provider-form']").SubmitAsync();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, providers.RefreshCalls);
            Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.Contains("/modelproviders/ollama-local", StringComparison.Ordinal));
            Assert.IsTrue(context.Services.GetRequiredService<NotificationState>().Items.Any(item =>
                item.Title.Contains("initial discovery failed", StringComparison.OrdinalIgnoreCase)
                && item.Status == UiStatus.Warning));
        });
    }

    [TestMethod]
    public async Task CreatingProviderKeepsUnconfiguredOptionalRequirementEditableAfterNavigation()
    {
        using var culture = new TestCultureScope("en-US");
        ExtensionResponse extension = new(
            RegistrationName: "typed-extension", RegistrationNamespace: ResourceNamespace.DefaultValue,
            Endpoint: new Uri("http://localhost:5000"), Status: "available",
            Extension: new ExtensionIdentityResponse("typed.extension", "Typed extension", "1.0.0", null),
            Contributions: [new ExtensionContributionResponse("model-provider", "typed")],
            OptionSets: [], Usages: [], Providers: [], Details: null, DiscoverySource: "manual",
            ValueRequirements: [new("model-provider", "typed", "apiVersion", false, "string", "standard", "API version")]);
        using var context = CreateContext(out _, [extension]);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/modelproviders/new?extension=typed-extension&extensionNamespace=default&contributionId=typed");
        var rendered = context.Render<ModelProviderDetails>();
        rendered.WaitForElement("[data-testid='model-provider-name']").Change("typed-provider");
        rendered.Find("[data-testid='model-provider-display-name']").Change("Typed provider");

        await rendered.Find("[data-testid='model-provider-form']").SubmitAsync();
        rendered.Find("#model-provider-configuration-tab").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(rendered.Find("[data-testid='model-provider-form']"));
            Assert.IsNotNull(rendered.Find("[data-requirement-id='apiVersion']"));
        });
    }

    [TestMethod]
    public void ProviderDetailsKeepsConfigurationDisabledUntilRouteReloadIsConsistent()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(out var providers);
        var rendered = context.Render<ModelProviderDetails>(parameters => parameters
            .Add(component => component.Name, "ollama/local"));
        rendered.WaitForElement("#model-provider-configuration-tab[data-interactive='true']");
        providers.DelayUsages();

        rendered.Render(parameters => parameters
            .Add(component => component.Name, "ollama/reloaded"));

        rendered.WaitForAssertion(() =>
        {
            var configuration = rendered.Find("#model-provider-configuration-tab");
            Assert.AreEqual("false", configuration.GetAttribute("data-interactive"));
            Assert.IsTrue(configuration.HasAttribute("disabled"));
        });

        providers.CompleteUsages();
        rendered.WaitForAssertion(() =>
        {
            var configuration = rendered.Find("#model-provider-configuration-tab");
            Assert.AreEqual("true", configuration.GetAttribute("data-interactive"));
            Assert.IsFalse(configuration.HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public void NewProviderDisplaysCanonicalRequirementTypeAndFormat()
    {
        using var culture = new TestCultureScope("fr-FR");
        ExtensionResponse extension = new(
            RegistrationName: "typed-extension",
            RegistrationNamespace: ResourceNamespace.DefaultValue,
            Endpoint: new Uri("http://localhost:5000"),
            Status: "available",
            Extension: new ExtensionIdentityResponse("typed.extension", "Typed extension", "1.0.0", null),
            Contributions: [new ExtensionContributionResponse("model-provider", "typed")],
            OptionSets: [],
            Usages: [],
            Providers: [],
            Details: null,
            DiscoverySource: "manual",
            ValueRequirements:
            [
                new ExtensionValueRequirementResponse(
                    "model-provider", "typed", "endpoint", true, "string", "standard", null, "uri")
            ]);
        using var context = CreateContext(out _, [extension]);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/modelproviders/new?extension=typed-extension&extensionNamespace=default&contributionId=typed");

        var rendered = context.Render<ModelProviderDetails>();

        rendered.WaitForAssertion(() =>
        {
            var requirement = rendered.Find("[data-requirement-id='endpoint']");
            StringAssert.Contains(requirement.TextContent, "Type attendu: string");
            StringAssert.Contains(requirement.TextContent, "Format: uri");
            Assert.IsFalse(requirement.TextContent.Contains("unknown", StringComparison.OrdinalIgnoreCase));
        });
    }

    [TestMethod]
    public void NewProviderOpensReusableContextualParameterCreator()
    {
        using var culture = new TestCultureScope("fr-FR");
        ExtensionResponse extension = new(
            RegistrationName: "typed-extension",
            RegistrationNamespace: ResourceNamespace.DefaultValue,
            Endpoint: new Uri("http://localhost:5000"),
            Status: "available",
            Extension: new ExtensionIdentityResponse("typed.extension", "Typed extension", "1.0.0", null),
            Contributions: [new ExtensionContributionResponse("model-provider", "typed")],
            OptionSets: [], Usages: [], Providers: [], Details: null, DiscoverySource: "manual",
            ValueRequirements: [new("model-provider", "typed", "projectEndpoint", true, "string", "standard", "Project endpoint", "uri")]);
        using var context = CreateContext(out _, [extension]);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/modelproviders/new?extension=typed-extension&extensionNamespace=default&contributionId=typed");

        var rendered = context.Render<ModelProviderDetails>();
        var create = rendered.WaitForElement("[data-testid='model-provider-binding-create']:not([disabled])");
        create.Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(rendered.Find("[data-testid='contextual-parameter-creator']"));
            Assert.AreEqual("mp-typed-projectendpoint", rendered.Find("[data-testid='contextual-parameter-name']").GetAttribute("value"));
            Assert.AreEqual(ParameterValueType.Text.ToString(), rendered.Find("[data-testid='contextual-parameter-value-type']").GetAttribute("value"));
            StringAssert.Contains(rendered.Markup, "Format attendu : uri");
        });
    }

    [TestMethod]
    public void NewProviderProjectsAllowedValuesIntoTheReusableParameterCreator()
    {
        using var culture = new TestCultureScope("fr-FR");
        ExtensionResponse extension = new(
            RegistrationName: "typed-extension",
            RegistrationNamespace: ResourceNamespace.DefaultValue,
            Endpoint: new Uri("http://localhost:5000"),
            Status: "available",
            Extension: new ExtensionIdentityResponse("typed.extension", "Typed extension", "1.0.0", null),
            Contributions: [new ExtensionContributionResponse("model-provider", "typed")],
            OptionSets: [], Usages: [], Providers: [], Details: null, DiscoverySource: "manual",
            ValueRequirements:
            [
                new("model-provider", "typed", "authenticationMode", true, "string", "standard", "Authentication mode",
                    AllowedValues:
                    [
                        JsonSerializer.SerializeToElement("ApiKey"),
                        JsonSerializer.SerializeToElement("ManagedIdentity"),
                        JsonSerializer.SerializeToElement("WorkloadIdentity"),
                        JsonSerializer.SerializeToElement("Development")
                    ])
            ]);
        using var context = CreateContext(out _, [extension]);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/modelproviders/new?extension=typed-extension&extensionNamespace=default&contributionId=typed");

        var rendered = context.Render<ModelProviderDetails>();
        rendered.WaitForElement("[data-testid='model-provider-binding-create']:not([disabled])").Click();

        var select = rendered.WaitForElement("select[data-testid='contextual-parameter-value']");
        CollectionAssert.AreEqual(
            new[] { "ApiKey", "ManagedIdentity", "WorkloadIdentity", "Development" },
            select.QuerySelectorAll("option").Select(option => option.GetAttribute("value")).ToArray());
        Assert.AreEqual("ApiKey", ((AngleSharp.Html.Dom.IHtmlSelectElement)select).Value);
    }

    [TestMethod]
    public void ContextuallyCreatedParameterBecomesTheVisibleBindingSelection()
    {
        using var culture = new TestCultureScope("en-US");
        ExtensionResponse extension = new(
            RegistrationName: "typed-extension", RegistrationNamespace: ResourceNamespace.DefaultValue,
            Endpoint: new Uri("http://localhost:5000"), Status: "available",
            Extension: new ExtensionIdentityResponse("typed.extension", "Typed extension", "1.0.0", null),
            Contributions: [new ExtensionContributionResponse("model-provider", "typed")],
            OptionSets: [], Usages: [], Providers: [], Details: null, DiscoverySource: "manual",
            ValueRequirements: [new("model-provider", "typed", "projectEndpoint", true, "string", "standard", "Project endpoint", "uri")]);
        using var context = CreateContext(out _, [extension]);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/modelproviders/new?extension=typed-extension&extensionNamespace=default&contributionId=typed");
        var rendered = context.Render<ModelProviderDetails>();
        rendered.WaitForElement("[data-testid='model-provider-name']").Change("foundry");
        rendered.WaitForElement("[data-testid='model-provider-binding-create']:not([disabled])").Click();
        rendered.WaitForElement("[data-testid='contextual-parameter-value']").Change("https://example.test/projects/demo");
        rendered.Find("[data-testid='contextual-parameter-create']").Click();

        rendered.WaitForAssertion(() =>
        {
            var target = rendered.Find("[data-testid='model-provider-binding-target']");
            Assert.AreEqual($"{WorkspaceScope.Value}|default|mp-foundry-projectendpoint", ((AngleSharp.Html.Dom.IHtmlSelectElement)target).Value);
            Assert.AreEqual($"{WorkspaceScope.Value}|default|mp-foundry-projectendpoint", target.QuerySelector("option[selected]")?.GetAttribute("value"));
            StringAssert.Contains(target.TextContent, "projectEndpoint");
            Assert.IsFalse(rendered.Markup.Contains("contextual-parameter-creator", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void ContextuallyCreatedSecretBecomesTheVisibleBindingSelectionWithoutRenderingItsValue()
    {
        using var culture = new TestCultureScope("en-US");
        ExtensionResponse extension = new(
            RegistrationName: "secured-extension", RegistrationNamespace: ResourceNamespace.DefaultValue,
            Endpoint: new Uri("http://localhost:5000"), Status: "available",
            Extension: new ExtensionIdentityResponse("secured.extension", "Secured extension", "1.0.0", null),
            Contributions: [new ExtensionContributionResponse("model-provider", "secured")],
            OptionSets: [], Usages: [], Providers: [], Details: null, DiscoverySource: "manual",
            ValueRequirements: [new("model-provider", "secured", "credential", true, "string", "secured", "API credential")]);
        using var context = CreateContext(out _, [extension]);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/modelproviders/new?extension=secured-extension&extensionNamespace=default&contributionId=secured");
        var rendered = context.Render<ModelProviderDetails>();
        rendered.WaitForElement("[data-testid='model-provider-name']").Change("foundry");
        rendered.WaitForElement("[data-testid='model-provider-binding-create']:not([disabled])").Click();
        Assert.AreEqual("mp-foundry-credential", rendered.WaitForElement("[data-testid='contextual-secret-name']").GetAttribute("value"));
        rendered.WaitForElement("[data-testid='contextual-secret-value']").Change("browser-secret-must-disappear");
        rendered.Find("[data-testid='contextual-secret-create']").Click();

        rendered.WaitForAssertion(() =>
        {
            var target = rendered.Find("[data-testid='model-provider-binding-target']");
            var expected = $"{WorkspaceScope.Value}|default|mp-foundry-credential";
            Assert.AreEqual(expected, ((AngleSharp.Html.Dom.IHtmlSelectElement)target).Value);
            Assert.AreEqual(expected, target.QuerySelector("option[selected]")?.GetAttribute("value"));
            Assert.IsFalse(rendered.Markup.Contains("browser-secret-must-disappear", StringComparison.Ordinal));
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

    private static BunitContext CreateContext(out StubModelProvidersClient providers,
        IReadOnlyList<ExtensionResponse>? extensions = null)
    {
        var context = new BunitContext();
        providers = new StubModelProvidersClient();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IModelProvidersClient>(providers);
        context.Services.AddSingleton<IExtensionsClient>(new StubExtensionsClient(extensions ?? []));
        context.Services.AddSingleton<IParametersClient>(new StubParametersClient());
        context.Services.AddSingleton<ISecretsClient>(new StubSecretsClient());
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new StubResourceScopeInventoryClient());
        context.Services.AddSingleton(new NotificationState());
        return context;
    }

    private sealed class StubResourceScopeInventoryClient : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(WorkspaceScope, ResourceScopeKind.Workspace, "Default workspace", true)]);
    }

    private sealed class StubModelProvidersClient : IModelProvidersClient
    {
        public ResourceNamespace? RequestedModelNamespace { get; private set; }
        public string? RequestedModelProvider { get; private set; }
        public int RefreshCalls { get; private set; }
        public string? RefreshedProvider { get; private set; }
        public bool FailRefresh { get; set; }
        private TaskCompletionSource<ModelProviderUsagesResponse>? usagesCompletion;

        public void DelayUsages() =>
            usagesCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteUsages() =>
            usagesCompletion!.SetResult(new ModelProviderUsagesResponse([], 0));

        public Task<IReadOnlyList<ModelProviderResponse>> GetModelProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelProviderResponse>>([
                new("shared.models/modelproviders/ollama/local", "ollama/local", new(
                    "Local Ollama", "ollama", "ollama", "ollama-extension", ProviderNamespace.Value,
                    "configuration", "available", "Local Ollama", 1), ProviderNamespace.Value)
            ]);

        public Task<ResourceSnapshot<ModelProviderResource>> GetModelProviderAsync(string providerName, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot<ModelProviderResource>(ProviderResource(providerName), "\"provider-etag\""));

        public Task<ModelProviderUsagesResponse> GetModelProviderUsagesAsync(string providerName, CancellationToken cancellationToken) =>
            usagesCompletion?.Task ?? Task.FromResult(new ModelProviderUsagesResponse([], 0));

        public Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(string providerName, CancellationToken cancellationToken) =>
            GetProviderModelsAsync(ResourceNamespace.Default, providerName, cancellationToken);

        public Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken)
        {
            RequestedModelNamespace = @namespace;
            RequestedModelProvider = providerName;
            return Task.FromResult<IReadOnlyList<AvailableModelResponse>>([
                new("qwen3", "Qwen 3", "available", new ModelSpecification(), ResourceName: "ollama-local.qwen3-deadbeef")
            ]);
        }

        public Task<ModelProviderStatusResponse> GetProviderStatusAsync(string providerName, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelProviderStatusResponse(providerName, "available", DateTimeOffset.UnixEpoch, null));

        public Task<ResourceSnapshot<ModelProviderResource>> CreateModelProviderAsync(CreateModelProviderRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot<ModelProviderResource>(new ModelProviderResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ModelResourceKinds.ModelProvider,
                Metadata = new ResourceMetadata { Name = request.Name, Namespace = ResourceNamespace.Parse(request.Namespace) },
                ScopeRef = request.ScopeRef,
                Definition = request.Properties
            }, "\"provider-created\""));
        public Task<ModelDiscoveryDiffResponse> RefreshProviderModelsAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            RefreshedProvider = providerName;
            if (FailRefresh)
            {
                throw new AgentstrationApiException("Ollama is unavailable.", "ollama-unavailable");
            }

            return Task.FromResult(new ModelDiscoveryDiffResponse(0, 1, 1, 0, 0, 2));
        }
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

    private sealed class StubExtensionsClient(IReadOnlyList<ExtensionResponse> extensions) : IExtensionsClient
    {
        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken) => Task.FromResult(extensions);
        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubParametersClient : IParametersClient
    {
        private readonly List<ParameterResource> resources = [];
        public Task<IReadOnlyList<ParameterResource>> GetParametersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ParameterResource>>(resources.ToArray());
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> GetParameterAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> CreateParameterAsync(CreateParameterRequest request, CancellationToken cancellationToken)
        {
            var resource = new ParameterResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ParameterResourceKinds.Parameter,
                Metadata = new() { Name = request.Name },
                ScopeRef = request.ScopeRef,
                Definition = request.Properties
            };
            resources.Add(resource);
            return Task.FromResult(new ResourceSnapshot<ParameterResource>(resource, "\"parameter-etag\""));
        }
        public Task<ResourceSnapshot<ParameterResource>> UpdateParameterAsync(string name, ResourceScopeRef scopeRef, PutParameterRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteParameterAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ParameterUsagesResponse> GetParameterUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubSecretsClient : ISecretsClient
    {
        private readonly List<SecretResponse> resources = [];
        public Task<IReadOnlyList<SecretResponse>> GetSecretsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SecretResponse>>(resources.ToArray());
        public Task<IReadOnlyList<VaultResponse>> GetVaultsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VaultResponse>>([new(new VaultResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Vault,
            Metadata = new() { Name = "local-vault" },
            ScopeRef = WorkspaceScope,
            Definition = new VaultProperties { DisplayName = "Local vault", ProviderType = "local" }
        }, "available")]);
        public Task<ResourceSnapshot<VaultResponse>> GetVaultAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> CreateVaultAsync(CreateVaultRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> UpdateVaultAsync(string name, PutVaultRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteVaultAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VaultInitializationResponse> InitializeVaultAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResponse>> GetSecretAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResource>> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken)
        {
            var resource = new SecretResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SecretResourceKinds.Secret,
                Metadata = new() { Name = request.Name },
                ScopeRef = request.ScopeRef,
                Definition = request.Properties
            };
            resources.Add(new(resource, "Missing", false));
            return Task.FromResult(new ResourceSnapshot<SecretResource>(resource, "\"secret-etag\""));
        }
        public Task<ResourceSnapshot<SecretResource>> UpdateSecretAsync(string name, PutSecretRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetSecretValueAsync(string name, string value, CancellationToken cancellationToken)
        {
            var index = resources.FindIndex(value => value.Resource.Name == name);
            resources[index] = resources[index] with { ValueStatus = "Configured", ValueConfigured = true };
            return Task.CompletedTask;
        }
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
