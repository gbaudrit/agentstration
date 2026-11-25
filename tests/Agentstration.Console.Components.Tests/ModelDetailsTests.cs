using System.Globalization;
using System.Net;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed partial class ModelDetailsTests
{
    [TestMethod]
    public async Task PageShowsFriendlyEffectiveSummaryAndCanonicalObservedYaml()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var models = new FakeModelsClient(ModelObservationState.Available);
        var providers = new FakeProvidersClient(includeEffectiveModel: true);
        context.Services.AddSingleton<IModelsClient>(models);
        context.Services.AddSingleton<IModelProvidersClient>(providers);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/models/{FakeModelsClient.ResourceName}?namespace=shared.models");

        ConfigureNavigation(context);
        var rendered = context.Render<ModelDetails>(parameters => parameters
            .Add(component => component.Name, FakeModelsClient.ResourceName));

        Assert.AreEqual(new ResourceNamespace("shared.models"), models.RequestedNamespace);
        Assert.AreEqual(FakeModelsClient.ResourceName, models.RequestedName);
        Assert.AreEqual(new ResourceNamespace("shared.models"), providers.RequestedNamespace);
        Assert.AreEqual("foundry", providers.RequestedProvider);
        CollectionAssert.AreEqual(new[] { "overview", "definition/identity", "definition/configuration", "overrides", "observation", "access", "yaml" }, rendered.FindComponent<ResourceNavigation>().Instance.Items.Where(value => value.Navigable).Select(value => value.Path).ToArray());
        Assert.AreEqual("overview", rendered.FindComponent<ResourceNavigation>().Instance.CurrentPath);
        Assert.AreEqual(4, rendered.FindAll(".resource-overview-metrics .metric-card").Count);
        Assert.IsTrue(rendered.Find("[data-testid='model-capabilities']").TextContent.Contains("Not specified", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Find("[data-testid='model-capabilities']").TextContent.Contains("Supported", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Find("[data-testid='model-capabilities']").TextContent.Contains("Native", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Find("[data-testid='model-capabilities']").TextContent.Contains("Provider override", StringComparison.Ordinal));
        Assert.AreEqual("/modelproviders/foundry?namespace=shared.models&scopeRef=%2Finstance&context=%2Finstance", rendered.FindAll("a").First(link => link.TextContent.Contains("Open model provider", StringComparison.Ordinal)).GetAttribute("href"));

        await ModelSectionAsync(context, rendered, "yaml");

        var yaml = rendered.Find("[data-testid='model-yaml-editor'] textarea");
        Assert.IsTrue(yaml.HasAttribute("readonly"));
        Assert.IsTrue((yaml.GetAttribute("value") ?? string.Empty).Contains($"name: {FakeModelsClient.ResourceName}", StringComparison.Ordinal));
        Assert.IsTrue((yaml.GetAttribute("value") ?? string.Empty).Contains("externalId: gpt-5", StringComparison.Ordinal));
        Assert.IsTrue((yaml.GetAttribute("value") ?? string.Empty).Contains("support: unknown", StringComparison.Ordinal));
        Assert.IsTrue((yaml.GetAttribute("value") ?? string.Empty).Contains("maxOutputTokens: 16000", StringComparison.Ordinal));
        Assert.IsFalse((yaml.GetAttribute("value") ?? string.Empty).Contains("maxOutputTokens: 8000", StringComparison.Ordinal));
        Assert.IsFalse((yaml.GetAttribute("value") ?? string.Empty).Contains("specificationOverride", StringComparison.Ordinal));

        await rendered.Find("[data-testid='copy-model-yaml']").ClickAsync(new());

        var invocation = context.JSInterop.Invocations.Single(value => value.Identifier == "navigator.clipboard.writeText");
        Assert.IsTrue(invocation.Arguments[0]?.ToString()?.Contains("kind: Model", StringComparison.Ordinal) == true);
        Assert.AreEqual("Copied", rendered.Find("[data-testid='copy-model-yaml']").TextContent.Trim());
    }

    [TestMethod]
    public void DisappearedModelRemainsInspectableWithActionableExplanation()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddSingleton<IModelsClient>(new FakeModelsClient(ModelObservationState.Missing));
        context.Services.AddSingleton<IModelProvidersClient>(new FakeProvidersClient(includeEffectiveModel: false));
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/models/{FakeModelsClient.ResourceName}?namespace=shared.models");

        ConfigureNavigation(context);
        var rendered = context.Render<ModelDetails>(parameters => parameters
            .Add(component => component.Name, FakeModelsClient.ResourceName));

        Assert.IsTrue(rendered.Find("[data-testid='resource-model-details']").TextContent.Contains("Disappeared", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Find(".model-observation-warning").TextContent.Contains("last observation remains available", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Effective diagnostics are unavailable", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("gpt-5", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OverrideEditorAddsExactModelOverrideAndPreservesProviderState()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        var providers = new FakeProvidersClient(includeEffectiveModel: true, includeOverride: false);
        context.Services.AddSingleton<IModelsClient>(new FakeModelsClient(ModelObservationState.Available));
        context.Services.AddSingleton<IModelProvidersClient>(providers);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/models/{FakeModelsClient.ResourceName}?namespace=shared.models");
        ConfigureNavigation(context);
        var rendered = context.Render<ModelDetails>(parameters => parameters.Add(component => component.Name, FakeModelsClient.ResourceName));

        await rendered.Find("[data-testid='edit-model-override']").ClickAsync(new());
        Assert.AreEqual("overrides", rendered.FindComponent<ResourceNavigation>().Instance.CurrentPath);
        var editorText = rendered.Find("[data-testid='model-override-editor']").TextContent;
        StringAssert.Contains(editorText, "Discovery:");
        StringAssert.Contains(editorText, "Not specified");
        StringAssert.Contains(editorText, "Not overridden");
        StringAssert.Contains(editorText, "Supported");
        StringAssert.Contains(editorText, "Available");
        Assert.IsFalse(rendered.FindAll("[data-testid='model-override-editor'] option")
            .Any(option => string.Equals(option.TextContent.Trim(), "Inherit", StringComparison.Ordinal)));
        await rendered.Find("[data-testid='model-override-context-tokens']").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "64000" });
        Assert.AreEqual("64000", rendered.Find("[data-testid='model-override-context-tokens']").GetAttribute("value"));
        await rendered.Find("#model-provider-override-form").SubmitAsync();

        rendered.WaitForAssertion(() =>
        {
            var properties = providers.UpdatedRequest!.Properties;
            Assert.AreEqual(64_000, properties.SpecificationOverrides["gpt-5"].Limits.ContextTokens);
            Assert.IsTrue(properties.SpecificationOverrides.ContainsKey("other-model"));
            Assert.AreEqual("Foundry", properties.DisplayName);
            StringAssert.Contains(rendered.Find("[data-testid='model-override-message']").TextContent, "saved");
        });
    }

    [TestMethod]
    public async Task AvailableDetailAutomaticallyEnablesUnknownParentSupport()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        var providers = new FakeProvidersClient(includeEffectiveModel: true, includeOverride: false, toolsUnknown: true);
        context.Services.AddSingleton<IModelsClient>(new FakeModelsClient(ModelObservationState.Available, toolsUnknown: true));
        context.Services.AddSingleton<IModelProvidersClient>(providers);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/models/{FakeModelsClient.ResourceName}?namespace=shared.models");
        ConfigureNavigation(context);
        var rendered = context.Render<ModelDetails>(parameters => parameters.Add(component => component.Name, FakeModelsClient.ResourceName));

        await rendered.Find("[data-testid='edit-model-override']").ClickAsync(new());
        var tools = rendered.FindAll(".model-feature-editor-grid > article")
            .Single(element => string.Equals(element.QuerySelector("h4")?.TextContent.Trim(), "Tools", StringComparison.Ordinal));
        Assert.AreEqual(3, tools.QuerySelectorAll(".model-discovery-state").Count(value => string.Equals(value.TextContent.Trim(), "Not specified", StringComparison.Ordinal)));
        await tools.QuerySelectorAll("select")[1].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = ModelOverrideOperation.Add.ToString() });

        tools = rendered.FindAll(".model-feature-editor-grid > article")
            .Single(element => string.Equals(element.QuerySelector("h4")?.TextContent.Trim(), "Tools", StringComparison.Ordinal));
        Assert.AreEqual(ModelFeatureSupport.Native.ToString(), tools.QuerySelectorAll("select")[0].GetAttribute("value"));
        StringAssert.Contains(rendered.Find("[data-testid='model-override-interaction-message']").TextContent, "automatically marked as supported");

        await rendered.Find("#model-provider-override-form").SubmitAsync();

        var saved = providers.UpdatedRequest!.Properties.SpecificationOverrides["gpt-5"].Features.Tools!;
        Assert.AreEqual(ModelFeatureSupport.Native, saved.Support);
        Assert.IsTrue(saved.Modes!.Add.ContainsKey(ModelToolMode.Function));
    }

    [TestMethod]
    public async Task OverrideEditorRemovesOnlyExactModelOverride()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        var providers = new FakeProvidersClient(includeEffectiveModel: true);
        context.Services.AddSingleton<IModelsClient>(new FakeModelsClient(ModelObservationState.Available));
        context.Services.AddSingleton<IModelProvidersClient>(providers);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/models/{FakeModelsClient.ResourceName}?namespace=shared.models");
        ConfigureNavigation(context);
        var rendered = context.Render<ModelDetails>(parameters => parameters.Add(component => component.Name, FakeModelsClient.ResourceName));

        await rendered.Find("[data-testid='edit-model-override']").ClickAsync(new());
        await rendered.Find("[data-testid='remove-model-override']").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            Assert.IsFalse(providers.UpdatedRequest!.Properties.SpecificationOverrides.ContainsKey("gpt-5"));
            Assert.IsTrue(providers.UpdatedRequest.Properties.SpecificationOverrides.ContainsKey("other-model"));
            StringAssert.Contains(rendered.Find("[data-testid='model-override-message']").TextContent, "removed");
        });
    }

    [TestMethod]
    public async Task OverrideEditorSurfacesConcurrencyConflictWithoutOverwritingProvider()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        var providers = new FakeProvidersClient(includeEffectiveModel: true) { FailWithConcurrency = true };
        context.Services.AddSingleton<IModelsClient>(new FakeModelsClient(ModelObservationState.Available));
        context.Services.AddSingleton<IModelProvidersClient>(providers);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/models/{FakeModelsClient.ResourceName}?namespace=shared.models");
        ConfigureNavigation(context);
        var rendered = context.Render<ModelDetails>(parameters => parameters.Add(component => component.Name, FakeModelsClient.ResourceName));

        await rendered.Find("[data-testid='edit-model-override']").ClickAsync(new());
        await rendered.Find("[data-testid='model-override-context-tokens']").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "64000" });
        await rendered.Find("#model-provider-override-form").SubmitAsync();

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("[data-testid='model-override-error']").TextContent, "changed while you were editing");
            StringAssert.Contains(rendered.Find("[data-testid='model-override-error']").TextContent, "Refresh");
        });
    }

    [TestMethod]
    public void FrenchCatalogKeepsEquivalentModelDetailMeaning()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var localizer = context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<ModelDetailsStrings>>();

        Assert.AreEqual("Vue d’ensemble", localizer["Tab.Overview"].Value);
        Assert.AreEqual("Surcharges", localizer["Tab.Overrides"].Value);
        Assert.AreEqual("Surcharge du fournisseur", localizer["ProviderOverride"].Value);
        Assert.AreEqual("Observé · lecture seule", localizer["ObservedReadOnly"].Value);
        Assert.AreEqual("Copier le YAML", localizer["CopyYaml"].Value);
        Assert.AreEqual("Découverte :", localizer["Discovery"].Value);
        Assert.AreEqual("Surcharge", localizer["Override"].Value);
        Assert.AreEqual("Non surchargé", localizer["NotOverridden"].Value);
        Assert.AreEqual("Non spécifié", localizer["DiscoverySupport.Unknown"].Value);
        Assert.AreEqual("Pris en charge", localizer["OverrideSupport.Native"].Value);
        Assert.AreEqual("Pris en charge", localizer["Support.Native"].Value);
        Assert.AreEqual("Disponible", localizer["Operation.Add"].Value);
    }

    private static void ConfigureNavigation(BunitContext context)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var current = context.Services.GetRequiredService<NavigationManager>().Uri.Split('#')[0];
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var path in new[] { "overview", "definition/identity", "definition/configuration", "overrides", "observation", "access", "yaml" })
            module.Setup<string>("navigate", call => call.Arguments[0] is string uri && uri.EndsWith("#" + path, StringComparison.Ordinal)).SetResult(current + "#" + path);
    }
    private static Task ModelSectionAsync(BunitContext context, IRenderedComponent<ModelDetails> cut, string path) =>
        cut.InvokeAsync(() => { var nav = context.Services.GetRequiredService<NavigationManager>(); nav.NavigateTo(nav.Uri.Split('#')[0] + "#" + path); });

    private sealed class FakeModelsClient(ModelObservationState state, bool toolsUnknown = false) : IModelsClient
    {
        public const string ResourceName = "foundry.gpt-5-1234567890abcdef";
        public ResourceNamespace RequestedNamespace { get; private set; }
        public string? RequestedName { get; private set; }
        public ModelResource? Replacement { get; set; }
        public AgentstrationApiException? ReadError { get; set; }
        public Task<ResourceSnapshot<ModelResource>>? PendingRead { get; set; }
        public string ReadEtag { get; set; } = "\"model-etag\"";

        public Task<ResourceSnapshot<ModelResource>> GetModelAsync(ResourceNamespace ns, string name, ResourceScopeRef owner, CancellationToken token) =>
            owner == ResourceScopeRef.Instance ? GetModelAsync(ns, name, token)
                : Task.FromException<ResourceSnapshot<ModelResource>>(new AgentstrationApiException("Missing exact model", "preview", HttpStatusCode.NotFound));

        public Task<ResourceSnapshot<ModelResource>> GetModelAsync(ResourceNamespace @namespace, string modelName, CancellationToken cancellationToken)
        {
            RequestedNamespace = @namespace;
            RequestedName = modelName;
            if (ReadError is not null) return Task.FromException<ResourceSnapshot<ModelResource>>(ReadError);
            if (PendingRead is not null) return PendingRead;
            var observedAt = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
            return Task.FromResult(new ResourceSnapshot<ModelResource>(Replacement ?? new ModelResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ModelResourceKinds.Model,
                Uid = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                Metadata = new ResourceMetadata { Name = ResourceName, Namespace = @namespace },
                ScopeRef = ResourceScopeRef.Instance,
                Generation = 3,
                Definition = new ModelProperties
                {
                    DisplayName = "GPT-5",
                    Provider = new ResourceReference("foundry", @namespace: @namespace),
                    ProviderUid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    ExternalId = "gpt-5",
                    ProviderStatus = state == ModelObservationState.Available ? "available" : "unavailable",
                    Identity = new ModelIdentity { Publisher = "OpenAI", Model = "GPT-5", Version = "2026-09" },
                    Specification = ObservedSpecification(toolsUnknown),
                    Observation = new ModelObservation
                    {
                        State = state,
                        FirstObservedAt = observedAt.AddDays(-7),
                        LastObservedAt = observedAt,
                        LastAttemptedAt = state == ModelObservationState.Available ? observedAt : observedAt.AddHours(2)
                    }
                }
            }, ReadEtag));
        }
    }

    private sealed class FakeProvidersClient(bool includeEffectiveModel, bool includeOverride = true, bool toolsUnknown = false) : IModelProvidersClient
    {
        private ModelProviderResource provider = CreateProvider(includeOverride);
        private string etag = "\"provider-etag\"";
        public ResourceNamespace RequestedNamespace { get; private set; }
        public string? RequestedProvider { get; private set; }
        public PutModelProviderRequest? UpdatedRequest { get; private set; }
        public bool FailWithConcurrency { get; init; }
        public ModelProviderResource Resource { get => provider; set => provider = value; }
        public string ReadEtag { get => etag; set => etag = value; }
        public string? UpdateEtag { get; private set; }
        public AgentstrationApiException? ReadError { get; set; }
        public AgentstrationApiException? InventoryError { get; set; }

        public Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken)
        {
            RequestedNamespace = @namespace;
            RequestedProvider = providerName;
            if (InventoryError is not null) return Task.FromException<IReadOnlyList<AvailableModelResponse>>(InventoryError);
            if (!includeEffectiveModel) return Task.FromResult<IReadOnlyList<AvailableModelResponse>>([]);
            provider.Definition.SpecificationOverrides.TryGetValue("gpt-5", out var @override);
            return Task.FromResult<IReadOnlyList<AvailableModelResponse>>([new(
                "gpt-5",
                "GPT-5",
                "available",
                EffectiveModelSpecificationResolver.Resolve(ObservedSpecification(toolsUnknown), @override),
                new ModelIdentity { Publisher = "OpenAI", Model = "GPT-5", Version = "2026-09" },
                ObservedSpecification(toolsUnknown),
                @override,
                FakeModelsClient.ResourceName)]);
        }

        public Task<IReadOnlyList<ModelProviderResponse>> GetModelProvidersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProviderResource>> GetModelProviderAsync(string providerName, CancellationToken cancellationToken) =>
            GetModelProviderAsync(ResourceNamespace.Default, providerName, cancellationToken);
        public Task<ResourceSnapshot<ModelProviderResource>> GetModelProviderAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken) =>
            ReadError is not null ? Task.FromException<ResourceSnapshot<ModelProviderResource>>(ReadError) : Task.FromResult(new ResourceSnapshot<ModelProviderResource>(provider, etag));
        public Task<ResourceSnapshot<ModelProviderResource>> CreateModelProviderAsync(CreateModelProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProviderResource>> UpdateModelProviderAsync(string providerName, PutModelProviderRequest request, string currentEtag, CancellationToken cancellationToken) =>
            UpdateModelProviderAsync(ResourceNamespace.Default, providerName, request, currentEtag, cancellationToken);
        public Task<ResourceSnapshot<ModelProviderResource>> UpdateModelProviderAsync(ResourceNamespace @namespace, string providerName, PutModelProviderRequest request, string currentEtag, CancellationToken cancellationToken)
        {
            UpdatedRequest = request; UpdateEtag = currentEtag;
            if (FailWithConcurrency) throw new AgentstrationApiException("The provider was modified by another administrator.", "conflict", HttpStatusCode.PreconditionFailed);
            provider = provider with { Definition = request.Properties };
            etag = "\"provider-etag-2\"";
            return Task.FromResult(new ResourceSnapshot<ModelProviderResource>(provider, etag));
        }
        public Task DeleteModelProviderAsync(string providerName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProviderUsagesResponse> GetModelProviderUsagesAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProviderStatusResponse> GetProviderStatusAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProviderStatusResponse> TestProviderAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static ModelProviderResource CreateProvider(bool includeTargetOverride)
        {
            var overrides = new Dictionary<string, ModelSpecificationOverride>(StringComparer.Ordinal)
            {
                ["other-model"] = new() { Limits = new ModelLimitOverrides { ContextTokens = 4_096 } }
            };
            if (includeTargetOverride)
            {
                overrides["gpt-5"] = new ModelSpecificationOverride
                {
                    Features = new ModelFeatureOverrides { Streaming = new ModelStreamingFeatureOverride { Support = ModelFeatureSupport.Native } },
                    Limits = new ModelLimitOverrides { MaxOutputTokens = 8_000 }
                };
            }
            return new ModelProviderResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ModelResourceKinds.ModelProvider,
                Uid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                Metadata = new ResourceMetadata { Name = "foundry", Namespace = new ResourceNamespace("shared.models") },
                ScopeRef = ResourceScopeRef.Instance,
                Definition = new ModelProviderProperties
                {
                    DisplayName = "Foundry",
                    Extension = new ResourceReference("foundry-extension"),
                    ContributionId = "foundry",
                    SpecificationOverrides = overrides
                }
            };
        }
    }

    private static ModelSpecification ObservedSpecification(bool toolsUnknown = false) => new()
    {
        Input = [ModelContentType.Text, ModelContentType.Image],
        Output = [ModelContentType.Text],
        Features = new ModelFeatureSpecifications
        {
            Streaming = new ModelStreamingFeatureSpecification { Support = ModelFeatureSupport.Unknown },
            Tools = toolsUnknown
                ? new ModelToolsFeatureSpecification { Support = ModelFeatureSupport.Unknown }
                : new ModelToolsFeatureSpecification { Support = ModelFeatureSupport.Native, Modes = new Dictionary<ModelToolMode, ModelToolModeSpecification> { [ModelToolMode.Function] = new() } },
            StructuredOutput = new ModelStructuredOutputFeatureSpecification { Support = ModelFeatureSupport.Partial, Formats = new Dictionary<ModelStructuredOutputFormat, ModelStructuredOutputFormatSpecification> { [ModelStructuredOutputFormat.JsonSchema] = new() { SupportsStrict = false } } },
            Reasoning = new ModelReasoningFeatureSpecification { Support = ModelFeatureSupport.Native, Efforts = new Dictionary<ReasoningEffort, ModelReasoningEffortSpecification> { [ReasoningEffort.High] = new() } }
        },
        Limits = new ModelLimits { ContextTokens = 128_000, MaxOutputTokens = 16_000 }
    };

    private sealed class TestCultureScope : IDisposable
    {
        private readonly CultureInfo originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        public TestCultureScope(string name) { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name); }
        public void Dispose() { CultureInfo.CurrentCulture = originalCulture; CultureInfo.CurrentUICulture = originalUiCulture; }
    }
}
