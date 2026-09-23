using System.Globalization;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ModelDetailsTests
{
    [TestMethod]
    public async Task PageShowsFriendlyEffectiveSummaryAndCanonicalObservedYaml()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var models = new FakeModelsClient(ModelObservationState.Available);
        var providers = new FakeProvidersClient(includeEffectiveModel: true);
        context.Services.AddSingleton<IModelsClient>(models);
        context.Services.AddSingleton<IModelProvidersClient>(providers);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/models/{FakeModelsClient.ResourceName}?namespace=shared.models");

        var rendered = context.Render<ModelDetails>(parameters => parameters
            .Add(component => component.Name, FakeModelsClient.ResourceName));

        Assert.AreEqual(new ResourceNamespace("shared.models"), models.RequestedNamespace);
        Assert.AreEqual(FakeModelsClient.ResourceName, models.RequestedName);
        Assert.AreEqual(new ResourceNamespace("shared.models"), providers.RequestedNamespace);
        Assert.AreEqual("foundry", providers.RequestedProvider);
        CollectionAssert.AreEqual(new[] { "Overview", "YAML" }, rendered.FindAll("[role='tab']").Select(value => value.TextContent.Trim()).ToArray());
        Assert.AreEqual("true", rendered.Find("#model-overview-tab").GetAttribute("aria-selected"));
        Assert.AreEqual(4, rendered.FindAll(".model-detail-metrics .metric-card").Count);
        Assert.IsTrue(rendered.Find("[data-testid='model-capabilities']").TextContent.Contains("Unknown", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Find("[data-testid='model-capabilities']").TextContent.Contains("Native", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Find("[data-testid='model-capabilities']").TextContent.Contains("Provider override", StringComparison.Ordinal));
        Assert.AreEqual("/modelproviders/foundry?namespace=shared.models", rendered.FindAll("a").First(link => link.TextContent.Contains("Open model provider", StringComparison.Ordinal)).GetAttribute("href"));

        await rendered.Find("#model-yaml-tab").ClickAsync(new());

        var yaml = rendered.Find("[data-testid='model-yaml'] textarea");
        Assert.IsTrue(yaml.HasAttribute("readonly"));
        Assert.IsTrue(yaml.TextContent.Contains($"name: {FakeModelsClient.ResourceName}", StringComparison.Ordinal));
        Assert.IsTrue(yaml.TextContent.Contains("externalId: gpt-5", StringComparison.Ordinal));
        Assert.IsTrue(yaml.TextContent.Contains("support: unknown", StringComparison.Ordinal));
        Assert.IsTrue(yaml.TextContent.Contains("maxOutputTokens: 16000", StringComparison.Ordinal));
        Assert.IsFalse(yaml.TextContent.Contains("maxOutputTokens: 8000", StringComparison.Ordinal));
        Assert.IsFalse(yaml.TextContent.Contains("specificationOverride", StringComparison.Ordinal));

        await rendered.Find("[data-testid='copy-model-yaml']").ClickAsync(new());

        var invocation = context.JSInterop.Invocations.Single(value => value.Identifier == "navigator.clipboard.writeText");
        Assert.IsTrue(invocation.Arguments[0]?.ToString()?.Contains("kind: Model", StringComparison.Ordinal) == true);
        Assert.AreEqual("Copied", rendered.Find("[data-testid='copy-model-yaml']").TextContent.Trim());
    }

    [TestMethod]
    public void DisappearedModelRemainsInspectableWithActionableExplanation()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddSingleton<IModelsClient>(new FakeModelsClient(ModelObservationState.Missing));
        context.Services.AddSingleton<IModelProvidersClient>(new FakeProvidersClient(includeEffectiveModel: false));
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/models/{FakeModelsClient.ResourceName}?namespace=shared.models");

        var rendered = context.Render<ModelDetails>(parameters => parameters
            .Add(component => component.Name, FakeModelsClient.ResourceName));

        Assert.IsTrue(rendered.Find("[data-testid='resource-model-details']").TextContent.Contains("Disappeared", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Find(".model-observation-warning").TextContent.Contains("last observation remains available", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Effective diagnostics are unavailable", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("gpt-5", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FrenchCatalogKeepsEquivalentModelDetailMeaning()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var localizer = context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<ModelDetailsStrings>>();

        Assert.AreEqual("Vue d’ensemble", localizer["Tab.Overview"].Value);
        Assert.AreEqual("Surcharge du fournisseur", localizer["ProviderOverride"].Value);
        Assert.AreEqual("Observé · lecture seule", localizer["ObservedReadOnly"].Value);
        Assert.AreEqual("Copier le YAML", localizer["CopyYaml"].Value);
    }

    private sealed class FakeModelsClient(ModelObservationState state) : IModelsClient
    {
        public const string ResourceName = "foundry.gpt-5-1234567890abcdef";
        public ResourceNamespace RequestedNamespace { get; private set; }
        public string? RequestedName { get; private set; }

        public Task<ResourceSnapshot<ModelResource>> GetModelAsync(ResourceNamespace @namespace, string modelName, CancellationToken cancellationToken)
        {
            RequestedNamespace = @namespace;
            RequestedName = modelName;
            var observedAt = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
            return Task.FromResult(new ResourceSnapshot<ModelResource>(new ModelResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ModelResourceKinds.Model,
                Metadata = new ResourceMetadata { Name = ResourceName, Namespace = @namespace },
                ScopeRef = ResourceScopeRef.Tenant(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
                Generation = 3,
                Definition = new ModelProperties
                {
                    DisplayName = "GPT-5",
                    Provider = new ResourceReference("foundry", @namespace: @namespace),
                    ProviderUid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    ExternalId = "gpt-5",
                    ProviderStatus = state == ModelObservationState.Available ? "available" : "unavailable",
                    Identity = new ModelIdentity { Publisher = "OpenAI", Model = "GPT-5", Version = "2026-09" },
                    Specification = ObservedSpecification(),
                    Observation = new ModelObservation
                    {
                        State = state,
                        FirstObservedAt = observedAt.AddDays(-7),
                        LastObservedAt = observedAt,
                        LastAttemptedAt = state == ModelObservationState.Available ? observedAt : observedAt.AddHours(2)
                    }
                }
            }, "\"model-etag\""));
        }
    }

    private sealed class FakeProvidersClient(bool includeEffectiveModel) : IModelProvidersClient
    {
        public ResourceNamespace RequestedNamespace { get; private set; }
        public string? RequestedProvider { get; private set; }

        public Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken)
        {
            RequestedNamespace = @namespace;
            RequestedProvider = providerName;
            if (!includeEffectiveModel) return Task.FromResult<IReadOnlyList<AvailableModelResponse>>([]);
            var @override = new ModelSpecificationOverride
            {
                Features = new ModelFeatureOverrides { Streaming = new ModelStreamingFeatureOverride { Support = ModelFeatureSupport.Native } },
                Limits = new ModelLimitOverrides { MaxOutputTokens = 8_000 }
            };
            return Task.FromResult<IReadOnlyList<AvailableModelResponse>>([new(
                "gpt-5",
                "GPT-5",
                "available",
                EffectiveModelSpecificationResolver.Resolve(ObservedSpecification(), @override),
                new ModelIdentity { Publisher = "OpenAI", Model = "GPT-5", Version = "2026-09" },
                ObservedSpecification(),
                @override,
                FakeModelsClient.ResourceName)]);
        }

        public Task<IReadOnlyList<ModelProviderResponse>> GetModelProvidersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProviderResource>> GetModelProviderAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProviderResource>> CreateModelProviderAsync(CreateModelProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProviderResource>> UpdateModelProviderAsync(string providerName, PutModelProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteModelProviderAsync(string providerName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProviderUsagesResponse> GetModelProviderUsagesAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProviderStatusResponse> GetProviderStatusAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProviderStatusResponse> TestProviderAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static ModelSpecification ObservedSpecification() => new()
    {
        Input = [ModelContentType.Text, ModelContentType.Image],
        Output = [ModelContentType.Text],
        Features = new ModelFeatureSpecifications
        {
            Streaming = new ModelStreamingFeatureSpecification { Support = ModelFeatureSupport.Unknown },
            Tools = new ModelToolsFeatureSpecification { Support = ModelFeatureSupport.Native, Modes = new Dictionary<ModelToolMode, ModelToolModeSpecification> { [ModelToolMode.Function] = new() } },
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
