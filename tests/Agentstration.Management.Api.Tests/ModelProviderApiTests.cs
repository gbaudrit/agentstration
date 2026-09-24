using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Api.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.ModelProviders;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ModelProviderApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task ReadOnlyProviderApisExposeConfiguredProviderAndPersistedInventory()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var providers = await client.GetFromJsonAsync<ValueResponse<ModelProviderResponse>>("/api/modelproviders");
        var provider = providers!.Value.Single(value => value.Name == "ollama-local");
        var status = await client.GetFromJsonAsync<ModelProviderStatusResponse>("/api/modelproviders/ollama-local/status");
        using var models = await client.GetAsync("/api/modelproviders/ollama-local/models");

        Assert.AreEqual("aspire", provider.Properties.RegistrationSource);
        Assert.AreEqual("unavailable", status!.Status);
        Assert.AreEqual(HttpStatusCode.OK, models.StatusCode);
        var inventory = await models.Content.ReadFromJsonAsync<ValueResponse<AvailableModelResponse>>();
        Assert.AreEqual(0, inventory?.Value.Count);
    }

    [TestMethod]
    public async Task ExplicitRefreshReconcilesGovernedModelsAndRetainsLastValidObservation()
    {
        var observed = new ModelSpecification
        {
            Input = [ModelContentType.Text],
            Output = [ModelContentType.Text],
            Features = new ModelFeatureSpecifications
            {
                Streaming = new() { Support = ModelFeatureSupport.Native }
            },
            Limits = new ModelLimits { ContextTokens = 8192 }
        };
        var changed = observed with { Limits = new ModelLimits { ContextTokens = 16384 } };
        var discovery = new SequenceModelDiscovery(
            () => [new DiscoveredModel("shared/model", "Shared model", "available", observed, new ModelIdentity { Publisher = "test", Model = "shared", Version = "1" })],
            () => [new DiscoveredModel("shared/model", "Shared model", "available", observed, new ModelIdentity { Publisher = "test", Model = "shared", Version = "1" })],
            () => [],
            () => [new DiscoveredModel("shared/model", "Shared model v2", "available", changed, new ModelIdentity { Publisher = "test", Model = "shared", Version = "2" })],
            () => throw new InvalidOperationException("observation failed"));
        await using var factory = DiscoveryFactory(discovery);
        using var client = factory.CreateClient();

        var before = await client.GetFromJsonAsync<ValueResponse<AvailableModelResponse>>("/api/modelproviders/ollama-local/models");
        Assert.AreEqual(0, before?.Value.Count);
        Assert.AreEqual(0, discovery.ListCalls);

        var created = await RefreshAsync(client);
        Assert.AreEqual(new ModelDiscoveryDiffResponse(1, 0, 0, 0, 0, 1), created);
        Assert.AreEqual(1, discovery.ListCalls);

        var provider = await client.GetFromJsonAsync<ModelProviderResource>("/api/modelproviders/ollama-local");
        var models = await client.GetFromJsonAsync<ValueResponse<ModelResource>>("/api/models");
        var model = models!.Value.Single();
        Assert.AreEqual(provider!.Uid, model.Definition.ProviderUid);
        Assert.AreEqual(provider.Namespace, model.Namespace);
        Assert.AreEqual(provider.ScopeRef, model.ScopeRef);
        Assert.AreEqual("shared/model", model.Definition.ExternalId);
        Assert.AreEqual(ModelObservationState.Available, model.Definition.Observation.State);
        Assert.AreEqual(8192, model.Definition.Specification.Limits.ContextTokens);
        var providerInventory = await client.GetFromJsonAsync<ValueResponse<AvailableModelResponse>>("/api/modelproviders/ollama-local/models");
        Assert.AreEqual(model.Name, providerInventory!.Value.Single().ResourceName);

        var unchanged = await RefreshAsync(client);
        Assert.AreEqual(new ModelDiscoveryDiffResponse(0, 0, 1, 0, 0, 1), unchanged);

        var missing = await RefreshAsync(client);
        Assert.AreEqual(new ModelDiscoveryDiffResponse(0, 0, 0, 1, 0, 0), missing);
        model = (await client.GetFromJsonAsync<ValueResponse<ModelResource>>("/api/models"))!.Value.Single();
        Assert.AreEqual(ModelObservationState.Missing, model.Definition.Observation.State);
        Assert.AreEqual(8192, model.Definition.Specification.Limits.ContextTokens);

        var reappeared = await RefreshAsync(client);
        Assert.AreEqual(new ModelDiscoveryDiffResponse(0, 0, 0, 0, 1, 1), reappeared);
        model = (await client.GetFromJsonAsync<ValueResponse<ModelResource>>("/api/models"))!.Value.Single();
        Assert.AreEqual(ModelObservationState.Available, model.Definition.Observation.State);
        Assert.AreEqual("Shared model v2", model.Definition.DisplayName);
        Assert.AreEqual(16384, model.Definition.Specification.Limits.ContextTokens);

        using var failed = await client.PostAsync("/api/modelproviders/ollama-local/models/refresh", null);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        model = (await client.GetFromJsonAsync<ValueResponse<ModelResource>>("/api/models"))!.Value.Single();
        Assert.AreEqual(ModelObservationState.Failed, model.Definition.Observation.State);
        Assert.AreEqual("discovery_failed", model.Definition.Observation.ErrorCode);
        Assert.AreEqual(16384, model.Definition.Specification.Limits.ContextTokens);

        using var directCreate = await client.PostAsJsonAsync("/api/models", model);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, directCreate.StatusCode);
    }

    [TestMethod]
    public async Task ProvidersWithTheSameExternalModelRemainIsolatedByProviderUid()
    {
        var discovery = new PerProviderModelDiscovery();
        await using var factory = DiscoveryFactory(discovery);
        using var client = factory.CreateClient();
        var provider = await client.GetFromJsonAsync<ModelProviderResource>("/api/modelproviders/ollama-local");
        var tenant = provider!.ScopeRef!.Value;

        using var extension = await client.PostAsJsonAsync("/api/extensionregistrations",
            new CreateExtensionRegistrationRequest("second-model-extension", new()
            {
                DisplayName = "Second model extension",
                Endpoint = new Uri("http://127.0.0.1:6788"),
                ExpectedExtensionId = "second-model-extension"
            }, ScopeRef: tenant));
        Assert.AreEqual(HttpStatusCode.Created, extension.StatusCode);
        using var createdProvider = await client.PostAsJsonAsync("/api/modelproviders", new CreateModelProviderRequest(
            "second-provider",
            new ModelProviderProperties
            {
                DisplayName = "Second provider",
                Extension = new ResourceReference("second-model-extension", tenant),
                ContributionId = "second"
            },
            ScopeRef: tenant));
        Assert.AreEqual(HttpStatusCode.Created, createdProvider.StatusCode);

        _ = await RefreshAsync(client, "ollama-local");
        _ = await RefreshAsync(client, "second-provider");
        var models = (await client.GetFromJsonAsync<ValueResponse<ModelResource>>("/api/models"))!.Value;
        Assert.AreEqual(2, models.Count);
        Assert.IsTrue(models.All(value => value.Definition.ExternalId == "same-id"));
        Assert.AreEqual(2, models.Select(value => value.Definition.ProviderUid).Distinct().Count());
        Assert.AreEqual(2, models.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count());

        discovery.MissingProvider = "ollama-local";
        _ = await RefreshAsync(client, "ollama-local");
        models = (await client.GetFromJsonAsync<ValueResponse<ModelResource>>("/api/models"))!.Value;
        Assert.AreEqual(ModelObservationState.Missing,
            models.Single(value => value.Definition.Provider.Name == "ollama-local").Definition.Observation.State);
        Assert.AreEqual(ModelObservationState.Available,
            models.Single(value => value.Definition.Provider.Name == "second-provider").Definition.Observation.State);
    }

    [TestMethod]
    public async Task ProviderOwnedSpecificationOverridesAreIsolatedEffectiveAndConcurrencyProtected()
    {
        var discovery = new SequenceModelDiscovery(
            () =>
            [
                Model("omitted-tools", ModelFeatureSupport.Unknown),
                Model("explicitly-unsupported", ModelFeatureSupport.Unsupported)
            ],
            () =>
            [
                Model("omitted-tools", ModelFeatureSupport.Unsupported),
                Model("explicitly-unsupported", ModelFeatureSupport.Unsupported)
            ]);
        await using var factory = DiscoveryFactory(discovery);
        using var client = factory.CreateClient();
        _ = await RefreshAsync(client);

        using var providerResponse = await client.GetAsync("/api/modelproviders/ollama-local");
        providerResponse.EnsureSuccessStatusCode();
        var provider = await providerResponse.Content.ReadFromJsonAsync<ModelProviderResource>()
            ?? throw new InvalidOperationException("The provider response was empty.");
        var originalEtag = providerResponse.Headers.ETag?.ToString()
            ?? throw new InvalidOperationException("The provider ETag was missing.");
        var overrideValue = new ModelSpecificationOverride
        {
            Features = new ModelFeatureOverrides
            {
                Tools = new() { Support = ModelFeatureSupport.Native }
            }
        };
        var properties = provider.Definition with
        {
            SpecificationOverrides = new Dictionary<string, ModelSpecificationOverride>(StringComparer.Ordinal)
            {
                ["omitted-tools"] = overrideValue,
                ["explicitly-unsupported"] = overrideValue
            }
        };

        using var put = new HttpRequestMessage(HttpMethod.Put, "/api/modelproviders/ollama-local")
        {
            Content = JsonContent.Create(new PutModelProviderRequest(properties))
        };
        put.Headers.IfMatch.ParseAdd(originalEtag);
        using var updatedResponse = await client.SendAsync(put);
        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode);
        var updated = await updatedResponse.Content.ReadFromJsonAsync<ModelProviderResource>();
        Assert.AreEqual(2, updated?.Definition.SpecificationOverrides.Count);

        var effective = await client.GetFromJsonAsync<ValueResponse<AvailableModelResponse>>(
            "/api/modelproviders/ollama-local/models");
        Assert.AreEqual(ModelFeatureSupport.Native,
            effective!.Value.Single(value => value.Name == "omitted-tools").Specification.Features.Tools?.Support);
        Assert.AreEqual(ModelFeatureSupport.Unknown,
            effective.Value.Single(value => value.Name == "omitted-tools").ObservedSpecification?.Features.Tools?.Support);
        Assert.AreEqual(ModelFeatureSupport.Native,
            effective.Value.Single(value => value.Name == "omitted-tools").SpecificationOverride?.Features.Tools?.Support);
        Assert.AreEqual(ModelFeatureSupport.Unsupported,
            effective.Value.Single(value => value.Name == "explicitly-unsupported").Specification.Features.Tools?.Support);
        var observed = await client.GetFromJsonAsync<ValueResponse<ModelResource>>("/api/models");
        Assert.AreEqual(ModelFeatureSupport.Unknown,
            observed!.Value.Single(value => value.Definition.ExternalId == "omitted-tools").Definition.Specification.Features.Tools?.Support);

        var discoveryChange = await RefreshAsync(client);
        Assert.AreEqual(1, discoveryChange.Updated);
        Assert.AreEqual(1, discoveryChange.Unchanged);
        effective = await client.GetFromJsonAsync<ValueResponse<AvailableModelResponse>>(
            "/api/modelproviders/ollama-local/models");
        Assert.AreEqual(ModelFeatureSupport.Unsupported,
            effective!.Value.Single(value => value.Name == "omitted-tools").Specification.Features.Tools?.Support);
        Assert.IsNotNull(effective.Value.Single(value => value.Name == "omitted-tools").SpecificationOverride);

        using var stalePut = new HttpRequestMessage(HttpMethod.Put, "/api/modelproviders/ollama-local")
        {
            Content = JsonContent.Create(new PutModelProviderRequest(properties with { SpecificationOverrides = new Dictionary<string, ModelSpecificationOverride>() }))
        };
        stalePut.Headers.IfMatch.ParseAdd(originalEtag);
        using var staleResponse = await client.SendAsync(stalePut);
        Assert.AreEqual(HttpStatusCode.Conflict, staleResponse.StatusCode);

        var currentEtag = updatedResponse.Headers.ETag?.ToString()
            ?? throw new InvalidOperationException("The updated provider ETag was missing.");
        using var remove = new HttpRequestMessage(HttpMethod.Put, "/api/modelproviders/ollama-local")
        {
            Content = JsonContent.Create(new PutModelProviderRequest(properties with
            {
                SpecificationOverrides = new Dictionary<string, ModelSpecificationOverride>()
            }))
        };
        remove.Headers.IfMatch.ParseAdd(currentEtag);
        using var removedResponse = await client.SendAsync(remove);
        Assert.AreEqual(HttpStatusCode.OK, removedResponse.StatusCode);
        effective = await client.GetFromJsonAsync<ValueResponse<AvailableModelResponse>>(
            "/api/modelproviders/ollama-local/models");
        Assert.AreEqual(ModelFeatureSupport.Unsupported,
            effective!.Value.Single(value => value.Name == "omitted-tools").Specification.Features.Tools?.Support);

        var removedEtag = removedResponse.Headers.ETag?.ToString()
            ?? throw new InvalidOperationException("The removal ETag was missing.");
        using var invalid = new HttpRequestMessage(HttpMethod.Put, "/api/modelproviders/ollama-local")
        {
            Content = JsonContent.Create(new PutModelProviderRequest(properties with
            {
                SpecificationOverrides = new Dictionary<string, ModelSpecificationOverride>
                {
                    ["not-discovered"] = overrideValue
                }
            }))
        };
        invalid.Headers.IfMatch.ParseAdd(removedEtag);
        using var invalidResponse = await client.SendAsync(invalid);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidResponse.StatusCode);

        static DiscoveredModel Model(string name, ModelFeatureSupport support) => new(
            name,
            name,
            "available",
            new ModelSpecification
            {
                Features = new ModelFeatureSpecifications
                {
                    Tools = new() { Support = support }
                }
            });
    }

    [TestMethod]
    public async Task ProviderValidationAndDeletionProtectionReturnProblemDetails()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var invalid = new CreateModelProviderRequest(
            "invalid-provider",
            new ModelProviderProperties
            {
                DisplayName = "Invalid",
                Extension = new ResourceReference("missing-extension"),
                ContributionId = "ollama"
            });

        using var invalidResponse = await client.PostAsJsonAsync("/api/modelproviders", invalid);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidResponse.StatusCode);
        Assert.AreEqual("application/problem+json", invalidResponse.Content.Headers.ContentType?.MediaType);

        var usages = await client.GetFromJsonAsync<ModelProviderUsagesResponse>("/api/modelproviders/ollama-local/usages");
        Assert.IsTrue(usages!.Count >= 1);
        Assert.IsTrue(usages.Value.Any(usage => usage.ResourceType == ModelResourceKinds.ModelProfile));
        using var deleted = await client.DeleteAsync("/api/modelproviders/ollama-local");
        Assert.AreEqual(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.AreEqual("application/problem+json", deleted.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public void LegacyProviderOptionsRemainReadableForExplicitMigration()
    {
        var options = JsonSerializer.Deserialize<VersionedExtensionOptions>("""{"minP":0.05,"repeatPenalty":1.1}""");

        Assert.IsNotNull(options);
        Assert.AreEqual(string.Empty, options.OptionSet);
        Assert.IsNotNull(options.LegacyValues);
        Assert.IsTrue(options.LegacyValues.ContainsKey("minP"));
    }

    [TestMethod]
    public async Task ProviderValueBindingsValidateExactParametersAndProtectTheirDeletion()
    {
        var requirements = new AepValueRequirement[]
        {
            new(AepContributionKinds.ModelProvider, "discovered", "endpoint", true, AepValueType.Text, AllowedValues:
            [
                JsonSerializer.SerializeToElement("https://provider.test"),
                JsonSerializer.SerializeToElement("https://backup.test")
            ]),
            new(AepContributionKinds.ModelProvider, "discovered", "credential", false,
                AepValueType.Text, AepValueProtection.Secured)
        };
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExtensionInspector>();
            services.AddSingleton<IExtensionInspector>(new ConfiguredEndpointInspector(requirements));
        }));
        using var client = factory.CreateClient();
        var targets = (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={ParameterResourceKinds.Parameter}"))!;
        var tenant = targets.Single(value => value.Kind == ResourceScopeKind.Tenant).ScopeRef;
        using var extension = await client.PostAsJsonAsync("/api/extensionregistrations",
            new CreateExtensionRegistrationRequest("binding-extension", new()
            {
                DisplayName = "Binding extension",
                Endpoint = new Uri("http://127.0.0.1:6789"),
                ExpectedExtensionId = "binding-extension"
            }, ScopeRef: tenant));
        Assert.AreEqual(HttpStatusCode.Created, extension.StatusCode);

        using var parameter = await client.PostAsJsonAsync("/api/parameters", new CreateParameterRequest(
            "provider-endpoint",
            new ParameterProperties
            {
                DisplayName = "Provider endpoint",
                ValueType = ParameterValueType.Text,
                Value = JsonSerializer.SerializeToElement("https://provider.test")
            }, tenant));
        Assert.AreEqual(HttpStatusCode.Created, parameter.StatusCode);
        using var disallowedParameter = await client.PostAsJsonAsync("/api/parameters", new CreateParameterRequest(
            "disallowed-endpoint",
            new ParameterProperties
            {
                DisplayName = "Disallowed endpoint",
                ValueType = ParameterValueType.Text,
                Value = JsonSerializer.SerializeToElement("https://other.test")
            }, tenant));
        Assert.AreEqual(HttpStatusCode.Created, disallowedParameter.StatusCode);
        var reference = new ParameterReference(
            ResourceAddress.Create(ResourceNamespace.Default, ParameterResourceKinds.Parameter, "provider-endpoint"), tenant);
        ModelProviderProperties Properties(string name, IReadOnlyList<ModelProviderValueBinding> bindings) => new()
        {
            DisplayName = name,
            Extension = new ResourceReference("binding-extension", tenant),
            ContributionId = "discovered",
            ValueBindings = bindings
        };

        using var created = await client.PostAsJsonAsync("/api/modelproviders", new CreateModelProviderRequest(
            "bound-provider", Properties("Bound provider", [ModelProviderValueBinding.FromParameter("endpoint", reference)]), ScopeRef: tenant));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<ModelProviderResource>();
        Assert.AreEqual(reference, saved?.Definition.ValueBindings.Single().Parameter);
        Assert.AreEqual("endpoint=Parameter:[REDACTED]", saved?.Definition.ValueBindings.Single().ToString());

        var invalidCases = new[]
        {
            Properties("Unknown", [ModelProviderValueBinding.FromParameter("unknown", reference)]),
            Properties("Duplicate", [
                ModelProviderValueBinding.FromParameter("endpoint", reference),
                ModelProviderValueBinding.FromParameter("endpoint", reference)]),
            Properties("Wrong protection", [ModelProviderValueBinding.FromParameter("credential", reference)]),
            Properties("Disallowed", [ModelProviderValueBinding.FromParameter("endpoint", reference with
            {
                Address = ResourceAddress.Create(ResourceNamespace.Default, ParameterResourceKinds.Parameter, "disallowed-endpoint")
            })]),
            Properties("Missing", [ModelProviderValueBinding.FromParameter("endpoint", reference with
            {
                Address = ResourceAddress.Create(ResourceNamespace.Default, ParameterResourceKinds.Parameter, "missing")
            })])
        };
        for (var index = 0; index < invalidCases.Length; index++)
        {
            using var invalid = await client.PostAsJsonAsync("/api/modelproviders", new CreateModelProviderRequest(
                $"invalid-binding-{index}", invalidCases[index], ScopeRef: tenant));
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            Assert.IsFalse((await invalid.Content.ReadAsStringAsync()).Contains("https://other.test", StringComparison.Ordinal));
        }

        using var delete = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/parameters/provider-endpoint?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        delete.Headers.IfMatch.ParseAdd(parameter.Headers.ETag!.ToString());
        using var protectedDelete = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.Conflict, protectedDelete.StatusCode);
    }

    private static Task<ModelDiscoveryDiffResponse> RefreshAsync(HttpClient client) => RefreshAsync(client, "ollama-local");

    private static async Task<ModelDiscoveryDiffResponse> RefreshAsync(HttpClient client, string providerName)
    {
        using var response = await client.PostAsync($"/api/modelproviders/{providerName}/models/refresh", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ModelDiscoveryDiffResponse>()
            ?? throw new InvalidOperationException("The refresh response was empty.");
    }

    private sealed class SequenceModelDiscovery(params Func<IReadOnlyList<DiscoveredModel>>[] observations) : IModelProviderDiscovery
    {
        private int index;
        public int ListCalls { get; private set; }
        public string ProviderType => "test";
        public bool CanHandle(string providerType) => true;
        public ValueTask<ModelProviderHealth> GetHealthAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ModelProviderHealth("available"));
        public ValueTask<IReadOnlyList<DiscoveredModel>> ListModelsAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls++;
            var observation = observations[Math.Min(index++, observations.Length - 1)];
            return ValueTask.FromResult(observation());
        }
    }

    private sealed class PerProviderModelDiscovery : IModelProviderDiscovery
    {
        public string? MissingProvider { get; set; }
        public string ProviderType => "test";
        public bool CanHandle(string providerType) => true;
        public ValueTask<ModelProviderHealth> GetHealthAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ModelProviderHealth("available"));
        public ValueTask<IReadOnlyList<DiscoveredModel>> ListModelsAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DiscoveredModel>>(
                string.Equals(provider.Name, MissingProvider, StringComparison.Ordinal)
                    ? []
                    : [new DiscoveredModel("same-id", $"{provider.Name} model", "available", new ModelSpecification())]);
    }

}

