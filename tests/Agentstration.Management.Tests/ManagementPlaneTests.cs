using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Agentstration.Management.Core;
using Agentstration.Domain;
using Agentstration.Management.Abstractions;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Agents;
using Agentstration.Management.Contracts;
using Agentstration.Management.Storage.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ManagementPlaneTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void ManagementRoutesAreOwnedByIndependentMinimalApiEndpointClasses()
    {
        string[] expectedEndpoints =
        [
            "CreateAgentRevisionEndpoint",
            "CreateDeploymentEndpoint",
            "DeleteAgentEndpoint",
            "GetAgentEndpoint",
            "GetAgentTypeEndpoint",
            "GetDeploymentEndpoint",
            "ListAgentsEndpoint",
            "ListAgentTypesEndpoint",
            "PutAgentEndpoint",
            "PutAgentTypeEndpoint",
            "ReconcileDeploymentEndpoint",
            "RouteAndExecuteEndpoint",
            "StartDeploymentEndpoint",
            "StopDeploymentEndpoint"
        ];
        Array.Sort(expectedEndpoints, StringComparer.Ordinal);

        var endpointTypes = typeof(Program).Assembly.GetTypes()
            .Where(type => type.Namespace == "Agentstration.Web.Api.Management" && type.IsClass && type.Name.EndsWith("Endpoint", StringComparison.Ordinal))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(expectedEndpoints, endpointTypes.Select(type => type.Name).ToArray());
        Assert.IsTrue(endpointTypes.All(type => type.IsSealed));
        Assert.IsTrue(endpointTypes.All(type => type.GetMethod("Map", BindingFlags.Public | BindingFlags.Static) is not null));
    }

    [TestMethod]
    public void CompilerIsDeterministicAndOrdersTools()
    {
        var compiler = new AgentDefinitionCompiler();
        var type = CreateType() with
        {
            RequiredToolIds = ["zeta", "alpha"],
            AllowedToolIds = ["extra"],
            Policy = CreatePolicy() with { AllowAdditionalTools = true }
        };
        var agent = AgentResource("default", CreateAgent("sql-expert", type) with { Tools = [ToolReference("extra"), ToolReference("alpha")] });
        var spec = LocalSpec();

        var first = compiler.Compile(type, agent, spec);
        var second = compiler.Compile(type, agent, spec);

        Assert.AreEqual(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.AreEqual(first.DefinitionHash, second.DefinitionHash);
        CollectionAssert.AreEquivalent(new[] { "alpha", "zeta", ToolReference("extra").ResourceId, ToolReference("alpha").ResourceId }, first.EffectiveToolIds.ToArray());
    }

    [TestMethod]
    public void CompilerRejectsForbiddenOverridesAndTools()
    {
        var compiler = new AgentDefinitionCompiler();
        var type = CreateType() with { AllowedToolIds = ["safe-tool"], Policy = CreatePolicy() };
        var instructionOverride = AgentResource("default", CreateAgent("sql-expert", type) with { AdditionalInstructions = "Override" });
        var toolType = type with { Policy = type.Policy with { AllowAdditionalTools = true } };
        var toolOverride = AgentResource("default", CreateAgent("sql-expert", toolType) with { Tools = [ToolReference("unsafe-tool")] });

        var instructionsError = Assert.Throws<AgentDefinitionValidationException>(() => compiler.Compile(type, instructionOverride, LocalSpec()));
        var toolsError = Assert.Throws<AgentDefinitionValidationException>(() => compiler.Compile(toolType, toolOverride, LocalSpec()));
        Assert.AreEqual("instructions_override_forbidden", instructionsError.Code);
        Assert.AreEqual("tool_not_allowed", toolsError.Code);
    }

    [TestMethod]
    public async Task StandaloneVerticalPersistsReconcilesRoutesExecutesAndStops()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var management = fixture.Services.GetRequiredService<AgentManagementService>();
        var store = fixture.Services.GetRequiredService<IControlPlaneStore>();
        var typeResource = TypeResource("default", "readonly-expert", CreateType() with
        {
            BehaviorIds = ["technical", "read-only"],
            Policy = CreatePolicy() with { AllowAdditionalInstructions = true }
        });
        await management.PutAgentTypeAsync(typeResource, null, true, default);

        var dotnet = AgentResource("default", CreateAgent("dotnet-expert", typeResource.Properties) with
        {
            Description = "Specialized in .NET, C#, ASP.NET Core, and runtime diagnostics.",
            AdditionalInstructions = "Focus on .NET."
        });
        var sql = AgentResource("default", CreateAgent("sql-expert", typeResource.Properties) with
        {
            Description = "Specialized in SQL query performance and database diagnostics.",
            AdditionalInstructions = "Focus on SQL."
        });
        await management.PutAgentAsync(dotnet, null, true, default);
        var storedSql = await management.PutAgentAsync(sql, null, true, default);

        var dotnetRevision = await management.CreateRevisionAsync(dotnet.Id, LocalSpec(), default);
        var sqlRevision = await management.CreateRevisionAsync(sql.Id, LocalSpec(), default);
        var dotnetDeployment = await management.CreateDeploymentAsync("default", "dotnet-expert", "local", dotnetRevision.Value.Id, LocalSpec(), default);
        var sqlDeployment = await management.CreateDeploymentAsync("default", "sql-expert", "local", sqlRevision.Value.Id, LocalSpec(), default);
        dotnetDeployment = await management.ReconcileAsync(dotnetDeployment, default);
        sqlDeployment = await management.ReconcileAsync(sqlDeployment, default);

        Assert.AreEqual(OperationalState.Ready, dotnetDeployment.Value.OperationalState);
        Assert.AreEqual(sqlRevision.Value.Id, sqlDeployment.Value.ObservedRevisionId);
        var routed = await management.RouteAndExecuteAsync("How can I optimize this SQL query?", default);
        Assert.AreEqual("sql-expert", routed.Route.AgentId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(routed.Execution.Output));

        storedSql = await management.PutAgentAsync(
            storedSql.Value with { Properties = storedSql.Value.Properties with { AdditionalInstructions = "Focus on SQL Server query plans." } },
            storedSql.ETag,
            false,
            default);
        var secondRevision = await management.CreateRevisionAsync(sql.Id, LocalSpec(), default);
        sqlDeployment = await store.PutAsync(
            sqlDeployment.Value with { RevisionId = secondRevision.Value.Id, ProvisioningState = ProvisioningState.Accepted, OperationalState = OperationalState.Starting },
            sqlDeployment.ETag,
            false,
            default);
        sqlDeployment = await management.ReconcileAsync(sqlDeployment, default);
        Assert.AreEqual(secondRevision.Value.Id, sqlDeployment.Value.ObservedRevisionId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutAsync(sqlRevision.Value with { DefinitionHash = "tampered" }, sqlRevision.ETag, false, default));
        var stopped = await management.StopAsync(sqlDeployment, default);
        stopped = await management.ReconcileAsync(stopped, default);
        Assert.AreEqual(OperationalState.Stopped, stopped.Value.OperationalState);

        var persisted = await store.GetAsync<AgentDeployment>(stopped.Value.Id, default);
        Assert.AreEqual(OperationalState.Stopped, persisted!.Value.OperationalState);
    }

    [TestMethod]
    public async Task SqliteStoreEnforcesOptimisticConcurrencyWithETag()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var store = fixture.Services.GetRequiredService<IControlPlaneStore>();
        var resource = TypeResource("default", "readonly-expert", CreateType());
        var created = await store.PutAsync(resource, null, true, default);

        await Assert.ThrowsAsync<ControlPlaneConcurrencyException>(() =>
            store.PutAsync(resource with { Location = "other" }, "\"stale\"", false, default));
        var updated = await store.PutAsync(resource with { Location = "other" }, created.ETag, false, default);

        Assert.AreNotEqual(created.ETag, updated.ETag);
        Assert.AreEqual("other", (await store.GetAsync<AgentTypeResource>(resource.Id, default))!.Value.Location);
    }

    [TestMethod]
    public async Task ManagementApiRequiresApiVersionAndHonorsIfMatch()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var path = $"/resourceGroups/default/providers/Agentstration.Agents/agentTypes/readonly-expert?api-version={ManagementApiVersions.V20260801}";
        var body = new ResourceEnvelope<AgentTypeDefinition>("local", null, CreateType());
        using var created = await client.PutAsJsonAsync(path, body);
        created.EnsureSuccessStatusCode();
        var etag = created.Headers.ETag?.Tag;
        Assert.IsNotNull(etag);
        var page = await client.GetFromJsonAsync<PagedResponse<AgentTypeResource>>($"/resourceGroups/default/providers/Agentstration.Agents/agentTypes?api-version={ManagementApiVersions.V20260801}");
        Assert.IsNotNull(page);
        Assert.IsTrue(page.Value.Any(value => value.Name == "readonly-expert"));

        using var staleRequest = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) };
        staleRequest.Headers.TryAddWithoutValidation("If-Match", "\"stale\"");
        using var stale = await client.SendAsync(staleRequest);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        using var missingVersion = await client.GetAsync("/resourceGroups/default/providers/Agentstration.Agents/agentTypes/readonly-expert");
        Assert.AreEqual(HttpStatusCode.BadRequest, missingVersion.StatusCode);
    }

    [TestMethod]
    public async Task AgentPutIsIdempotentTracksGenerationAndPublishesLifecycleEvents()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var management = fixture.Services.GetRequiredService<AgentManagementService>();
        var type = TypeResource("default", "readonly-expert", CreateType());
        await management.PutAgentTypeAsync(type, null, true, default);
        var desired = AgentResource("default", CreateAgent("sql-expert", type.Properties));

        var created = await management.PutAgentAsync(desired, null, true, default);
        var unchanged = await management.PutAgentAsync(created.Value, created.ETag, false, default);
        var updated = await management.PutAgentAsync(
            created.Value with { Properties = created.Value.Properties with { Description = "Updated declaration" } },
            created.ETag,
            false,
            default);

        Assert.AreEqual(1L, created.Value.Generation);
        Assert.AreEqual(created.ETag, unchanged.ETag);
        Assert.AreEqual(1L, unchanged.Value.Generation);
        Assert.AreEqual(2L, updated.Value.Generation);
        Assert.AreEqual(ProvisioningState.Accepted, updated.Value.Status.ProvisioningState);
        Assert.AreEqual(updated.ETag, updated.Value.Status.ResourceVersion);
        Assert.AreEqual(1, fixture.Events.Created.Count);
        Assert.AreEqual(1, fixture.Events.Updated.Count);
        Assert.AreEqual(2L, fixture.Events.Updated[0].Generation);

        await management.DeleteAgentAsync(updated.Value.Id, updated.ETag, default);

        Assert.AreEqual(1, fixture.Events.Deleted.Count);
        Assert.IsNull(await management.GetAgentAsync(updated.Value.Id, default));
    }

    [TestMethod]
    public async Task AgentValidationRejectsInvalidResourceShapeAndReferences()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var management = fixture.Services.GetRequiredService<AgentManagementService>();
        var type = TypeResource("default", "readonly-expert", CreateType() with
        {
            AllowedToolIds = ["sql-readonly"],
            Policy = CreatePolicy() with { AllowAdditionalTools = true }
        });
        await management.PutAgentTypeAsync(type, null, true, default);
        var valid = AgentResource("default", CreateAgent("sql-expert", type.Properties));

        await AssertCodeAsync("resource_type_mismatch", valid with { Type = "Agentstration.Agents/notAgents" });
        await AssertCodeAsync("api_version_not_supported", valid with { ApiVersion = "2025-01-01" });
        await AssertCodeAsync("resource_id_mismatch", valid with { Name = "other" });
        await AssertCodeAsync("agent_type_reference_invalid", valid with
        {
            Properties = valid.Properties with { AgentType = new AgentTypeReference("not-a-resource-id", 1) }
        });
        await AssertCodeAsync("model_profile_reference_invalid", valid with
        {
            Properties = valid.Properties with { ModelProfile = new ResourceReference(ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Tools, "tools", "wrong").Value) }
        });
        await AssertCodeAsync("tool_reference_invalid", valid with
        {
            Properties = valid.Properties with { Tools = [new ResourceReference(ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Models, "modelProfiles", "wrong").Value)] }
        });
        await AssertCodeAsync("duplicate_tool_reference", valid with
        {
            Properties = valid.Properties with { Tools = [ToolReference("sql-readonly"), ToolReference("sql-readonly")] }
        });

        async Task AssertCodeAsync(string code, AgentResource resource)
        {
            var error = await Assert.ThrowsAsync<AgentDefinitionValidationException>(() => management.PutAgentAsync(resource, null, false, default));
            Assert.AreEqual(code, error.Code);
        }
    }

    [TestMethod]
    public async Task AgentApiRejectsRouteAndDocumentMismatches()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var typePath = $"/resourceGroups/default/providers/Agentstration.Agents/agentTypes/readonly-expert?api-version={ManagementApiVersions.V20260801}";
        using var typeResponse = await client.PutAsJsonAsync(typePath, new ResourceEnvelope<AgentTypeDefinition>("local", null, CreateType()));
        typeResponse.EnsureSuccessStatusCode();
        var path = $"/resourceGroups/default/providers/Agentstration.Agents/agents/sql-expert?api-version={ManagementApiVersions.V20260801}";
        var body = AgentRequest("sql-expert", "default");

        using var valid = await client.PutAsJsonAsync(path, body);
        valid.EnsureSuccessStatusCode();
        using var wrongName = await client.PutAsJsonAsync(path, body with { Name = "other" });
        using var wrongGroup = await client.PutAsJsonAsync(path, body with { ResourceGroup = "other" });

        Assert.AreEqual(HttpStatusCode.BadRequest, wrongName.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongGroup.StatusCode);
    }

    [TestMethod]
    public void ResourceIdentifierParsesAndReconstructsCanonicalIds()
    {
        var value = "/resourceGroups/default/providers/Agentstration.Agents/agents/sql-expert";
        var parsed = ResourceIdentifier.Parse(value);

        Assert.AreEqual("default", parsed.ResourceGroup);
        Assert.AreEqual(AgentstrationProviderNamespaces.Agents, parsed.ProviderNamespace);
        Assert.AreEqual("agents", parsed.ResourceType);
        Assert.AreEqual("sql-expert", parsed.Name);
        Assert.AreEqual(value, parsed.Value);
        Assert.IsFalse(ResourceIdentifier.TryParse("resourceGroups/default/agents/sql-expert", out _));
    }

    [TestMethod]
    public void AgentResourceSerializesOnlyTheCurrentCanonicalSchema()
    {
        var resource = AgentResource("default", CreateAgent("dotnet-expert", CreateType()));
        var json = JsonSerializer.Serialize(resource, JsonOptions);
        using var document = JsonDocument.Parse(json);
        var properties = document.RootElement.GetProperty("properties");

        Assert.IsTrue(properties.TryGetProperty("agentType", out _));
        Assert.IsTrue(properties.TryGetProperty("modelProfile", out _));
        Assert.IsTrue(properties.TryGetProperty("tools", out _));
        Assert.IsFalse(properties.TryGetProperty("modelProfileOverride", out _));
        Assert.IsFalse(properties.TryGetProperty("additionalToolIds", out _));
        Assert.IsFalse(properties.TryGetProperty("type", out _));
    }

    private static AgentTypeDefinition CreateType() => new()
    {
        Key = "readonly-expert",
        Version = 1,
        Handler = "prompt-agent",
        BaseInstructions = "You are a specialized read-only expert.",
        DefaultModelProfileId = "reasoning-default",
        Policy = CreatePolicy()
    };

    private static AgentTypePolicy CreatePolicy() => new() { MaximumAdditionalInstructionsLength = 10_000 };

    private static AgentProperties CreateAgent(string key, AgentTypeDefinition type) => new()
    {
        DisplayName = key,
        Description = key,
        AgentType = new AgentTypeReference(ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Agents, "agentTypes", type.Key).Value, type.Version),
        ModelProfile = new ResourceReference(ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Models, "modelProfiles", type.DefaultModelProfileId).Value)
    };

    private static AgentTypeResource TypeResource(string resourceGroup, string name, AgentTypeDefinition definition) => new()
    {
        Id = ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Agents, "agentTypes", name).Value,
        Name = name,
        Type = AgentstrationResourceTypes.AgentTypes,
        ApiVersion = ManagementApiVersions.V20260801,
        ResourceGroup = resourceGroup,
        Location = "local",
        Properties = definition
    };

    private static AgentResource AgentResource(string resourceGroup, AgentProperties definition) => new()
    {
        Id = ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Agents, "agents", definition.DisplayName).Value,
        Name = definition.DisplayName,
        Type = AgentstrationResourceTypes.Agents,
        ApiVersion = ManagementApiVersions.V20260801,
        ResourceGroup = resourceGroup,
        Location = "local",
        Properties = definition
    };

    private static ResourceReference ToolReference(string name) => new(ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Tools, "tools", name).Value);

    private static AgentResourceRequest AgentRequest(string name, string resourceGroup) => new()
    {
        Type = AgentstrationResourceTypes.Agents,
        ApiVersion = ManagementApiVersions.V20260801,
        Name = name,
        ResourceGroup = resourceGroup,
        Location = "local",
        Properties = CreateAgent(name, CreateType())
    };

    private static AgentDeploymentSpec LocalSpec() => new() { Environment = "local", RuntimeProfileId = RuntimeProfileManagementService.ProfileId("default", "maf-default"), HostingMode = AgentHostingMode.InProcess };

    private sealed class ManagementFixture : IAsyncDisposable
    {
        private readonly string _directory;
        public ServiceProvider Services { get; }
        public ManagementEventRecorder Events { get; }

        private ManagementFixture(string directory, ServiceProvider services, ManagementEventRecorder events)
        {
            _directory = directory;
            Services = services;
            Events = events;
        }

        public static async Task<ManagementFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"agentstration-management-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAgentstration(
                Path.Combine(directory, "data.json"),
                inMemory: true,
                new AiProviderOptions("Deterministic", new Uri("http://localhost/"), "deterministic", null),
                $"Data Source={Path.Combine(directory, "control-plane.db")};Pooling=False");
            var events = new ManagementEventRecorder();
            services.AddSingleton(events);
            services.AddSingleton<IManagementEventHandler<AgentCreated>>(events);
            services.AddSingleton<IManagementEventHandler<AgentUpdated>>(events);
            services.AddSingleton<IManagementEventHandler<AgentDeleted>>(events);
            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<AgentManagementService>().InitializeAsync(default);
            var runtimeProfiles = provider.GetRequiredService<RuntimeProfileManagementService>();
            var runtimeProfileId = RuntimeProfileManagementService.ProfileId("default", "maf-default");
            await runtimeProfiles.CreateAsync(new RuntimeProfileResource
            {
                Id = runtimeProfileId,
                Name = "maf-default",
                Type = AgentstrationResourceTypes.RuntimeProfiles,
                ApiVersion = ManagementApiVersions.V20260801,
                ResourceGroup = "default",
                Location = "local",
                Properties = new RuntimeProfileProperties { DisplayName = "MAF default", RuntimeType = "microsoft-agent-framework" }
            }, default);
            return new ManagementFixture(directory, provider, events);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ManagementEventRecorder : IManagementEventHandler<AgentCreated>, IManagementEventHandler<AgentUpdated>, IManagementEventHandler<AgentDeleted>
    {
        public List<AgentCreated> Created { get; } = [];
        public List<AgentUpdated> Updated { get; } = [];
        public List<AgentDeleted> Deleted { get; } = [];

        public Task HandleAsync(AgentCreated domainEvent, CancellationToken cancellationToken) { Created.Add(domainEvent); return Task.CompletedTask; }
        public Task HandleAsync(AgentUpdated domainEvent, CancellationToken cancellationToken) { Updated.Add(domainEvent); return Task.CompletedTask; }
        public Task HandleAsync(AgentDeleted domainEvent, CancellationToken cancellationToken) { Deleted.Add(domainEvent); return Task.CompletedTask; }
    }
}
