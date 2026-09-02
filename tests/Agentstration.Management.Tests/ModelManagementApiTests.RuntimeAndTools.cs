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
    public async Task RuntimeProfileIsPersistedAsAnIndependentManagementResource()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
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
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
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
    public async Task RecreatedAgentUsesDistinctRevisionIdentityAndCanStartAgain()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        var agents = factory.Services.GetRequiredService<AgentManagementService>();
        const string agentName = "recreated-agent";
        var spec = new AgentDeploymentSpec
        {
            Environment = "local",
            RuntimeProfileName = "maf-builtin",
            HostingMode = AgentHostingMode.InProcess
        };

        var firstAgent = await agents.PutAgentAsync(Agent(agentName), null, true, default);
        var firstRevision = await agents.CreateRevisionAsync(agentName, spec, default);
        await agents.DeleteAgentAsync(agentName, firstAgent.ETag, default);

        var secondAgent = await agents.PutAgentAsync(Agent(agentName), null, true, default);
        var secondRevision = await agents.CreateRevisionAsync(agentName, spec, default);
        var repeated = await agents.CreateRevisionAsync(agentName, spec, default);
        var prepared = await agents.PrepareLocalRuntimeAsync(agentName, secondAgent.Value.Generation, default);

        Assert.AreNotEqual(firstAgent.Value.Uid, secondAgent.Value.Uid);
        Assert.AreNotEqual(firstRevision.Value.Metadata.Name, secondRevision.Value.Metadata.Name);
        Assert.AreEqual(secondAgent.Value.Uid, secondRevision.Value.AgentUid);
        Assert.AreEqual(secondRevision.Value.Metadata.Name, repeated.Value.Metadata.Name);
        Assert.AreEqual(OperationalState.Ready, prepared.Value.OperationalState);

        static AgentResource Agent(string name) => new()
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Agent,
            Metadata = new ResourceMetadata { Name = name },
            Definition = new AgentProperties
            {
                DisplayName = "Recreated agent",
                Instructions = "Verify retained revision identity.",
                ModelProfile = new ResourceReference("reasoning-default")
            }
        };
    }

    [TestMethod]
    public async Task DeletingRuntimeProfileCleansUpDeploymentOrphanedByEarlierAgentDeletion()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
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

}

