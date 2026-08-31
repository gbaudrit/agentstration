using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class ModelManagementApiTests
{
    [TestMethod]
    public async Task SeededOllamaProviderUsesAepExtensionEndpointInsteadOfNativeOllamaEndpoint()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI:Provider", "Managed");
            builder.UseSetting("AI:Endpoint", "http://localhost:11434");
            builder.UseSetting("Agentstration:Extensions:Agentstration.Extensions.Ollama:Endpoint", "http://localhost:5265");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });
        using var client = factory.CreateClient();

        var provider = await client.GetFromJsonAsync<ModelProviderResource>("/api/modelproviders/ollama-local");
        var extension = await client.GetFromJsonAsync<ExtensionRegistrationResource>("/api/extensionregistrations/ollama-extension");

        Assert.IsNotNull(provider);
        Assert.AreEqual("ollama-extension", provider.Definition.Extension.Name);
        Assert.AreEqual(new Uri("http://localhost:5265/"), extension!.Definition.Endpoint);
        Assert.AreNotEqual(new Uri("http://localhost:11434"), extension.Definition.Endpoint);
    }

    [TestMethod]
    public async Task SeededLlamaCppProviderUsesItsAepExtensionEndpoint()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI:Provider", "Managed");
            builder.UseSetting("Agentstration:Extensions:Agentstration.Extensions.LlamaCpp:Endpoint", "http://localhost:5275");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });
        using var client = factory.CreateClient();

        var provider = await client.GetFromJsonAsync<ModelProviderResource>("/api/modelproviders/llama-cpp-local");
        var extension = await client.GetFromJsonAsync<ExtensionRegistrationResource>("/api/extensionregistrations/llama-cpp-extension");

        Assert.IsNotNull(provider);
        Assert.AreEqual("llamacpp", provider.Definition.ContributionId);
        Assert.AreEqual("llama-cpp-extension", provider.Definition.Extension.Name);
        Assert.AreEqual(new Uri("http://localhost:5275/"), extension!.Definition.Endpoint);
    }

    [TestMethod]
    public async Task SeededLocalAiProviderUsesItsAepExtensionEndpoint()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI:Provider", "Managed");
            builder.UseSetting("Agentstration:Extensions:Agentstration.Extensions.LocalAI:Endpoint", "http://localhost:5285");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });
        using var client = factory.CreateClient();

        var provider = await client.GetFromJsonAsync<ModelProviderResource>("/api/modelproviders/localai-local");
        var extension = await client.GetFromJsonAsync<ExtensionRegistrationResource>("/api/extensionregistrations/localai-extension");

        Assert.IsNotNull(provider);
        Assert.AreEqual("localai", provider.Definition.ContributionId);
        Assert.AreEqual("localai-extension", provider.Definition.Extension.Name);
        Assert.AreEqual(new Uri("http://localhost:5285/"), extension!.Definition.Endpoint);
    }

    [TestMethod]
    public async Task ExtensionsApiReportsConfiguredProvidersWithoutRequiringThemToBeOnline()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<ValueResponse<ExtensionResponse>>("/api/extensions");
        var extension = response!.Value.Single(value => value.RegistrationName == "ollama-extension");

        Assert.AreEqual("unavailable", extension.Status);
        Assert.AreEqual("http://127.0.0.1:1/", extension.Endpoint.AbsoluteUri);
        Assert.IsEmpty(extension.OptionSets);
        Assert.IsTrue(extension.Providers.Any(value => value.Name == "ollama-local" && value.ContributionId == "ollama"));
    }

    [TestMethod]
    public async Task ExtensionsApiDiscoversConfiguredEndpointAndRefreshesOnCommand()
    {
        await using var factory = Factory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agentstration:Extensions:extension.discovered:Endpoint", "http://127.0.0.1:5678");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IExtensionInspector>();
                services.AddSingleton<IExtensionInspector, ConfiguredEndpointInspector>();
            });
        });
        using var client = factory.CreateClient();

        using var discoveryResponse = await client.PostAsync("/api/extensions/discover", null);
        Assert.AreEqual(HttpStatusCode.OK, discoveryResponse.StatusCode);
        var discovery = await discoveryResponse.Content.ReadFromJsonAsync<ExtensionDiscoveryResponse>();
        Assert.IsNotNull(discovery);
        Assert.IsGreaterThanOrEqualTo(1, discovery.Sources);

        var response = await client.GetFromJsonAsync<ValueResponse<ExtensionResponse>>("/api/extensions");
        var extension = response!.Value.Single(value => value.RegistrationName == "extension-discovered");

        Assert.AreEqual("configuration", extension.DiscoverySource);
        Assert.AreEqual("extension.discovered", extension.Extension!.Id);
        Assert.AreEqual("http://127.0.0.1:5678/", extension.Endpoint.AbsoluteUri);
        var contribution = extension.Contributions.Single();
        Assert.AreEqual("model-provider", contribution.Kind);
        Assert.AreEqual("discovered", contribution.Id);
    }

    [TestMethod]
    public async Task ExtensionRegistrationCrudUsesETagAndControlsDiscovery()
    {
        await using var factory = Factory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IExtensionInspector>();
                services.AddSingleton<IExtensionInspector, ConfiguredEndpointInspector>();
            }));
        using var client = factory.CreateClient();
        var properties = new ExtensionRegistrationProperties
        {
            DisplayName = "Registered extension",
            Endpoint = new("http://127.0.0.1:6789"),
            Enabled = true
        };

        using var createdResponse = await client.PostAsJsonAsync(
            "/api/extensionregistrations",
            new CreateExtensionRegistrationRequest("registered-extension", properties));

        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var created = await createdResponse.Content.ReadFromJsonAsync<ExtensionRegistrationResource>();
        Assert.IsNotNull(created);
        Assert.AreEqual("http://127.0.0.1:6789/", created.Definition.Endpoint.AbsoluteUri);
        var registrations = await client.GetFromJsonAsync<ValueResponse<ExtensionRegistrationResource>>("/api/extensionregistrations");
        Assert.IsTrue(registrations!.Value.Any(value => value.Name == created.Name));
        var discovered = await client.GetFromJsonAsync<ValueResponse<ExtensionResponse>>("/api/extensions");
        Assert.AreEqual("manual", discovered!.Value.Single(value => value.RegistrationName == created.Name).DiscoverySource);

        using var disable = new HttpRequestMessage(HttpMethod.Put, $"/api/extensionregistrations/{created.Name}")
        {
            Content = JsonContent.Create(new PutExtensionRegistrationRequest(created.Definition with { Enabled = false }))
        };
        disable.Headers.IfMatch.Add(createdResponse.Headers.ETag);
        using var disabledResponse = await client.SendAsync(disable);
        Assert.AreEqual(HttpStatusCode.OK, disabledResponse.StatusCode);
        Assert.IsNotNull(disabledResponse.Headers.ETag);
        discovered = await client.GetFromJsonAsync<ValueResponse<ExtensionResponse>>("/api/extensions");
        Assert.AreEqual("disabled", discovered!.Value.Single(value => value.RegistrationName == created.Name).Status);

        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/extensionregistrations/{created.Name}")
        {
            Content = JsonContent.Create(new PutExtensionRegistrationRequest(created.Definition))
        };
        stale.Headers.IfMatch.Add(createdResponse.Headers.ETag);
        using var staleResponse = await client.SendAsync(stale);
        Assert.AreEqual(HttpStatusCode.Conflict, staleResponse.StatusCode);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/extensionregistrations/{created.Name}");
        delete.Headers.IfMatch.Add(disabledResponse.Headers.ETag);
        using var deletedResponse = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deletedResponse.StatusCode);
    }

    [TestMethod]
    public async Task ExtensionRegistrationRejectsUnsafeAndDuplicateEndpoints()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var unsafeProperties = new ExtensionRegistrationProperties
        {
            DisplayName = "Unsafe",
            Endpoint = new("https://user:password@example.test/aep")
        };

        using var unsafeResponse = await client.PostAsJsonAsync(
            "/api/extensionregistrations",
            new CreateExtensionRegistrationRequest("unsafe-extension", unsafeProperties));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, unsafeResponse.StatusCode);

        var endpoint = new ExtensionRegistrationProperties { DisplayName = "First", Endpoint = new("http://127.0.0.1:6790") };
        using var first = await client.PostAsJsonAsync(
            "/api/extensionregistrations",
            new CreateExtensionRegistrationRequest("first-extension", endpoint, "extensions"));
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        using var duplicate = await client.PostAsJsonAsync(
            "/api/extensionregistrations",
            new CreateExtensionRegistrationRequest("duplicate-extension", endpoint with { DisplayName = "Duplicate" }, "extensions"));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
    }

    [TestMethod]
    public async Task ExtensionRegistrationDeletionIsBlockedWhileAProviderReferencesIt()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        using var registrationResponse = await client.PostAsJsonAsync(
            "/api/extensionregistrations",
            new CreateExtensionRegistrationRequest("bound-extension", new ExtensionRegistrationProperties
            {
                DisplayName = "Bound extension",
                Endpoint = new Uri("http://127.0.0.1:6792")
            }));
        Assert.AreEqual(HttpStatusCode.Created, registrationResponse.StatusCode);
        using var providerResponse = await client.PostAsJsonAsync(
            "/api/modelproviders",
            new CreateModelProviderRequest("bound-provider", new ModelProviderProperties
            {
                DisplayName = "Bound provider",
                Extension = new ResourceReference("bound-extension"),
                ContributionId = "ollama"
            }));
        Assert.AreEqual(HttpStatusCode.Created, providerResponse.StatusCode);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/extensionregistrations/bound-extension");
        delete.Headers.IfMatch.Add(registrationResponse.Headers.ETag!);
        using var response = await client.SendAsync(delete);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task ProviderCrudUsesETagAndPersistsItsExtensionBinding()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        using var registrationResponse = await client.PostAsJsonAsync(
            "/api/extensionregistrations",
            new CreateExtensionRegistrationRequest("ollama-lab-extension", new ExtensionRegistrationProperties
            {
                DisplayName = "Ollama lab extension",
                Endpoint = new Uri("http://127.0.0.1:11435")
            }));
        Assert.AreEqual(HttpStatusCode.Created, registrationResponse.StatusCode);
        var properties = new ModelProviderProperties
        {
            DisplayName = "Ollama lab",
            Extension = new ResourceReference("ollama-lab-extension"),
            ContributionId = "ollama"
        };

        using var createdResponse = await client.PostAsJsonAsync(
            "/api/modelproviders",
            new CreateModelProviderRequest("ollama-lab", properties));
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var created = await createdResponse.Content.ReadFromJsonAsync<ModelProviderResource>();
        Assert.IsNotNull(created);
        Assert.AreEqual("ollama-lab-extension", created.Definition.Extension.Name);
        Assert.AreEqual("ollama", created.Definition.ContributionId);

        using var getResponse = await client.GetAsync("/api/modelproviders/ollama-lab");
        Assert.AreEqual(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.AreEqual(createdResponse.Headers.ETag, getResponse.Headers.ETag);

        var updatedProperties = created.Definition with
        {
            DisplayName = "Ollama workstation"
        };
        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/modelproviders/ollama-lab")
        {
            Content = JsonContent.Create(new PutModelProviderRequest(updatedProperties))
        };
        update.Headers.IfMatch.Add(createdResponse.Headers.ETag);
        using var updatedResponse = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode);
        Assert.AreNotEqual(createdResponse.Headers.ETag, updatedResponse.Headers.ETag);

        using var staleUpdate = new HttpRequestMessage(HttpMethod.Put, "/api/modelproviders/ollama-lab")
        {
            Content = JsonContent.Create(new PutModelProviderRequest(updatedProperties))
        };
        staleUpdate.Headers.IfMatch.Add(createdResponse.Headers.ETag);
        using var staleResponse = await client.SendAsync(staleUpdate);
        Assert.AreEqual(HttpStatusCode.Conflict, staleResponse.StatusCode);

        var usages = await client.GetFromJsonAsync<ModelProviderUsagesResponse>("/api/modelproviders/ollama-lab/usages");
        Assert.AreEqual(0, usages!.Count);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/modelproviders/ollama-lab");
        delete.Headers.IfMatch.Add(updatedResponse.Headers.ETag!);
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missing = await client.GetAsync("/api/modelproviders/ollama-lab");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

}

