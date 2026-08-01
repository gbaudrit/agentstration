using Agentstration.Management.Abstractions;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Local;
using Agentstration.Runtime.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using Agentstration.Runtime.Contracts;
using Agentstration.Work.Contracts;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class RuntimeRunTests
{
    [TestMethod]
    public async Task CreatePreservesPayloadAndRequiresExactAgentVersion()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var input = Input("Optimize this query.", "{\"engine\":\"sqlserver\"}");

        var created = await fixture.Service.CreateAsync(new RuntimeAgentReference(fixture.AgentId, 3), input, new RuntimeExecutionOptions(), RuntimeRunOrigin.Console, "operator", default);

        Assert.AreEqual(RuntimeRunState.Pending, created.Value.Status.State);
        Assert.AreEqual(3L, created.Value.Properties.Agent.Version);
        Assert.AreEqual("Optimize this query.", created.Value.Properties.Input.Messages[0].Content);
        Assert.AreEqual(RuntimeRunOrigin.Console, created.Value.Properties.Origin);
        await Assert.ThrowsAsync<RuntimeRunValidationException>(() => fixture.Service.CreateAsync(new RuntimeAgentReference(fixture.AgentId, 2), input, new RuntimeExecutionOptions(), RuntimeRunOrigin.Api, "api", default));
    }

    [TestMethod]
    public async Task ExecutionPersistsProgressiveEventsAndSucceededResponse()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var created = await fixture.CreateRunAsync();

        await fixture.Service.ExecuteAsync(created.Value.Id, default);
        var completed = await fixture.Service.GetAsync(created.Value.Id, default);
        var events = await fixture.Store.ListEventsAsync(created.Value.Id, 0, default);

        Assert.IsNotNull(completed);
        Assert.AreEqual(RuntimeRunState.Succeeded, completed.Value.Status.State);
        Assert.AreEqual("runtime response", completed.Value.Status.Response);
        Assert.IsTrue(events.Any(item => item.Kind == RuntimeRunEventKind.ResponseDelta));
        Assert.IsTrue(events.Any(item => item.Kind == RuntimeRunEventKind.StepCompleted && item.Step == "Model profile resolved"));
        Assert.AreEqual(RuntimeRunEventKind.RunCompleted, events[^1].Kind);
    }

    [TestMethod]
    public async Task FailureAndCancellationReachExplicitTerminalStates()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Registry.Behavior = RuntimeBehavior.Fail;
        var failed = await fixture.CreateRunAsync();
        await fixture.Service.ExecuteAsync(failed.Value.Id, default);
        Assert.AreEqual(RuntimeRunState.Failed, (await fixture.Service.GetAsync(failed.Value.Id, default))!.Value.Status.State);

        fixture.Registry.Behavior = RuntimeBehavior.Block;
        var running = await fixture.CreateRunAsync();
        var execution = fixture.Service.ExecuteAsync(running.Value.Id, default);
        await fixture.Registry.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Service.CancelAsync(running.Value.Id, default);
        await execution;

        Assert.AreEqual(RuntimeRunState.Cancelled, (await fixture.Service.GetAsync(running.Value.Id, default))!.Value.Status.State);
    }

    [TestMethod]
    public async Task RetryCreatesNewRunAndTerminalEventStreamCloses()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var original = await fixture.CreateRunAsync();
        await fixture.Service.ExecuteAsync(original.Value.Id, default);

        var retry = await fixture.Service.RetryAsync(original.Value.Id, default);
        var observed = new List<RuntimeRunEvent>();
        await foreach (var runEvent in fixture.Service.ObserveAsync(original.Value.Id, 0, default)) observed.Add(runEvent);

        Assert.AreNotEqual(original.Value.Id, retry.Value.Id);
        CollectionAssert.AreEqual(original.Value.Properties.Input.Messages.ToArray(), retry.Value.Properties.Input.Messages.ToArray());
        Assert.AreEqual(original.Value.Properties.Input.Context, retry.Value.Properties.Input.Context);
        Assert.AreEqual(RuntimeRunEventKind.RunCompleted, observed[^1].Kind);
    }

    [TestMethod]
    public void RuntimeCoreDoesNotReferenceWorkPlaneOrWeb()
    {
        var references = typeof(RuntimeRunService).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Work", StringComparison.Ordinal) || name.Contains("Agentstration.Web", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RuntimeApiExecutesAndObservesRunWithoutCreatingWorkItem()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var agentPath = $"/resourceGroups/default/providers/Agentstration.Agents/agents/sql-expert?api-version={ManagementApiVersions.V20260801}";
        var agent = await client.GetFromJsonAsync<AgentResource>(agentPath);
        Assert.IsNotNull(agent);
        var workBefore = await client.GetFromJsonAsync<WorkItemPageResponse>("/api/work/workitems?top=100");
        var request = new CreateRuntimeRunRequest
        {
            Agent = new RuntimeAgentReference(agent.Id, agent.Generation),
            Input = Input("integration prompt"),
            Origin = RuntimeRunOrigin.Api
        };

        using var createdResponse = await client.PostAsJsonAsync("/api/runtime/runs", request);
        Assert.AreEqual(HttpStatusCode.Accepted, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<RuntimeRun>();
        Assert.IsNotNull(created);

        RuntimeRun? completed = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            completed = await client.GetFromJsonAsync<RuntimeRun>($"/api/runtime/runs/{created.Id}");
            if (completed!.Status.State.IsTerminal()) break;
            await Task.Delay(100);
        }
        var eventStream = await client.GetStringAsync($"/api/runtime/runs/{created.Id}/events");
        var workAfter = await client.GetFromJsonAsync<WorkItemPageResponse>("/api/work/workitems?top=100");

        Assert.AreEqual(RuntimeRunState.Succeeded, completed!.Status.State);
        StringAssert.Contains(eventStream, "event: ResponseDelta");
        StringAssert.Contains(eventStream, "event: RunCompleted");
        Assert.AreEqual(workBefore!.Value.Count, workAfter!.Value.Count);
    }

    private static RuntimeRunInput Input(string prompt, string? context = null) => new()
    {
        Messages = [new RuntimeRunMessage(RuntimeMessageRole.User, prompt)],
        Context = context
    };

    private enum RuntimeBehavior { Succeed, Fail, Block }

    private sealed class FakeRuntimeRegistry : IRuntimeRegistry
    {
        public RuntimeBehavior Behavior { get; set; }
        public TaskCompletionSource ExecutionStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Set(string deploymentId, IAgentRuntime runtime) { }
        public bool TryGet(string deploymentId, out IAgentRuntime? runtime) { runtime = null; return false; }
        public bool Remove(string deploymentId) => false;
        public async Task<AgentExecutionResult> ExecuteAsync(string deploymentId, AgentExecutionRequest request, CancellationToken cancellationToken)
        {
            ExecutionStarted.TrySetResult();
            if (Behavior == RuntimeBehavior.Fail) throw new InvalidOperationException("runtime failed");
            if (Behavior == RuntimeBehavior.Block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new AgentExecutionResult("runtime response", request.SessionId);
        }
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string directory;
        private readonly ServiceProvider provider;
        public RuntimeRunService Service { get; }
        public IRuntimeRunStore Store { get; }
        public FakeRuntimeRegistry Registry { get; }
        public string AgentId { get; }

        private RuntimeFixture(string directory, ServiceProvider provider, RuntimeRunService service, IRuntimeRunStore store, FakeRuntimeRegistry registry, string agentId)
        {
            this.directory = directory;
            this.provider = provider;
            Service = service;
            Store = store;
            Registry = registry;
            AgentId = agentId;
        }

        public static async Task<RuntimeFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"agentstration-runtime-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSqliteControlPlane($"Data Source={Path.Combine(directory, "management.db")}");
            services.AddSqliteRuntimeRuns($"Data Source={Path.Combine(directory, "runtime.db")}");
            services.AddSingleton<IRuntimeRunQueue, LocalRuntimeRunQueue>();
            services.AddSingleton<IRuntimeRunCancellationRegistry, LocalRuntimeRunCancellationRegistry>();
            var registry = new FakeRuntimeRegistry();
            services.AddSingleton(registry);
            services.AddSingleton<IRuntimeRegistry>(registry);
            services.AddSingleton<RuntimeRunService>();
            var provider = services.BuildServiceProvider();
            var management = provider.GetRequiredService<IControlPlaneStore>();
            var store = provider.GetRequiredService<IRuntimeRunStore>();
            await management.InitializeAsync(default);
            await store.InitializeAsync(default);

            var agentId = ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Agents, "agents", "sql-expert").Value;
            var revisionId = ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Agents, "agentRevisions", "sql-expert--000001").Value;
            await management.PutAsync(Agent(agentId), null, true, default);
            await management.CreateImmutableAsync(Revision(revisionId, agentId), default);
            await management.PutAsync(Deployment(revisionId), null, true, default);
            return new RuntimeFixture(directory, provider, provider.GetRequiredService<RuntimeRunService>(), store, registry, agentId);
        }

        public Task<StoredRuntimeRun> CreateRunAsync() => Service.CreateAsync(new RuntimeAgentReference(AgentId, 3), Input("test prompt"), new RuntimeExecutionOptions(), RuntimeRunOrigin.Api, "test", default);

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        private static AgentResource Agent(string id) => new()
        {
            Id = id,
            Name = "sql-expert",
            Type = AgentstrationResourceTypes.Agents,
            ApiVersion = ManagementApiVersions.V20260801,
            ResourceGroup = "default",
            Location = "local",
            Generation = 3,
            Properties = new AgentProperties
            {
                DisplayName = "SQL Expert",
                AgentType = new AgentTypeReference(ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Agents, "agentTypes", "readonly-expert").Value, 1),
                ModelProfile = new ResourceReference(ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Models, "modelProfiles", "reasoning-default").Value)
            }
        };

        private static AgentRevision Revision(string id, string agentId) => new()
        {
            Id = id,
            Name = "sql-expert--000001",
            Type = AgentstrationResourceTypes.AgentRevisions,
            ApiVersion = ManagementApiVersions.V20260801,
            ResourceGroup = "default",
            Location = "local",
            AgentResourceId = agentId,
            AgentVersion = 3,
            AgentTypeVersion = 1,
            DefinitionHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow,
            ProvisioningState = ProvisioningState.Succeeded,
            Definition = new ResolvedAgentDefinition
            {
                AgentId = Guid.NewGuid(), AgentKey = "sql-expert", DisplayName = "SQL Expert", Description = "Test", AgentVersion = 3,
                EffectiveInstructions = "Test", ModelProfileId = "reasoning-default", RuntimeProfileId = "standalone", EffectiveToolIds = [], MiddlewareIds = [], ContextProviderIds = [], Capabilities = [], Handler = "prompt-agent", DefinitionHash = "hash"
            }
        };

        private static AgentDeployment Deployment(string revisionId) => new()
        {
            Id = ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Agents, "deployments", "sql-expert").Value,
            Name = "sql-expert",
            Type = AgentstrationResourceTypes.Deployments,
            ApiVersion = ManagementApiVersions.V20260801,
            ResourceGroup = "default",
            Location = "local",
            RevisionId = revisionId,
            Environment = "local",
            RuntimeProfileId = "standalone",
            HostingMode = AgentHostingMode.InProcess,
            DesiredState = DesiredAgentState.Running,
            ProvisioningState = ProvisioningState.Succeeded,
            OperationalState = OperationalState.Ready,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}
