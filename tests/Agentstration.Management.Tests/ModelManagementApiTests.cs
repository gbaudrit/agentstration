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
    public async Task ReadOnlyProviderApisExposeConfiguredProviderAndUnavailableDiscovery()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var providers = await client.GetFromJsonAsync<ValueResponse<ModelProviderResponse>>("/api/modelproviders");
        var provider = providers!.Value.Single(value => value.Name == "ollama-local");
        var status = await client.GetFromJsonAsync<ModelProviderStatusResponse>("/api/modelproviders/ollama-local/status");
        using var models = await client.GetAsync("/api/modelproviders/ollama-local/models");

        Assert.AreEqual("aspire", provider.Properties.ManagementMode);
        Assert.AreEqual("unknown", status!.Status);
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
        var runtimeProfile = await profileStore.GetRequiredAsync(created.Id);
        var runtimeDeployment = await deploymentStore.GetRequiredAsync(runtimeProfile.DeploymentName);
        Assert.AreEqual("ollama-local", runtimeDeployment.ProviderName);
        Assert.AreEqual("model-not-downloaded", runtimeDeployment.ModelName);

        var updatedProperties = created.Properties with { Description = "Updated profile" };
        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/modelprofiles/api-test-profile")
        {
            Content = JsonContent.Create(new PutModelProfileRequest(updatedProperties))
        };
        update.Headers.IfMatch.Add(createdResponse.Headers.ETag);
        using var updatedResponse = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode);
        Assert.AreNotEqual(createdResponse.Headers.ETag, updatedResponse.Headers.ETag);

        var filtered = await client.GetFromJsonAsync<ValueResponse<ModelProfileSummaryResponse>>(
            "/api/modelprofiles?provider=ollama-local&model=model-not-downloaded&status=unknown&search=api-test");
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
        Assert.AreEqual("unknown", resolution!.Status);
        Assert.AreEqual("reasoning-default", agentModel!.Declared.ModelProfile.Name);
        Assert.AreEqual(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.AreEqual("application/problem+json", deleted.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task AgentUpdateRejectsUnknownModelProfileWithUnprocessableEntity()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        const string path = "/resourceGroups/default/providers/Agentstration.Agents/agents/sql-expert?api-version=2026-08-01";
        var agent = await client.GetFromJsonAsync<AgentResource>(path);
        Assert.IsNotNull(agent);
        var request = new AgentResourceRequest
        {
            Type = agent.Type,
            ApiVersion = agent.ApiVersion,
            Name = agent.Name,
            ResourceGroup = agent.ResourceGroup!,
            Location = agent.Location!,
            Tags = agent.Tags,
            Properties = agent.Properties with
            {
                ModelProfile = new ResourceReference(ModelProfileManagementService.ProfileId("default", "missing-profile"))
            }
        };

        using var response = await client.PutAsJsonAsync(path, request);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
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
    public async Task CanonicalProfileCategoriesFlowIntoRuntimeResolution()
    {
        await using var factory = Factory();
        var service = factory.Services.GetRequiredService<ModelProfileManagementService>();
        var profile = new ModelProfileResource
        {
            Id = ModelProfileManagementService.ProfileId("default", "canonical-profile"),
            Name = "canonical-profile",
            Type = AgentstrationResourceTypes.ModelProfiles,
            ApiVersion = ManagementApiVersions.V20260801,
            ResourceGroup = "default",
            Location = "local",
            Properties = new ModelProfileProperties
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
        var resolved = await ((IModelProfileStore)service).GetRequiredAsync(stored.Value.Id);
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
        var id = RuntimeProfileManagementService.ProfileId("default", "maf-persistent-test");
        var stored = await service.CreateAsync(new RuntimeProfileResource
        {
            Id = id,
            Name = "maf-persistent-test",
            Type = AgentstrationResourceTypes.RuntimeProfiles,
            ApiVersion = ManagementApiVersions.V20260801,
            ResourceGroup = "default",
            Location = "local",
            Properties = new RuntimeProfileProperties
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
        Assert.AreEqual("microsoft-agent-framework", stored.Value.Properties.RuntimeType);
        Assert.IsNotNull(await service.GetAsync("default", "maf-persistent-test", default));

        var updated = await service.PutAsync("default", "maf-persistent-test", stored.Value.Properties with
        {
            Execution = stored.Value.Properties.Execution with { Streaming = StreamingMode.Enabled }
        }, stored.ETag, default);
        Assert.AreEqual(2, updated.Value.Generation);
        Assert.AreEqual(StreamingMode.Enabled, updated.Value.Properties.Execution.Streaming);
        Assert.IsEmpty(await service.GetUsagesAsync(id, default));

        await service.DeleteAsync("default", "maf-persistent-test", updated.ETag, default);
        Assert.IsNull(await service.GetAsync("default", "maf-persistent-test", default));
    }

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));

    private static CreateModelProfileRequest Request(string name, string model) => new(
        name,
        "default",
        "local",
        new ModelProfileProperties
        {
            DisplayName = name,
            Description = "API test profile",
            Provider = new ResourceReference(ModelProviderManagementService.ModelProviderId("ollama-local")),
            Model = new ModelSelection { Name = model },
            Generation = new ModelGenerationOptions { Temperature = 0.3, MaxOutputTokens = 512 }
        });
}
