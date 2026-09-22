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
    public async Task ReadOnlyProviderApisExposeConfiguredProviderAndUnavailableDiscovery()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var providers = await client.GetFromJsonAsync<ValueResponse<ModelProviderResponse>>("/api/modelproviders");
        var provider = providers!.Value.Single(value => value.Name == "ollama-local");
        var status = await client.GetFromJsonAsync<ModelProviderStatusResponse>("/api/modelproviders/ollama-local/status");
        using var models = await client.GetAsync("/api/modelproviders/ollama-local/models");

        Assert.AreEqual("aspire", provider.Properties.RegistrationSource);
        Assert.AreEqual("unavailable", status!.Status);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, models.StatusCode);
        Assert.AreEqual("application/problem+json", models.Content.Headers.ContentType?.MediaType);
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

}

