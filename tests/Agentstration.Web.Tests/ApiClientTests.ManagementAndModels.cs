using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Configuration;
using Agentstration.Web.Console;
using Agentstration.Web.Features.Flows.Designer;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.Security;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task ManagementClientPreservesETagAndSendsCreatePrecondition()
    {
        var resource = CreateAgentResource("web-agent");
        var sawCreatePrecondition = false;
        string? requestPath = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestPath = request.RequestUri?.AbsolutePath;
            sawCreatePrecondition = request.Headers.IfNoneMatch.Any(value => value == EntityTagHeaderValue.Any);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ManagementApiClient(httpClient);

        var snapshot = await client.PutAgentAsync(ToRequest(resource), null, createOnly: true, CancellationToken.None);

        Assert.IsTrue(sawCreatePrecondition);
        Assert.AreEqual("/api/agents/web-agent", requestPath);
        Assert.AreEqual("\"v1\"", snapshot.ETag);
        Assert.AreEqual("web-agent", snapshot.Value.Name);
    }

    [TestMethod]
    public async Task ManagementClientListsAgentsThroughApiInsteadOfRazorPage()
    {
        var @namespace = new ResourceNamespace("agentstration.who-am-i");
        var resource = CreateAgentResource("web-agent") with
        {
            Metadata = CreateAgentResource("web-agent").Metadata with { Namespace = @namespace },
            Definition = CreateAgentResource("web-agent").Definition with
            {
                ModelProfile = new ResourceReference("reasoning-shared", @namespace: new ResourceNamespace("shared.models"))
            }
        };
        var requestedPaths = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri.AbsolutePath == "/api/agents"
                    ? JsonContent.Create(new PagedResponse<AgentResource>([resource], null))
                    : JsonContent.Create(new PagedResponse<AgentDeployment>([], null))
            };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ManagementApiClient(httpClient);

        var agents = await client.GetAgentsAsync(CancellationToken.None);

        CollectionAssert.Contains(requestedPaths, "/api/agents?allNamespaces=true&top=1000");
        CollectionAssert.Contains(requestedPaths, "/api/deployments?top=1000");
        Assert.HasCount(1, agents);
        Assert.AreEqual(@namespace, agents[0].Namespace);
        Assert.AreEqual(new ResourceNamespace("shared.models"), agents[0].ModelProfileNamespace);
        Assert.AreEqual("/modelprofiles/reasoning-shared?namespace=shared.models", ConsoleResourceUrls.ModelProfile(agents[0].ModelProfileAddress));
        Assert.AreEqual("/namespaces/agentstration.who-am-i/agents/web-agent", agents[0].DetailsUrl);
    }

    [TestMethod]
    public async Task ManagementClientReportsTheCurrentAgentDeployment()
    {
        var updatedAt = new DateTimeOffset(2026, 8, 31, 15, 20, 0, TimeSpan.Zero);
        var agent = CreateAgentResource("web-agent") with
        {
            Generation = 4,
            Metadata = CreateAgentResource("web-agent").Metadata with { Namespace = new ResourceNamespace("engineering") }
        };
        var deployment = new AgentDeployment
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.AgentDeployment,
            Metadata = new ResourceMetadata { Name = "web-agent--g000004", Namespace = agent.Namespace },
            AgentNamespace = agent.Namespace,
            RevisionName = "web-agent--000004",
            AgentName = agent.Metadata.Name,
            Environment = "local",
            RuntimeProfileName = "maf-builtin",
            HostingMode = AgentHostingMode.InProcess,
            DesiredState = DesiredAgentState.Running,
            ProvisioningState = ProvisioningState.Succeeded,
            OperationalState = OperationalState.Ready,
            ObservedRevisionName = "web-agent--000004",
            UpdatedAt = updatedAt
        };
        using var httpClient = new HttpClient(new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath == "/api/agents"
                ? JsonContent.Create(new PagedResponse<AgentResource>([agent], null))
                : JsonContent.Create(new PagedResponse<AgentDeployment>([deployment], null))
        }))
        { BaseAddress = new Uri("http://localhost/") };

        var agents = await new ManagementApiClient(httpClient).GetAgentsAsync(default);

        Assert.HasCount(1, agents);
        Assert.AreEqual("Ready", agents[0].Runtime);
        Assert.AreEqual("web-agent--g000004", agents[0].DeploymentId);
        Assert.AreEqual("/deployments#deployment-engineering-web-agent--g000004", agents[0].DeploymentUrl);
        Assert.AreEqual(updatedAt, agents[0].LastActivity);
    }

    [TestMethod]
    public async Task ManagementClientGetsAgentFromItsNamespace()
    {
        var @namespace = new ResourceNamespace("agentstration.who-am-i");
        var resource = CreateAgentResource("who-am-i-judge") with
        {
            Metadata = CreateAgentResource("who-am-i-judge").Metadata with { Namespace = @namespace }
        };
        Uri? requested = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
            response.Headers.ETag = new EntityTagHeaderValue("\"stored\"");
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ManagementApiClient(httpClient);

        var actual = await client.GetAgentAsync(@namespace, resource.Metadata.Name, CancellationToken.None);

        Assert.AreEqual("/api/namespaces/agentstration.who-am-i/agents/who-am-i-judge", requested!.AbsolutePath);
        Assert.AreEqual(@namespace, actual.Value.Namespace);
    }

    [TestMethod]
    public async Task ManagementClientSendsETagForUpdateAndDelete()
    {
        var resource = CreateAgentResource("web-agent");
        var methods = new List<(HttpMethod Method, string? IfMatch)>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            methods.Add((request.Method, request.Headers.IfMatch.FirstOrDefault()?.ToString()));
            var response = new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK);
            if (request.Method != HttpMethod.Delete)
            {
                response.Content = JsonContent.Create(resource);
                response.Headers.ETag = new EntityTagHeaderValue("\"v2\"");
            }
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ManagementApiClient(httpClient);

        _ = await client.PutAgentAsync(ToRequest(resource), "\"v1\"", createOnly: false, CancellationToken.None);
        await client.DeleteAgentAsync("web-agent", "\"v2\"", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { (HttpMethod.Put, "\"v1\""), (HttpMethod.Delete, "\"v2\"") }, methods);
    }

    [TestMethod]
    public async Task ManagementClientExposesProblemDetailsAndConcurrencyConflict()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
        {
            Content = JsonContent.Create(new { title = "precondition_failed", detail = "The ETag is stale.", status = 412 })
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ManagementApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<AgentstrationApiException>(() => client.GetAgentAsync("web-agent", CancellationToken.None));

        Assert.IsTrue(exception.IsConcurrencyConflict);
        Assert.AreEqual("precondition_failed", exception.ProblemTitle);
        Assert.AreEqual("The ETag is stale.", exception.Message);
    }

    [TestMethod]
    public async Task ModelProvidersClientMapsProviderAndDynamicModels()
    {
        var provider = new ModelProviderResponse("provider-id", "ollama-local", new ModelProviderPropertiesResponse("Ollama local", "aep", "ollama", "ollama-extension", "default", "aspire", "available", "Ollama extension", 1));
        var model = new AvailableModelResponse("qwen3:4b", "Qwen 3 4B", "available", ["chat"], new Dictionary<string, string> { ["parameterSize"] = "4B" });
        using var httpClient = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ValueResponse<AvailableModelResponse>([model])) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ValueResponse<ModelProviderResponse>([provider])) }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ModelProvidersApiClient(httpClient);

        var providers = await client.GetModelProvidersAsync(default);
        var models = await client.GetProviderModelsAsync("ollama-local", default);

        Assert.AreEqual("aspire", providers[0].Properties.RegistrationSource);
        Assert.AreEqual("qwen3:4b", models[0].Name);
        Assert.AreEqual("4B", models[0].Metadata["parameterSize"]);
    }

    [TestMethod]
    public async Task ModelManagementClientsPreserveNamespaceInResourceRequests()
    {
        var requests = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            object response = request.RequestUri.AbsolutePath switch
            {
                var path when path.Contains("modelproviders", StringComparison.Ordinal) => new ValueResponse<AvailableModelResponse>([]),
                var path when path.Contains("modelprofiles", StringComparison.Ordinal) => new ModelProfileUsagesResponse([], 0),
                _ => new RuntimeProfileUsagesResponse([], 0)
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var resourceNamespace = new ResourceNamespace("team-a");

        _ = await new ModelProvidersApiClient(httpClient).GetProviderModelsAsync(resourceNamespace, "shared", default);
        _ = await new ModelProfilesApiClient(httpClient).GetModelProfileUsagesAsync(resourceNamespace, "shared", default);
        _ = await new RuntimeProfilesApiClient(httpClient).GetRuntimeProfileUsagesAsync(resourceNamespace, "shared", default);

        CollectionAssert.AreEqual(new[]
        {
            "/api/modelproviders/shared/models?resourceNamespace=team-a",
            "/api/modelprofiles/shared/usages?resourceNamespace=team-a",
            "/api/runtimeprofiles/shared/usages?resourceNamespace=team-a"
        }, requests);
    }

    [TestMethod]
    public async Task ModelProfilesClientPreservesETagForUpdateAndDelete()
    {
        var profile = CreateModelProfile("reasoning-default");
        var requests = new List<(HttpMethod Method, string? IfMatch)>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add((request.Method, request.Headers.IfMatch.FirstOrDefault()?.ToString()));
            var response = new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK);
            if (request.Method != HttpMethod.Delete) { response.Content = JsonContent.Create(profile); response.Headers.ETag = new EntityTagHeaderValue("\"v2\""); }
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ModelProfilesApiClient(httpClient);

        _ = await client.UpdateModelProfileAsync(profile.Name, new PutModelProfileRequest(profile.Definition), "\"v1\"", default);
        await client.DeleteModelProfileAsync(profile.Name, "\"v2\"", default);

        CollectionAssert.AreEqual(new[] { (HttpMethod.Put, "\"v1\""), (HttpMethod.Delete, "\"v2\"") }, requests);
    }

    [TestMethod]
    public void ModelProfilePickerFilteringCoversNameProviderAndModelAndRejectsInvalidProfiles()
    {
        var ready = Summary("reasoning-default", "ollama-local", "qwen3:4b", "ready");
        var invalid = Summary("broken", "remote-provider", "missing", "invalidConfiguration");

        Assert.HasCount(1, ModelManagementUi.FilterProfiles([ready, invalid], "qwen3"));
        Assert.HasCount(1, ModelManagementUi.FilterProfiles([ready, invalid], "remote-provider"));
        Assert.IsFalse(ModelManagementUi.IsInvalid(ready));
        Assert.IsTrue(ModelManagementUi.IsInvalid(invalid));
    }

    [TestMethod]
    public async Task SecretsClientWritesValueThroughDedicatedEndpointAndNeverOffersReadValue()
    {
        HttpMethod? method = null;
        string? path = null;
        string? body = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            method = request.Method;
            path = request.RequestUri?.AbsolutePath;
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var client = new SecretsApiClient(httpClient);

        await client.SetSecretValueAsync("openai-key", "sensitive-value", default);

        Assert.AreEqual(HttpMethod.Put, method);
        Assert.AreEqual("/api/secrets/openai-key/value", path);
        StringAssert.Contains(body, "sensitive-value");
        Assert.IsFalse(typeof(ISecretsClient).GetMethods().Any(value => value.Name.Contains("GetSecretValue", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task VaultInitializationPostsToDedicatedEndpointWithoutReturningKeyMaterial()
    {
        HttpMethod? method = null;
        string? path = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            method = request.Method;
            path = request.RequestUri?.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new VaultInitializationResponse("initialized", "C:\\data\\secrets\\master.key"))
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var client = new SecretsApiClient(httpClient);

        var response = await client.InitializeVaultAsync("local vault", default);

        Assert.AreEqual(HttpMethod.Post, method);
        Assert.AreEqual("/api/vaults/local%20vault/initialize", path);
        Assert.AreEqual("initialized", response.Status);
        Assert.AreEqual("C:\\data\\secrets\\master.key", response.KeyFilePath);
        CollectionAssert.AreEquivalent(
            new[] { nameof(VaultInitializationResponse.Status), nameof(VaultInitializationResponse.KeyFilePath) },
            typeof(VaultInitializationResponse).GetProperties().Select(property => property.Name).ToArray());
    }

    [TestMethod]
    public void ModelProviderEditorPersistsExtensionAndContributionReferences()
    {
        var editor = new ModelProviderEditorModel
        {
            Name = "openai",
            DisplayName = "OpenAI",
            ExtensionId = "default:openai-extension",
            ContributionId = "openai"
        };

        var properties = editor.ToProperties();

        Assert.AreEqual("openai-extension", properties.Extension.Name);
        Assert.IsNull(properties.Extension.Namespace);
        Assert.AreEqual("openai", properties.ContributionId);
    }

    [TestMethod]
    public void SecretEditorBuildsAValidIdentifierFromDisplayName()
    {
        Assert.AreEqual("cle-openai-production", SecretEditorModel.IdentifierFromDisplayName("  Clé OpenAI — Production  "));
        Assert.AreEqual("github-token", SecretEditorModel.IdentifierFromDisplayName("GitHub___Token"));
        Assert.AreEqual(string.Empty, SecretEditorModel.IdentifierFromDisplayName("---"));
    }

    [TestMethod]
    public void ModelProfileEditorPersistsOnlyProviderReferenceModelAndSupportedOptions()
    {
        var editor = new ModelProfileEditorModel
        {
            Name = "reasoning-default",
            DisplayName = "Default reasoning",
            ProviderName = "ollama-local",
            ModelName = "qwen3:4b",
            Temperature = 0.2,
            MaxOutputTokens = 1000
        };

        var request = editor.ToCreateRequest();

        Assert.AreEqual("ollama-local", request.Properties.Provider.Name);
        Assert.AreEqual("qwen3:4b", request.Properties.Model.Name);
        Assert.AreEqual(0.2, request.Properties.Generation.Temperature);
    }

    [TestMethod]
    public async Task AgentsModelClientUsesAgentResolutionEndpoint()
    {
        Uri? requested = null;
        var response = new AgentModelResponse(
            new DeclaredAgentModelResponse(new ModelProfileIdentityResponse("profile-id", "reasoning-default", "Default reasoning")),
            new ResolvedAgentModelResponse(new ModelProviderReferenceResponse("provider-id", "ollama-local", "Ollama local", "ollama", "available"), new ModelReferenceResponse("qwen3:4b", "available"), new EffectiveModelOptionsResponse(new ModelGenerationOptions { Temperature = 0.2, MaxOutputTokens = 1000 }, new ModelReasoningOptions(), new ModelOutputOptions())),
            "ready", []);
        using var httpClient = new HttpClient(new StubHandler(request => { requested = request.RequestUri; return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) }; })) { BaseAddress = new Uri("http://localhost/") };

        var actual = await new AgentsModelApiClient(httpClient).GetAgentModelResolutionAsync("sql-expert", default);

        Assert.AreEqual("qwen3:4b", actual.Resolved.Model.Name);
        Assert.AreEqual("/api/agents/sql-expert/model", requested!.PathAndQuery);
    }

    [TestMethod]
    public async Task AgentsModelClientKeepsTheAgentNamespace()
    {
        Uri? requested = null;
        var response = new AgentModelResponse(
            new DeclaredAgentModelResponse(new ModelProfileIdentityResponse("reasoning", "reasoning", Namespace: "shared.models")),
            new ResolvedAgentModelResponse(null, new ModelReferenceResponse("qwen3:4b"), new EffectiveModelOptionsResponse(new(), new(), new())),
            "ready", []);
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }))
        { BaseAddress = new Uri("http://localhost/") };

        var actual = await new AgentsModelApiClient(httpClient).GetAgentModelResolutionAsync(new ResourceNamespace("agentstration.daily-life-assistant"), "concierge", default);

        Assert.AreEqual("shared.models", actual.Declared.ModelProfile.Namespace);
        Assert.AreEqual("/api/namespaces/agentstration.daily-life-assistant/agents/concierge/model", requested!.AbsolutePath);
    }

    [TestMethod]
    public void AgentEditorMarksOnlyAnEffectiveModelProfileChange()
    {
        var model = new AgentEditorModel
        {
            ModelProfileName = "default-reasoning",
            ModelProfileNamespace = "default"
        };
        var current = Summary("default-reasoning", "ollama-local", "qwen3:1.7b", "available");
        var namespaced = current with { Namespace = "shared.models" };

        Assert.IsFalse(model.SelectModelProfile(current));
        Assert.IsTrue(model.SelectModelProfile(namespaced));
        Assert.AreEqual("shared.models", model.ModelProfileNamespace);
    }

    [TestMethod]
    public async Task ManagementClientMapsPersistedAgentDeploymentsWithoutInventedMetrics()
    {
        var updatedAt = new DateTimeOffset(2026, 8, 21, 9, 30, 0, TimeSpan.Zero);
        var deployment = new AgentDeployment
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.AgentDeployment,
            Metadata = new ResourceMetadata { Name = "sql-expert--g000007", Namespace = new ResourceNamespace("engineering") },
            RevisionName = "sql-expert--000007",
            AgentName = "sql-expert",
            Environment = "local",
            RuntimeProfileName = "maf-default",
            HostingMode = AgentHostingMode.InProcess,
            DesiredState = DesiredAgentState.Running,
            ProvisioningState = ProvisioningState.Succeeded,
            OperationalState = OperationalState.Ready,
            ObservedRevisionName = "sql-expert--000007",
            UpdatedAt = updatedAt
        };
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PagedResponse<AgentDeployment>([deployment], null))
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ManagementApiClient(httpClient);

        var actual = await client.GetDeploymentsAsync(default);

        Assert.HasCount(1, actual);
        Assert.AreEqual("sql-expert--g000007", actual[0].Id);
        Assert.AreEqual("engineering", actual[0].Namespace);
        Assert.AreEqual("Ready", actual[0].Status);
        Assert.AreEqual("maf-default", actual[0].RuntimeProfile);
        Assert.AreEqual(updatedAt, actual[0].UpdatedAt);
    }

}

