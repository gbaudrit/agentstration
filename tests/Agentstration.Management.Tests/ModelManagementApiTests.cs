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

    [TestMethod]
    public async Task DeletingAgentRemovesItsDeploymentAndReleasesRuntimeProfile()
    {
        await using var factory = Factory();
        var agents = factory.Services.GetRequiredService<AgentManagementService>();
        var runtimes = factory.Services.GetRequiredService<RuntimeProfileManagementService>();
        var store = factory.Services.GetRequiredService<IControlPlaneStore>();
        const string agentName = "runtime-cleanup-test";
        const string runtimeName = "runtime-cleanup-profile";
        const string deploymentName = "runtime-cleanup-test--g000001";

        var runtime = await runtimes.CreateAsync(new RuntimeProfileResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.RuntimeProfile,
            Metadata = new ResourceMetadata { Name = runtimeName },
            Definition = new RuntimeProfileProperties
            {
                DisplayName = "Runtime cleanup test",
                RuntimeType = "microsoft-agent-framework"
            }
        }, default);
        var agent = await agents.PutAgentAsync(new AgentResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Agent,
            Metadata = new ResourceMetadata { Name = agentName },
            Definition = new AgentProperties
            {
                DisplayName = "Runtime cleanup test",
                Instructions = "Test deployment cleanup.",
                ModelProfile = new ResourceReference("reasoning-default")
            }
        }, null, true, default);
        var spec = new AgentDeploymentSpec
        {
            Environment = "local",
            RuntimeProfileName = runtimeName,
            HostingMode = AgentHostingMode.InProcess
        };
        var revision = await agents.CreateRevisionAsync(agentName, spec, default);
        var deployment = await agents.CreateDeploymentAsync(deploymentName, revision.Value.Metadata.Name, spec, default);
        _ = await agents.ReconcileAsync(deployment, default);

        Assert.HasCount(1, await runtimes.GetUsagesAsync(runtimeName, default));

        await agents.DeleteAgentAsync(agentName, agent.ETag, default);

        Assert.IsNull(await agents.GetDeploymentAsync(deploymentName, default));
        Assert.IsEmpty(await runtimes.GetUsagesAsync(runtimeName, default));
        await runtimes.DeleteAsync(runtimeName, runtime.ETag, default);
        Assert.IsNull(await store.GetAsync<RuntimeProfileResource>(new(ResourceKinds.RuntimeProfile, runtimeName), default));
    }

    [TestMethod]
    public async Task DeletingRuntimeProfileCleansUpDeploymentOrphanedByEarlierAgentDeletion()
    {
        await using var factory = Factory();
        var agents = factory.Services.GetRequiredService<AgentManagementService>();
        var runtimes = factory.Services.GetRequiredService<RuntimeProfileManagementService>();
        var store = factory.Services.GetRequiredService<IControlPlaneStore>();
        const string agentName = "orphan-cleanup-test";
        const string runtimeName = "orphan-cleanup-profile";
        const string deploymentName = "orphan-cleanup-test--g000001";

        var runtime = await runtimes.CreateAsync(new RuntimeProfileResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.RuntimeProfile,
            Metadata = new ResourceMetadata { Name = runtimeName },
            Definition = new RuntimeProfileProperties { DisplayName = "Orphan cleanup test", RuntimeType = "microsoft-agent-framework" }
        }, default);
        var agent = await agents.PutAgentAsync(new AgentResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Agent,
            Metadata = new ResourceMetadata { Name = agentName },
            Definition = new AgentProperties
            {
                DisplayName = "Orphan cleanup test",
                Instructions = "Test legacy orphan cleanup.",
                ModelProfile = new ResourceReference("reasoning-default")
            }
        }, null, true, default);
        var spec = new AgentDeploymentSpec
        {
            Environment = "local",
            RuntimeProfileName = runtimeName,
            HostingMode = AgentHostingMode.InProcess
        };
        var revision = await agents.CreateRevisionAsync(agentName, spec, default);
        var deployment = await agents.CreateDeploymentAsync(deploymentName, revision.Value.Metadata.Name, spec, default);
        _ = await agents.ReconcileAsync(deployment, default);

        await store.DeleteAsync(new(ResourceKinds.Agent, agentName), agent.ETag, default);

        Assert.IsEmpty(await runtimes.GetUsagesAsync(runtimeName, default));
        await runtimes.DeleteAsync(runtimeName, runtime.ETag, default);

        Assert.IsNull(await agents.GetDeploymentAsync(deploymentName, default));
        Assert.IsNull(await runtimes.GetAsync(runtimeName, default));
    }

    [TestMethod]
    public async Task ModelAndRuntimeApisAddressHomonymousResourcesByNamespace()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        const string resourceNamespace = "team-a";
        const string providerName = "ollama-local";

        using var extensionResponse = await client.PostAsJsonAsync("/api/extensionregistrations", new CreateExtensionRegistrationRequest(
            "team-ollama-extension",
            new ExtensionRegistrationProperties { DisplayName = "Team Ollama extension", Endpoint = new Uri("http://127.0.0.1:11439") },
            resourceNamespace));
        Assert.AreEqual(HttpStatusCode.Created, extensionResponse.StatusCode);

        using var providerResponse = await client.PostAsJsonAsync("/api/modelproviders", new CreateModelProviderRequest(
            providerName,
            new ModelProviderProperties
            {
                DisplayName = "Team Ollama",
                Extension = new ResourceReference("team-ollama-extension"),
                ContributionId = "ollama"
            },
            resourceNamespace));
        Assert.AreEqual(HttpStatusCode.Created, providerResponse.StatusCode);

        var provider = await client.GetFromJsonAsync<ModelProviderResource>($"/api/modelproviders/{providerName}?resourceNamespace={resourceNamespace}");
        Assert.AreEqual(resourceNamespace, provider?.Namespace.Value);
        Assert.AreEqual("Team Ollama", provider?.Definition.DisplayName);
        var defaultProvider = await client.GetFromJsonAsync<ModelProviderResource>($"/api/modelproviders/{providerName}");
        Assert.AreEqual(ResourceNamespace.Default, defaultProvider?.Namespace);

        const string profileName = "shared";
        using var profileResponse = await client.PostAsJsonAsync("/api/modelprofiles", new CreateModelProfileRequest(
            profileName,
            new ModelProfileProperties
            {
                DisplayName = "Team profile",
                Provider = new ResourceReference(providerName, @namespace: new ResourceNamespace(resourceNamespace)),
                Model = new ModelSelection { Name = "qwen3:8b" }
            },
            resourceNamespace));
        Assert.AreEqual(HttpStatusCode.Created, profileResponse.StatusCode);
        var profile = await client.GetFromJsonAsync<ModelProfileResource>($"/api/modelprofiles/{profileName}?resourceNamespace={resourceNamespace}");
        Assert.AreEqual(resourceNamespace, profile?.Namespace.Value);

        using var runtimeResponse = await client.PostAsJsonAsync("/api/runtimeprofiles", new CreateRuntimeProfileRequest(
            profileName,
            new RuntimeProfileProperties { DisplayName = "Team runtime", RuntimeType = "microsoft-agent-framework" },
            resourceNamespace));
        Assert.AreEqual(HttpStatusCode.Created, runtimeResponse.StatusCode);
        var runtime = await client.GetFromJsonAsync<RuntimeProfileResource>($"/api/runtimeprofiles/{profileName}?resourceNamespace={resourceNamespace}");
        Assert.AreEqual(resourceNamespace, runtime?.Namespace.Value);

        var providers = await client.GetFromJsonAsync<ValueResponse<ModelProviderResponse>>("/api/modelproviders");
        Assert.IsTrue(providers!.Value.Any(value => value.Name == providerName && value.Namespace == resourceNamespace));
        var profiles = await client.GetFromJsonAsync<ValueResponse<ModelProfileSummaryResponse>>("/api/modelprofiles");
        Assert.IsTrue(profiles!.Value.Any(value => value.Name == profileName && value.Namespace == resourceNamespace));
        var runtimes = await client.GetFromJsonAsync<ValueResponse<RuntimeProfileSummaryResponse>>("/api/runtimeprofiles");
        Assert.IsTrue(runtimes!.Value.Any(value => value.Name == profileName && value.Namespace == resourceNamespace));
    }

    [TestMethod]
    public async Task ToolExecutionHookCrudIsNamespacedValidatedAndUsesETags()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        const string hookName = "block-api-lookup";
        const string resourceNamespace = "team.governance";
        var properties = new ToolExecutionHookProperties
        {
            DisplayName = "Block API lookup",
            Handler = ToolExecutionHookHandlers.Deny,
            Order = 25,
            Selector = new ToolExecutionHookSelector { Tools = ["lookup"] },
            Configuration = new Dictionary<string, JsonElement>
            {
                ["code"] = JsonSerializer.SerializeToElement("api_lookup_denied"),
                ["message"] = JsonSerializer.SerializeToElement("API lookup is blocked.")
            }
        };

        using var createdResponse = await client.PostAsJsonAsync(
            "/api/toolexecutionhooks",
            new CreateToolExecutionHookRequest(hookName, properties, resourceNamespace));
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var created = await createdResponse.Content.ReadFromJsonAsync<ToolExecutionHookResource>();
        Assert.IsNotNull(created);
        Assert.AreEqual(resourceNamespace, created.Namespace.Value);

        using var update = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/toolexecutionhooks/{hookName}?resourceNamespace={resourceNamespace}")
        {
            Content = JsonContent.Create(new PutToolExecutionHookRequest(properties with { Enabled = false }))
        };
        update.Headers.IfMatch.Add(createdResponse.Headers.ETag);
        using var updatedResponse = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode);

        var listed = await client.GetFromJsonAsync<ValueResponse<ToolExecutionHookResource>>("/api/toolexecutionhooks");
        Assert.IsTrue(listed!.Value.Any(value => value.Name == hookName && value.Namespace.Value == resourceNamespace));

        using var invalid = await client.PostAsJsonAsync(
            "/api/toolexecutionhooks",
            new CreateToolExecutionHookRequest(
                "invalid-handler",
                properties with { Handler = "arbitrary-code" }));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

        using var delete = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/toolexecutionhooks/{hookName}?resourceNamespace={resourceNamespace}");
        delete.Headers.IfMatch.Add(updatedResponse.Headers.ETag!);
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
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

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:ollama-extension", "Endpoint=http://127.0.0.1:1");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });

    private static WebApplicationFactory<Program> DiagnosticFactory() => Factory().WithWebHostBuilder(builder =>
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IModelProviderDiscovery>();
            services.RemoveAll<IModelProviderCapabilitiesResolver>();
            services.AddSingleton<IModelProviderDiscovery, DiagnosticModelProvider>();
            services.AddSingleton<IModelProviderCapabilitiesResolver, DiagnosticModelProvider>();
        }));

    private sealed class DiagnosticModelProvider : IModelProviderDiscovery, IModelProviderCapabilitiesResolver
    {
        public string ProviderType => "diagnostic";
        public bool CanHandle(string providerType) => true;

        public ValueTask<ModelProviderHealth> GetHealthAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ModelProviderHealth("available"));

        public ValueTask<IReadOnlyList<DiscoveredModel>> ListModelsAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DiscoveredModel>>([
                new("local-model", "Local model", "available", ["chat", "streaming", "structuredOutput"], new Dictionary<string, string>())
            ]);

        public ValueTask<ResolvedModelProviderCapabilities> ResolveCapabilitiesAsync(
            ModelProviderConfiguration provider,
            ModelDeploymentConfiguration deployment,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new ResolvedModelProviderCapabilities(
                Capabilities(streaming: true, tools: true, structuredOutput: true, reasoning: true),
                Capabilities(streaming: true, tools: false, structuredOutput: true, reasoning: false),
                Capabilities(streaming: true, tools: true, structuredOutput: true, reasoning: true)));

        private static AgentRuntimeCapabilities Capabilities(bool streaming, bool tools, bool structuredOutput, bool reasoning) => new()
        {
            Streaming = new(streaming ? CapabilitySupport.Native : CapabilitySupport.Unsupported),
            Tools = new(tools ? CapabilitySupport.Native : CapabilitySupport.Unsupported),
            StructuredOutput = new(structuredOutput ? CapabilitySupport.Native : CapabilitySupport.Unsupported),
            Reasoning = new ReasoningCapability { Support = reasoning ? CapabilitySupport.Native : CapabilitySupport.Unsupported }
        };
    }

    private sealed class ConfiguredEndpointInspector : IExtensionInspector
    {
        public bool CanHandle(string providerType) => true;
        public bool CanInspectEndpoint(Uri endpoint) => true;
        public ValueTask<ExtensionInspection> InspectAsync(
            ModelProviderConfiguration provider,
            CancellationToken cancellationToken = default) =>
            InspectAsync(provider.Name, provider.Endpoint, cancellationToken);
        public ValueTask<ExtensionInspection> InspectAsync(
            string registrationName,
            Uri endpoint,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionInspection(
                registrationName,
                endpoint,
                "available",
                new ExtensionIdentity(registrationName == "extension-discovered" ? "extension.discovered" : registrationName, "Discovered extension", "1.0.0", null),
                [new ExtensionContribution("model-provider", "discovered")],
                []));
    }

    private sealed class MigrationExtensionAdapter : IExtensionInspector, IExtensionOptionsMigrator
    {
        private static readonly JsonElement SourceSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { legacyName = new { type = "string" } },
            required = new[] { "legacyName" },
            additionalProperties = false
        });
        private static readonly JsonElement TargetSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { name = new { type = "string" } },
            required = new[] { "name" },
            additionalProperties = false
        });
        public static ExtensionOptionSet OptionSet { get; } = new(
            "io.agentstration.test/model-profile",
            "model-provider",
            "ollama",
            ExtensionOptionScopes.ModelProfile,
            "2.0.0",
            [
                new("1.0.0", ExtensionOptionSchemaDigest.Compute(SourceSchema), SourceSchema, false),
                new("2.0.0", ExtensionOptionSchemaDigest.Compute(TargetSchema), TargetSchema, false)
            ],
            [new("1.0.0", "2.0.0")]);

        public bool CanHandle(string providerType) => string.Equals(providerType, AepModelProvider.AdapterType, StringComparison.OrdinalIgnoreCase);
        public bool CanInspectEndpoint(Uri endpoint) => true;
        public ValueTask<ExtensionInspection> InspectAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            InspectAsync(provider.Name, provider.Endpoint, cancellationToken);
        public ValueTask<ExtensionInspection> InspectAsync(string registrationName, Uri endpoint, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionInspection(
                registrationName,
                endpoint,
                "available",
                new ExtensionIdentity("migration.test", "Migration test", "2.0.0", null),
                [new ExtensionContribution("model-provider", "ollama")],
                [OptionSet]));
        public ValueTask<VersionedExtensionOptions> MigrateAsync(
            ModelProviderConfiguration provider,
            VersionedExtensionOptions source,
            string targetVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = OptionSet.Versions.Single(value => value.Version == targetVersion);
            return ValueTask.FromResult(new VersionedExtensionOptions
            {
                OptionSet = source.OptionSet,
                Version = target.Version,
                SchemaDigest = target.SchemaDigest,
                Values = JsonSerializer.SerializeToElement(new { name = source.Values.GetProperty("legacyName").GetString() })
            });
        }
    }

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
