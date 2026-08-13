using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.ModelProviders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ModelManagementApiTests
{
    [TestMethod]
    public async Task ManagedHostCompositionUsesPersistedModelProfileResolver()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI:Provider", "Managed");
            builder.UseSetting("ConnectionStrings:ollama-extension", "Endpoint=http://127.0.0.1:1");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(factory.Services.GetRequiredService<Agentstration.Application.IAgentRuntime>());
        Assert.IsInstanceOfType<ChatClientResolver>(factory.Services.GetRequiredService<IChatClientResolver>());
    }

    [TestMethod]
    public async Task SeededOllamaProviderUsesAepExtensionEndpointInsteadOfNativeOllamaEndpoint()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI:Provider", "Ollama");
            builder.UseSetting("AI:Endpoint", "http://localhost:11434");
            builder.UseSetting("Agentstration:Extensions:Agentstration.Extensions.Ollama:Endpoint", "http://localhost:5265");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });
        using var client = factory.CreateClient();

        var provider = await client.GetFromJsonAsync<ModelProviderResource>("/api/modelproviders/ollama-local");

        Assert.IsNotNull(provider);
        Assert.AreEqual(new Uri("http://localhost:5265"), provider.Definition.Endpoint);
        Assert.AreNotEqual(new Uri("http://localhost:11434"), provider.Definition.Endpoint);
    }

    [TestMethod]
    public async Task ReadOnlyProviderApisExposeConfiguredProviderAndUnavailableDiscovery()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var providers = await client.GetFromJsonAsync<ValueResponse<ModelProviderResponse>>("/api/modelproviders");
        var provider = providers!.Value.Single(value => value.Name == "ollama-local");
        var status = await client.GetFromJsonAsync<ModelProviderStatusResponse>("/api/modelproviders/ollama-local/status");
        using var models = await client.GetAsync("/api/modelproviders/ollama-local/models");

        Assert.AreEqual("aspire", provider.Properties.ManagementMode);
        Assert.AreEqual("unavailable", status!.Status);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, models.StatusCode);
        Assert.AreEqual("application/problem+json", models.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task ProfileCrudUsesETagAndAllowsTemporarilyUnavailableModel()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var create = Request("api-test-profile", "model-not-downloaded");

        using var createdResponse = await client.PostAsJsonAsync("/api/modelprofiles", create);
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var created = await createdResponse.Content.ReadFromJsonAsync<ModelProfileResource>();
        Assert.IsNotNull(created);
        var profileStore = factory.Services.GetRequiredService<IModelProfileStore>();
        var deploymentStore = factory.Services.GetRequiredService<IModelDeploymentStore>();
        var runtimeProfile = await profileStore.GetRequiredAsync(created.Metadata.Name);
        var runtimeDeployment = await deploymentStore.GetRequiredAsync(runtimeProfile.DeploymentName);
        Assert.AreEqual(ModelProviderManagementService.ModelProviderId("ollama-local"), runtimeDeployment.ProviderName);
        Assert.AreEqual("model-not-downloaded", runtimeDeployment.ModelName);

        var updatedProperties = created.Definition with { Description = "Updated profile" };
        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/modelprofiles/api-test-profile")
        {
            Content = JsonContent.Create(new PutModelProfileRequest(updatedProperties))
        };
        update.Headers.IfMatch.Add(createdResponse.Headers.ETag);
        using var updatedResponse = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode);
        Assert.AreNotEqual(createdResponse.Headers.ETag, updatedResponse.Headers.ETag);

        var filtered = await client.GetFromJsonAsync<ValueResponse<ModelProfileSummaryResponse>>(
            "/api/modelprofiles?provider=ollama-local&model=model-not-downloaded&status=providerUnavailable&search=api-test");
        Assert.IsTrue(filtered!.Value.Any(profile => profile.Name == "api-test-profile"));

        using var staleUpdate = new HttpRequestMessage(HttpMethod.Put, "/api/modelprofiles/api-test-profile")
        {
            Content = JsonContent.Create(new PutModelProfileRequest(updatedProperties))
        };
        staleUpdate.Headers.IfMatch.Add(createdResponse.Headers.ETag);
        using var staleResponse = await client.SendAsync(staleUpdate);
        Assert.AreEqual(HttpStatusCode.Conflict, staleResponse.StatusCode);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/modelprofiles/api-test-profile");
        delete.Headers.IfMatch.Add(updatedResponse.Headers.ETag!);
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [TestMethod]
    public async Task UsedProfileCannotBeDeletedAndExposesUsagesResolutionAndAgentModel()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var usages = await client.GetFromJsonAsync<ModelProfileUsagesResponse>("/api/modelprofiles/reasoning-default/usages");
        var resolution = await client.GetFromJsonAsync<ModelProfileResolutionResponse>("/api/modelprofiles/reasoning-default/resolution");
        var agentModel = await client.GetFromJsonAsync<AgentModelResponse>("/api/agents/sql-expert/model");
        using var deleted = await client.DeleteAsync("/api/modelprofiles/reasoning-default");

        Assert.IsTrue(usages!.Count >= 1);
        Assert.IsTrue(usages.Value.Any(usage => usage.Name == "sql-expert"));
        Assert.AreEqual("unavailable", resolution!.Status);
        Assert.AreEqual("reasoning-default", agentModel!.Declared.ModelProfile.Name);
        Assert.AreEqual(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.AreEqual("application/problem+json", deleted.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task AgentUpdateRejectsUnknownModelProfileWithUnprocessableEntity()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        const string path = "/api/agents/sql-expert";
        var agent = await client.GetFromJsonAsync<AgentResource>(path);
        Assert.IsNotNull(agent);
        var request = new AgentResourceRequest
        {
            ApiVersion = agent.ApiVersion,
            Kind = agent.Kind,
            Metadata = agent.Metadata,
            Definition = agent.Definition with
            {
                ModelProfile = new ResourceReference(ModelProfileManagementService.ProfileId("missing-profile"))
            }
        };

        using var response = await client.PutAsJsonAsync(path, request);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task AgentModelExpansionUsesTheNewProfileImmediatelyAfterUpdate()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        const string agentPath = "/api/agents/sql-expert";
        const string profileName = "deep-reasoning";
        var profileRequest = Request(profileName, "qwen3.6:latest") with
        {
            Properties = Request(profileName, "qwen3.6:latest").Properties with { DisplayName = "Deep reasoning" }
        };
        using var createdProfile = await client.PostAsJsonAsync("/api/modelprofiles", profileRequest);
        Assert.AreEqual(HttpStatusCode.Created, createdProfile.StatusCode);

        using var currentResponse = await client.GetAsync(agentPath);
        Assert.AreEqual(HttpStatusCode.OK, currentResponse.StatusCode);
        Assert.IsNotNull(currentResponse.Headers.ETag);
        var current = await currentResponse.Content.ReadFromJsonAsync<AgentResource>();
        Assert.IsNotNull(current);
        var updateRequest = new AgentResourceRequest
        {
            ApiVersion = current.ApiVersion,
            Kind = current.Kind,
            Metadata = current.Metadata,
            Definition = current.Definition with
            {
                ModelProfile = new ResourceReference(ModelProfileManagementService.ProfileId(profileName))
            }
        };
        using var update = new HttpRequestMessage(HttpMethod.Put, agentPath) { Content = JsonContent.Create(updateRequest) };
        update.Headers.IfMatch.Add(currentResponse.Headers.ETag);
        using var updatedResponse = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode);

        var expanded = await client.GetFromJsonAsync<AgentModelResponse>("/api/agents/sql-expert/model");
        Assert.IsNotNull(expanded);
        Assert.AreEqual(profileName, expanded.Declared.ModelProfile.Name);
        Assert.AreEqual("Deep reasoning", expanded.Declared.ModelProfile.DisplayName);
        Assert.AreEqual("qwen3.6:latest", expanded.Resolved.Model.Name);
    }

    [TestMethod]
    public async Task ProfileCreationRejectsUnknownProviderWithProblemDetails()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var request = Request("invalid-provider-profile", "qwen3:1.7b") with
        {
            Properties = Request("ignored", "qwen3:1.7b").Properties with
            {
                Provider = new ResourceReference(ModelProviderManagementService.ModelProviderId("missing-provider"))
            }
        };

        using var response = await client.PostAsJsonAsync("/api/modelprofiles", request);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task ProviderCrudUsesETagAndPersistsItsOllamaEndpoint()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var properties = new ModelProviderProperties
        {
            DisplayName = "Ollama lab",
            ProviderType = "ollama",
            Endpoint = new Uri("http://127.0.0.1:11435"),
            ManagementMode = ModelProviderManagementMode.External
        };

        using var createdResponse = await client.PostAsJsonAsync(
            "/api/modelproviders",
            new CreateModelProviderRequest("ollama-lab", properties));
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var created = await createdResponse.Content.ReadFromJsonAsync<ModelProviderResource>();
        Assert.IsNotNull(created);
        Assert.AreEqual(new Uri("http://127.0.0.1:11435/"), created.Definition.Endpoint);

        using var getResponse = await client.GetAsync("/api/modelproviders/ollama-lab");
        Assert.AreEqual(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.AreEqual(createdResponse.Headers.ETag, getResponse.Headers.ETag);

        var updatedProperties = created.Definition with
        {
            DisplayName = "Ollama workstation",
            Endpoint = new Uri("http://127.0.0.1:11436")
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
                ProviderType = "ollama",
                Endpoint = new Uri("file:///tmp/ollama")
            });

        using var invalidResponse = await client.PostAsJsonAsync("/api/modelproviders", invalid);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidResponse.StatusCode);
        Assert.AreEqual("application/problem+json", invalidResponse.Content.Headers.ContentType?.MediaType);

        var usages = await client.GetFromJsonAsync<ModelProviderUsagesResponse>("/api/modelproviders/ollama-local/usages");
        Assert.IsTrue(usages!.Count >= 1);
        Assert.IsTrue(usages.Value.Any(usage => usage.ResourceType == ResourceKinds.ModelProfile));
        using var deleted = await client.DeleteAsync("/api/modelproviders/ollama-local");
        Assert.AreEqual(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.AreEqual("application/problem+json", deleted.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task CanonicalProfileCategoriesFlowIntoRuntimeResolution()
    {
        await using var factory = Factory();
        var service = factory.Services.GetRequiredService<ModelProfileManagementService>();
        var profile = new ModelProfileResource
        {
            Metadata = new ResourceMetadata { Name = "canonical-profile" },
            Kind = ResourceKinds.ModelProfile,
            ApiVersion = ManagementApiVersions.CoreV1,
            Definition = new ModelProfileProperties
            {
                DisplayName = "Canonical profile",
                Provider = new ResourceReference(ModelProviderManagementService.ModelProviderId("ollama-local")),
                Model = new ModelSelection { Name = "qwen3:8b" },
                Generation = new ModelGenerationOptions { Temperature = 0.2, TopP = 0.8, TopK = 20, MaxOutputTokens = 4096 },
                Reasoning = new ModelReasoningOptions { Mode = ReasoningMode.Enabled, Effort = ReasoningEffort.Medium },
                Output = new ModelOutputOptions { Format = ModelOutputFormat.JsonObject },
                ProviderOptions = new Dictionary<string, JsonElement>
                {
                    ["ollama"] = JsonSerializer.SerializeToElement(new { think = "medium", contextSize = 8192 })
                }
            }
        };

        var stored = await service.CreateAsync(profile, default);
        var resolved = await ((IModelProfileStore)service).GetRequiredAsync(stored.Value.Metadata.Name);
        var deployment = await ((IModelDeploymentStore)service).GetRequiredAsync(resolved.DeploymentName);

        Assert.AreEqual(0.2, resolved.Generation.Temperature);
        Assert.AreEqual(0.8, resolved.Generation.TopP);
        Assert.AreEqual(ReasoningEffort.Medium, resolved.Reasoning.Effort);
        Assert.IsTrue(deployment.ProviderOptions.ContainsKey("ollama"));
    }

    [TestMethod]
    public async Task RuntimeProfileIsPersistedAsAnIndependentManagementResource()
    {
        await using var factory = Factory();
        var service = factory.Services.GetRequiredService<RuntimeProfileManagementService>();
        var id = RuntimeProfileManagementService.ProfileId("maf-persistent-test");
        var stored = await service.CreateAsync(new RuntimeProfileResource
        {
            Metadata = new ResourceMetadata { Name = id },
            Kind = ResourceKinds.RuntimeProfile,
            ApiVersion = ManagementApiVersions.CoreV1,
            Definition = new RuntimeProfileProperties
            {
                DisplayName = "MAF default",
                RuntimeType = "microsoft-agent-framework",
                Execution = new RuntimeExecutionDefaults
                {
                    SessionMode = RuntimeSessionMode.Persistent,
                    ToolInvocation = RuntimeToolInvocationMode.Automatic,
                    Streaming = StreamingMode.Automatic
                },
                RuntimeOptions = new Dictionary<string, JsonElement>
                {
                    ["microsoftAgentFramework"] = JsonSerializer.SerializeToElement(new { useProvidedChatClient = true })
                }
            }
        }, default);

        Assert.AreEqual(1, stored.Value.Generation);
        Assert.AreEqual("microsoft-agent-framework", stored.Value.Definition.RuntimeType);
        Assert.IsNotNull(await service.GetAsync("maf-persistent-test", default));

        var updated = await service.PutAsync("maf-persistent-test", stored.Value.Definition with
        {
            Execution = stored.Value.Definition.Execution with { Streaming = StreamingMode.Enabled }
        }, stored.ETag, default);
        Assert.AreEqual(2, updated.Value.Generation);
        Assert.AreEqual(StreamingMode.Enabled, updated.Value.Definition.Execution.Streaming);
        Assert.IsEmpty(await service.GetUsagesAsync(id, default));

        await service.DeleteAsync("maf-persistent-test", updated.ETag, default);
        Assert.IsNull(await service.GetAsync("maf-persistent-test", default));
    }

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:ollama-extension", "Endpoint=http://127.0.0.1:1");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });

    private static CreateModelProfileRequest Request(string name, string model) => new(
        name,
        new ModelProfileProperties
        {
            DisplayName = name,
            Description = "API test profile",
            Provider = new ResourceReference(ModelProviderManagementService.ModelProviderId("ollama-local")),
            Model = new ModelSelection { Name = model },
            Generation = new ModelGenerationOptions { Temperature = 0.3, MaxOutputTokens = 512 }
        });
}
