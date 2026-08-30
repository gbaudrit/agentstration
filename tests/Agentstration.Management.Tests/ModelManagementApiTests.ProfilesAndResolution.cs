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
        Assert.IsInstanceOfType<ChatClientResolver>(factory.Services.GetRequiredService<IChatClientResolver>());
    }

    [TestMethod]
    public async Task ModelProfileOptionMigrationPreviewsWithoutWritingAndAppliesWithETag()
    {
        await using var factory = Factory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IExtensionInspector>();
                services.RemoveAll<IExtensionOptionsMigrator>();
                services.AddSingleton<IExtensionInspector, MigrationExtensionAdapter>();
                services.AddSingleton<IExtensionOptionsMigrator, MigrationExtensionAdapter>();
            }));
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        var profiles = factory.Services.GetRequiredService<ModelProfileManagementService>();
        var sourceVersion = MigrationExtensionAdapter.OptionSet.Versions.Single(value => value.Version == "1.0.0");
        var stored = await profiles.CreateAsync(new ModelProfileResource
        {
            Metadata = new ResourceMetadata { Name = "migration-profile" },
            Kind = ResourceKinds.ModelProfile,
            ApiVersion = ManagementApiVersions.CoreV1,
            Definition = new ModelProfileProperties
            {
                DisplayName = "Migration profile",
                Provider = new ResourceReference(ModelProviderManagementService.ModelProviderId("ollama-local")),
                Model = new ModelSelection { Name = "test-model" },
                ProviderOptions = new Dictionary<string, VersionedExtensionOptions>
                {
                    ["ollama"] = new()
                    {
                        OptionSet = MigrationExtensionAdapter.OptionSet.Id,
                        Version = sourceVersion.Version,
                        SchemaDigest = sourceVersion.SchemaDigest,
                        Values = JsonSerializer.SerializeToElement(new { legacyName = "kept" })
                    }
                }
            }
        }, default);
        using var client = factory.CreateClient();

        using var previewResponse = await client.PostAsJsonAsync(
            "/api/modelprofiles/migration-profile/option-migrations/preview",
            new PreviewModelProfileOptionMigrationRequest("2.0.0"));

        Assert.AreEqual(HttpStatusCode.OK, previewResponse.StatusCode);
        Assert.AreEqual(stored.ETag, previewResponse.Headers.ETag?.ToString());
        var preview = await previewResponse.Content.ReadFromJsonAsync<ModelProfileOptionMigrationPreviewResponse>();
        Assert.IsNotNull(preview);
        Assert.AreEqual("1.0.0", preview.Source.Version);
        Assert.AreEqual("2.0.0", preview.Target.Version);
        Assert.AreEqual("kept", preview.Target.Values.GetProperty("name").GetString());
        var unchanged = await profiles.GetAsync("migration-profile", default);
        Assert.AreEqual("1.0.0", unchanged!.Value.Definition.ProviderOptions["ollama"].Version);

        using var invalidPreview = await client.PostAsJsonAsync(
            "/api/modelprofiles/migration-profile/option-migrations/preview",
            new PreviewModelProfileOptionMigrationRequest("9.0.0"));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidPreview.StatusCode);
        unchanged = await profiles.GetAsync("migration-profile", default);
        Assert.AreEqual(stored.ETag, unchanged!.ETag);

        using var missingPrecondition = await client.PostAsJsonAsync(
            "/api/modelprofiles/migration-profile/option-migrations/apply",
            new PreviewModelProfileOptionMigrationRequest("2.0.0"));
        Assert.AreEqual(HttpStatusCode.Conflict, missingPrecondition.StatusCode);
        unchanged = await profiles.GetAsync("migration-profile", default);
        Assert.AreEqual(stored.ETag, unchanged!.ETag);

        using var apply = new HttpRequestMessage(HttpMethod.Post, "/api/modelprofiles/migration-profile/option-migrations/apply")
        {
            Content = JsonContent.Create(new PreviewModelProfileOptionMigrationRequest("2.0.0"))
        };
        apply.Headers.IfMatch.Add(previewResponse.Headers.ETag!);
        using var appliedResponse = await client.SendAsync(apply);
        Assert.AreEqual(HttpStatusCode.OK, appliedResponse.StatusCode);
        Assert.AreNotEqual(stored.ETag, appliedResponse.Headers.ETag?.ToString());
        var applied = await appliedResponse.Content.ReadFromJsonAsync<ModelProfileResource>();
        Assert.IsNotNull(applied);
        Assert.AreEqual("2.0.0", applied.Definition.ProviderOptions["ollama"].Version);
        Assert.AreEqual("kept", applied.Definition.ProviderOptions["ollama"].Values.GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task ProfileCrudUsesETagAndAllowsTemporarilyUnavailableModel()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
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
    public async Task NamespacedAgentModelResolutionUsesItsExplicitProfileNamespace()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var profileNamespace = new ResourceNamespace("shared.models");
        var agentNamespace = new ResourceNamespace("agentstration.daily-life-assistant");
        var profileRequest = Request("shared-reasoning", "qwen3:4b") with
        {
            Namespace = profileNamespace.Value,
            Properties = Request("shared-reasoning", "qwen3:4b").Properties with
            {
                Provider = new ResourceReference("ollama-local", @namespace: ResourceNamespace.Default)
            }
        };
        using var createdProfile = await client.PostAsJsonAsync("/api/modelprofiles", profileRequest);
        Assert.AreEqual(HttpStatusCode.Created, createdProfile.StatusCode);
        var agentRequest = new AgentResourceRequest
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Agent,
            Metadata = new ResourceMetadata { Name = "concierge", Namespace = agentNamespace },
            Definition = new AgentProperties
            {
                DisplayName = "Concierge",
                Instructions = "Help the user.",
                ModelProfile = new ResourceReference("shared-reasoning", @namespace: profileNamespace)
            }
        };
        using var createdAgent = await client.PutAsJsonAsync(
            $"/api/namespaces/{agentNamespace.Value}/agents/concierge",
            agentRequest);
        Assert.AreEqual(HttpStatusCode.OK, createdAgent.StatusCode);

        var resolution = await client.GetFromJsonAsync<AgentModelResponse>(
            $"/api/namespaces/{agentNamespace.Value}/agents/concierge/model");

        Assert.IsNotNull(resolution);
        Assert.AreEqual("shared-reasoning", resolution.Declared.ModelProfile.Name);
        Assert.AreEqual(profileNamespace.Value, resolution.Declared.ModelProfile.Namespace);
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
    public async Task AgentUpdateRejectsCrossWorkspaceResourceReference()
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
                ModelProfile = new ResourceReference(agent.Definition.ModelProfile.Name, "another-workspace")
            }
        };

        using var response = await client.PutAsJsonAsync(path, request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
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
    public async Task CanonicalProfileCategoriesFlowIntoRuntimeResolution()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
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
                ProviderOptions = new Dictionary<string, VersionedExtensionOptions>
                {
                    ["ollama"] = new()
                    {
                        OptionSet = "io.agentstration.ollama/model-profile",
                        Version = "1.0.0",
                        SchemaDigest = $"sha256:{new string('0', 64)}",
                        Values = JsonSerializer.SerializeToElement(new { think = "medium", contextSize = 8192 })
                    }
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
    public async Task ProfileResolutionExposesEffectiveCapabilitiesAndConfigurationIssues()
    {
        await using var factory = DiagnosticFactory();
        using var client = factory.CreateClient();
        using var providerResponse = await client.PostAsJsonAsync("/api/modelproviders", new CreateModelProviderRequest(
            "diagnostic-local",
            new ModelProviderProperties
            {
                DisplayName = "Diagnostic provider",
                Extension = new ResourceReference("ollama-extension"),
                ContributionId = "diagnostic"
            }));
        Assert.AreEqual(HttpStatusCode.Created, providerResponse.StatusCode);
        using var profileResponse = await client.PostAsJsonAsync("/api/modelprofiles", new CreateModelProfileRequest(
            "incompatible-profile",
            new ModelProfileProperties
            {
                DisplayName = "Incompatible profile",
                Provider = new ResourceReference("diagnostic-local"),
                Model = new ModelSelection { Name = "local-model" },
                Reasoning = new ModelReasoningOptions { Mode = ReasoningMode.Enabled },
                Output = new ModelOutputOptions { Format = ModelOutputFormat.JsonObject }
            }));
        Assert.AreEqual(HttpStatusCode.Created, profileResponse.StatusCode);

        var resolution = await client.GetFromJsonAsync<ModelProfileResolutionResponse>("/api/modelprofiles/incompatible-profile/resolution");

        Assert.IsNotNull(resolution);
        Assert.AreEqual("incompatible", resolution.Status);
        var capabilities = resolution.Capabilities ?? [];
        var incompatibilities = resolution.Incompatibilities ?? [];
        Assert.HasCount(4, capabilities);
        var tools = capabilities.Single(capability => capability.Name == "Tools");
        Assert.AreEqual("native", tools.ProviderSupport);
        Assert.AreEqual("unsupported", tools.ModelSupport);
        Assert.AreEqual("unsupported", tools.EffectiveSupport);
        Assert.AreEqual("native", capabilities.Single(capability => capability.Name == "Structured output").EffectiveSupport);
        Assert.IsTrue(incompatibilities.Any(issue => issue.Capability == "reasoning"));
        Assert.IsFalse(incompatibilities.Any(issue => issue.Capability == "structuredOutput"));
    }

}

