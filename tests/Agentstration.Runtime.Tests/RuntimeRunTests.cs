using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Local;
using Agentstration.Runtime.Storage.Sqlite;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class RuntimeRunTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task OpaqueRuntimeExecutionStateSurvivesStoreReconstruction()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-runtime-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "runtime.db");
        try
        {
            static ServiceProvider Provider(string path)
            {
                var services = new ServiceCollection();
                services.AddSingleton(TimeProvider.System);
                services.AddSqliteRuntimeRuns($"Data Source={path}");
                return services.BuildServiceProvider();
            }

            await using (var first = Provider(databasePath))
            {
                await first.GetRequiredService<IRuntimeRunStore>().InitializeAsync(default);
                await first.GetRequiredService<IRuntimeExecutionStateStore>().StoreAsync(new(
                    TestScope.WorkspaceId, "run-opaque", "maf", "checkpoint-2", JsonSerializer.SerializeToElement(new { opaque = "payload" }),
                    new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero), "checkpoint-1"), default);
            }

            await using (var second = Provider(databasePath))
            {
                await second.GetRequiredService<IRuntimeRunStore>().InitializeAsync(default);
                var restored = await second.GetRequiredService<IRuntimeExecutionStateStore>()
                    .GetAsync(TestScope.WorkspaceId, "run-opaque", "maf", "checkpoint-2", default);
                Assert.IsNotNull(restored);
                Assert.AreEqual("checkpoint-1", restored.ParentStateId);
                Assert.AreEqual("payload", restored.Payload.GetProperty("opaque").GetString());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
    [TestMethod]
    public async Task CreatePreservesPayloadAndRequiresExactAgentVersion()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var input = Input("Optimize this query.", "{\"engine\":\"sqlserver\"}");

        var created = await fixture.Service.CreateAsync(TestScope, new RuntimeAgentReference(fixture.AgentId, 3), input, new RuntimeExecutionOptions(), RuntimeRunOrigin.Console, "operator", default);

        Assert.AreEqual(RuntimeRunState.Pending, created.Value.Status.State);
        Assert.AreEqual(3L, created.Value.Properties.Agent.Version);
        Assert.AreEqual("Optimize this query.", created.Value.Properties.Input.Messages[0].Content);
        Assert.AreEqual(RuntimeRunOrigin.Console, created.Value.Properties.Origin);
        Assert.AreEqual(TestScope, created.Value.Properties.Scope);
        var missingVersion = await Assert.ThrowsAsync<RuntimeRunValidationException>(() => fixture.Service.CreateAsync(TestScope, new RuntimeAgentReference(fixture.AgentId, 2), input, new RuntimeExecutionOptions(), RuntimeRunOrigin.Api, "api", default));
        Assert.AreEqual("agent_version_not_found", missingVersion.Code);
        var invalidReference = await Assert.ThrowsAsync<RuntimeRunValidationException>(() => fixture.Service.CreateAsync(TestScope, new RuntimeAgentReference("", 1), input, new RuntimeExecutionOptions(), RuntimeRunOrigin.Api, "api", default));
        Assert.AreEqual("agent_reference_invalid", invalidReference.Code);
        var invalidVersion = await Assert.ThrowsAsync<RuntimeRunValidationException>(() => fixture.Service.CreateAsync(TestScope, new RuntimeAgentReference(fixture.AgentId, 0), input, new RuntimeExecutionOptions(), RuntimeRunOrigin.Api, "api", default));
        Assert.AreEqual("agent_version_invalid", invalidVersion.Code);
    }

    [TestMethod]
    public async Task ExecutionPersistsProgressiveEventsAndSucceededResponse()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var created = await fixture.CreateRunAsync();

        await fixture.Service.ExecuteAsync(new(TestScope, created.Value.Id), default);
        var completed = await fixture.Service.GetAsync(TestScope.WorkspaceId, created.Value.Id, default);
        var events = await fixture.Store.ListEventsAsync(TestScope.WorkspaceId, created.Value.Id, 0, default);

        Assert.IsNotNull(completed);
        Assert.AreEqual(RuntimeRunState.Succeeded, completed.Value.Status.State);
        Assert.AreEqual("runtime response", completed.Value.Status.Response);
        Assert.AreEqual("ollama", completed.Value.Status.ModelProvider);
        Assert.AreEqual("qwen3:1.7b", completed.Value.Status.ResolvedModel);
        Assert.IsTrue(events.Any(item => item.Kind == RuntimeRunEventKind.ResponseDelta));
        Assert.IsTrue(events.Any(item => item.Kind == RuntimeRunEventKind.StepCompleted && item.Step == "Model profile resolved"));
        Assert.AreEqual(RuntimeRunEventKind.RunCompleted, events[^1].Kind);
    }

    [TestMethod]
    public async Task RuntimeRunScopeIsImmutable()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var created = await fixture.CreateRunAsync();
        var changedScope = TestScope with { WorkspaceId = new(Guid.NewGuid()) };

        var mutation = await Assert.ThrowsAsync<RuntimeRunConcurrencyException>(() => fixture.Store.UpdateAsync(
            created.Value with { Scope = changedScope, Properties = created.Value.Properties with { Scope = changedScope } },
            created.ETag,
            default));
        StringAssert.Contains(mutation.Message, "scope is immutable");
    }

    [TestMethod]
    public async Task ExecutionEmitsCorrelatedRuntimeActivityWithoutPromptContent()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Agentstration.Runtime",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        await using var fixture = await RuntimeFixture.CreateAsync();
        var created = await fixture.CreateRunAsync();

        await fixture.Service.ExecuteAsync(new(TestScope, created.Value.Id), default);

        Assert.HasCount(1, stopped);
        var activity = stopped.Single();
        Assert.AreEqual(created.Value.Id, activity.GetTagItem("agentstration.run.id"));
        Assert.AreEqual(fixture.AgentId, activity.GetTagItem("agentstration.agent.id"));
        Assert.IsFalse(activity.TagObjects.Any(tag => tag.Value?.ToString()?.Contains("test prompt", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public async Task ExecutionAppliesValidatedOverridesAndReportsReadiness()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var options = new RuntimeExecutionOptions
        {
            PersistToolArguments = true,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["temperature"] = JsonSerializer.SerializeToElement(0.6f),
                ["maxOutputTokens"] = JsonSerializer.SerializeToElement(750)
            }
        };
        var created = await fixture.Service.CreateAsync(TestScope, new RuntimeAgentReference(fixture.AgentId, 3), Input("test prompt"), options, RuntimeRunOrigin.Console, "operator", default);

        var readiness = await fixture.Service.GetReadinessAsync(fixture.AgentId, 3, default);
        await fixture.Service.ExecuteAsync(new(TestScope, created.Value.Id), default);
        var completed = await fixture.Service.GetAsync(TestScope.WorkspaceId, created.Value.Id, default);

        Assert.IsTrue(readiness.Ready);
        Assert.AreEqual(0.6f, fixture.Registry.LastRequest?.Options?.Temperature);
        Assert.AreEqual(750, fixture.Registry.LastRequest?.Options?.MaxOutputTokens);
        Assert.AreEqual(true, fixture.Registry.LastRequest?.ToolExecution?.PersistArguments);
        Assert.AreEqual(0.6f, completed!.Value.Status.EffectiveTemperature);
        Assert.AreEqual(750, completed.Value.Status.EffectiveMaxOutputTokens);
    }

    [TestMethod]
    public async Task UnsupportedRuntimeParameterIsRejectedBeforeRunCreation()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var options = new RuntimeExecutionOptions
        {
            Parameters = new Dictionary<string, JsonElement> { ["model"] = JsonSerializer.SerializeToElement("other-model") }
        };

        var exception = await Assert.ThrowsAsync<RuntimeRunValidationException>(() => fixture.Service.CreateAsync(TestScope,
            new RuntimeAgentReference(fixture.AgentId, 3), Input("test prompt"), options, RuntimeRunOrigin.Console, "operator", default));

        Assert.AreEqual("runtime_parameter_unsupported", exception.Code);
    }

    [TestMethod]
    public async Task FailureAndCancellationReachExplicitTerminalStates()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Registry.Behavior = RuntimeBehavior.Fail;
        var failed = await fixture.CreateRunAsync();
        await fixture.Service.ExecuteAsync(new(TestScope, failed.Value.Id), default);
        Assert.AreEqual(RuntimeRunState.Failed, (await fixture.Service.GetAsync(TestScope.WorkspaceId, failed.Value.Id, default))!.Value.Status.State);

        fixture.Registry.Behavior = RuntimeBehavior.Block;
        var running = await fixture.CreateRunAsync();
        var execution = fixture.Service.ExecuteAsync(new(TestScope, running.Value.Id), default);
        await fixture.Registry.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Service.CancelAsync(TestScope.WorkspaceId, running.Value.Id, default);
        await execution;

        Assert.AreEqual(RuntimeRunState.Cancelled, (await fixture.Service.GetAsync(TestScope.WorkspaceId, running.Value.Id, default))!.Value.Status.State);
    }

    [TestMethod]
    public async Task TimeoutReachesExplicitTerminalState()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Registry.Behavior = RuntimeBehavior.Block;
        var run = await fixture.Service.CreateAsync(TestScope,
            new RuntimeAgentReference(fixture.AgentId, 3),
            Input("timeout"),
            new RuntimeExecutionOptions { TimeoutSeconds = 1 },
            RuntimeRunOrigin.Api,
            "test",
            default);

        await fixture.Service.ExecuteAsync(new(TestScope, run.Value.Id), default);

        Assert.AreEqual(RuntimeRunState.TimedOut, (await fixture.Service.GetAsync(TestScope.WorkspaceId, run.Value.Id, default))!.Value.Status.State);
    }

    [TestMethod]
    public async Task ConcurrentTerminalTransitionAcceptsTheWinningStateWithoutDuplicateEvents()
    {
        var run = new RuntimeRun
        {
            WorkspaceId = TestScope.WorkspaceId,
            Scope = TestScope,
            Id = "concurrent-terminal",
            Name = "concurrent-terminal",
            Properties = new RuntimeRunProperties
            {
                Agent = new RuntimeAgentReference("sql-expert", 3),
                Input = Input("test"),
                Execution = new RuntimeExecutionOptions()
            },
            Status = new RuntimeRunStatus { State = RuntimeRunState.Running, CreatedAt = DateTimeOffset.UtcNow }
        };
        var store = new ConcurrentTerminalRunStore(run);
        var manager = new RuntimeRunStateManager(store, TimeProvider.System);

        await manager.CompleteFailureAsync(run.WorkspaceId, run.Id, RuntimeRunState.TimedOut, "Run timed out.", default);

        Assert.AreEqual(RuntimeRunState.Cancelled, store.Current.Value.Status.State);
        Assert.AreEqual(1, store.UpdateAttempts);
        Assert.HasCount(0, store.Events);
    }

    [TestMethod]
    public async Task InitializationReenqueuesPendingAndRunningButNotTerminalRuns()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var pending = await fixture.CreateRunAsync();
        var running = await fixture.CreateRunAsync();
        var succeeded = await fixture.CreateRunAsync();
        var failed = await fixture.CreateRunAsync();
        var cancelled = await fixture.CreateRunAsync();
        var timedOut = await fixture.CreateRunAsync();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        running = await fixture.Store.UpdateAsync(running.Value with { Status = running.Value.Status with { State = RuntimeRunState.Running, StartedAt = startedAt } }, running.ETag, default);
        _ = await SetStateAsync(succeeded, RuntimeRunState.Succeeded);
        _ = await SetStateAsync(failed, RuntimeRunState.Failed);
        _ = await SetStateAsync(cancelled, RuntimeRunState.Cancelled);
        _ = await SetStateAsync(timedOut, RuntimeRunState.TimedOut);
        fixture.Queue.Enqueued.Clear();

        await fixture.Service.InitializeAsync(default);

        CollectionAssert.AreEquivalent(
            new[] { new RuntimeRunQueueItem(TestScope, pending.Value.Id), new RuntimeRunQueueItem(TestScope, running.Value.Id) },
            fixture.Queue.Enqueued.ToArray());

        await fixture.Service.ExecuteAsync(new(TestScope, running.Value.Id), default);
        var recovered = await fixture.Service.GetAsync(TestScope.WorkspaceId, running.Value.Id, default);
        Assert.AreEqual(RuntimeRunState.Succeeded, recovered!.Value.Status.State);
        Assert.AreEqual(startedAt, recovered.Value.Status.StartedAt);

        Task<StoredRuntimeRun> SetStateAsync(StoredRuntimeRun stored, RuntimeRunState state) =>
            fixture.Store.UpdateAsync(stored.Value with { Status = stored.Value.Status with { State = state } }, stored.ETag, default);
    }

    [TestMethod]
    public async Task RetryCreatesNewRunAndTerminalEventStreamCloses()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var original = await fixture.Service.CreateAsync(
            TestScope,
            new RuntimeAgentReference(fixture.AgentId, 3),
            Input("test prompt"),
            new RuntimeExecutionOptions { PersistToolArguments = true },
            RuntimeRunOrigin.Console,
            "operator",
            default);
        await fixture.Service.ExecuteAsync(new(TestScope, original.Value.Id), default);

        var retry = await fixture.Service.RetryAsync(TestScope, original.Value.Id, default);
        var observed = new List<RuntimeRunEvent>();
        await foreach (var runEvent in fixture.Service.ObserveAsync(TestScope.WorkspaceId, original.Value.Id, 0, default)) observed.Add(runEvent);

        Assert.AreNotEqual(original.Value.Id, retry.Value.Id);
        CollectionAssert.AreEqual(original.Value.Properties.Input.Messages.ToArray(), retry.Value.Properties.Input.Messages.ToArray());
        Assert.AreEqual(original.Value.Properties.Input.Context, retry.Value.Properties.Input.Context);
        Assert.AreEqual(true, retry.Value.Properties.Execution.PersistToolArguments);
        Assert.AreEqual(RuntimeRunEventKind.RunCompleted, observed[^1].Kind);
    }

    [TestMethod]
    public async Task FreshRuntimeSchemaAllowsTheSameRunIdInDifferentWorkspaces()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-runtime-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "runtime.db");
        try
        {
            var timestamp = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSqliteRuntimeRuns($"Data Source={databasePath}");
            await using var provider = services.BuildServiceProvider();
            var store = provider.GetRequiredService<IRuntimeRunStore>();

            await store.InitializeAsync(default);
            await store.InitializeAsync(default);
            var otherScope = TestScope with { WorkspaceId = new(Guid.Parse("44444444-4444-4444-4444-444444444444")) };
            await store.CreateAsync(Run(TestScope, "first"), default);
            await store.CreateAsync(Run(otherScope, "second"), default);
            await store.AppendEventAsync(Event(TestScope.WorkspaceId), default);
            await store.AppendEventAsync(Event(otherScope.WorkspaceId), default);

            Assert.AreEqual("first", (await store.GetAsync(TestScope.WorkspaceId, "shared-run", default))!.Value.Name);
            Assert.AreEqual("second", (await store.GetAsync(otherScope.WorkspaceId, "shared-run", default))!.Value.Name);
            Assert.HasCount(1, await store.ListEventsAsync(TestScope.WorkspaceId, "shared-run", 0, default));
            Assert.HasCount(1, await store.ListEventsAsync(otherScope.WorkspaceId, "shared-run", 0, default));

            RuntimeRun Run(RuntimeRunScope scope, string name) => new()
            {
                WorkspaceId = scope.WorkspaceId,
                Scope = scope,
                Id = "shared-run",
                Name = name,
                Properties = new RuntimeRunProperties
                {
                    Agent = new RuntimeAgentReference("sql-expert", 1),
                    Input = Input(name),
                    Execution = new RuntimeExecutionOptions()
                },
                Status = new RuntimeRunStatus { State = RuntimeRunState.Pending, CreatedAt = timestamp }
            };
            RuntimeRunEvent Event(Agentstration.Resources.WorkspaceId workspaceId) => new()
            {
                WorkspaceId = workspaceId,
                EventId = Guid.NewGuid(),
                RunId = "shared-run",
                Kind = RuntimeRunEventKind.RunCreated,
                Timestamp = timestamp,
                State = RuntimeRunState.Pending
            };
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
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
        const string agentPath = "/api/agents/sql-expert";
        var agent = await client.GetFromJsonAsync<AgentResource>(agentPath);
        Assert.IsNotNull(agent);
        var workBefore = await client.GetFromJsonAsync<WorkItemPageResponse>("/api/work/workitems?top=100");
        var request = new CreateRuntimeRunRequest
        {
            Agent = new RuntimeAgentReference(agent.Metadata.Name, agent.Generation),
            Input = Input("integration prompt"),
            Origin = RuntimeRunOrigin.Api
        };

        var readiness = await client.GetFromJsonAsync<AgentRuntimeReadinessResponse>($"/api/runtime/agents/sql-expert/readiness?generation={agent.Generation}");

        using var createdResponse = await client.PostAsJsonAsync("/api/runtime/runs", request);
        Assert.AreEqual(HttpStatusCode.Accepted, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<RuntimeRun>();
        Assert.IsNotNull(created);
        var current = factory.Services.GetRequiredService<ICurrentRequestContext>().Current;
        Assert.AreEqual(new RuntimeRunScope(current.TenantId, new(current.WorkspaceId), current.PrincipalId), created.Properties.Scope);
        Assert.AreEqual(current.PrincipalId.ToString("D"), created.Properties.Initiator);
        Assert.IsNull(typeof(CreateRuntimeRunRequest).GetProperty("Initiator"));

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
        Assert.IsTrue(readiness?.Ready);
        StringAssert.Contains(eventStream, "event: ResponseDelta");
        StringAssert.Contains(eventStream, "event: RunCompleted");
        Assert.AreEqual(workBefore!.Value.Count, workAfter!.Value.Count);
    }

    [TestMethod]
    public void HttpPayloadCaptureIsRejectedOutsideDevelopment()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Observability:GenAI:HttpPayloadCapture:Enabled", "true");
        });

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateClient());

        StringAssert.Contains(exception.Message, "Development environment");
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
        public AgentExecutionRequest? LastRequest { get; private set; }
        public TaskCompletionSource ExecutionStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Set(string deploymentId, IAgentRuntime runtime) { }
        public bool TryGet(string deploymentId, out IAgentRuntime? runtime) { runtime = null; return true; }
        public bool Remove(string deploymentId) => false;
        public async Task<AgentExecutionResult> ExecuteAsync(string deploymentId, AgentExecutionRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            ExecutionStarted.TrySetResult();
            if (Behavior == RuntimeBehavior.Fail) throw new InvalidOperationException("runtime failed");
            if (Behavior == RuntimeBehavior.Block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new AgentExecutionResult("runtime response", request.SessionId, "ollama", "qwen3:1.7b", request.Options);
        }
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string directory;
        private readonly ServiceProvider provider;
        public RuntimeRunService Service { get; }
        public IRuntimeRunStore Store { get; }
        public FakeRuntimeRegistry Registry { get; }
        public TestRuntimeRunQueue Queue { get; }
        public string AgentId { get; }

        private RuntimeFixture(string directory, ServiceProvider provider, RuntimeRunService service, IRuntimeRunStore store, FakeRuntimeRegistry registry, TestRuntimeRunQueue queue, string agentId)
        {
            this.directory = directory;
            this.provider = provider;
            Service = service;
            Store = store;
            Registry = registry;
            Queue = queue;
            AgentId = agentId;
        }

        public static async Task<RuntimeFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"agentstration-runtime-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<ICurrentRequestContext, SystemOperationRequestContext>();
            services.AddSqliteControlPlane($"Data Source={Path.Combine(directory, "management.db")}");
            services.AddSingleton<IRuntimeAgentResolver, ControlPlaneRuntimeAgentResolver>();
            services.AddSqliteRuntimeRuns($"Data Source={Path.Combine(directory, "runtime.db")}");
            var queue = new TestRuntimeRunQueue();
            services.AddSingleton(queue);
            services.AddSingleton<IRuntimeRunQueue>(queue);
            services.AddSingleton<IRuntimeRunCancellationRegistry, LocalRuntimeRunCancellationRegistry>();
            services.AddSingleton<IRuntimeRunExecutionScope, TestRuntimeRunExecutionScope>();
            var registry = new FakeRuntimeRegistry();
            services.AddSingleton(registry);
            services.AddSingleton<IRuntimeRegistry>(registry);
            services.AddSingleton<RuntimeRunStateManager>();
            services.AddSingleton<RuntimeRunService>();
            var provider = services.BuildServiceProvider();
            var management = provider.GetRequiredService<IControlPlaneStore>();
            var store = provider.GetRequiredService<IRuntimeRunStore>();
            await management.InitializeAsync(default);
            await store.InitializeAsync(default);

            const string agentId = "sql-expert";
            const string revisionId = "sql-expert--000001";
            var agent = await management.PutAsync(Agent(agentId), null, true, default);
            await management.CreateImmutableAsync(Revision(revisionId, agentId, agent.Value.Uid), default);
            await management.PutAsync(Deployment(revisionId), null, true, default);
            return new RuntimeFixture(directory, provider, provider.GetRequiredService<RuntimeRunService>(), store, registry, queue, agentId);
        }

        public Task<StoredRuntimeRun> CreateRunAsync() => Service.CreateAsync(TestScope, new RuntimeAgentReference(AgentId, 3), Input("test prompt"), new RuntimeExecutionOptions(), RuntimeRunOrigin.Api, "test", default);

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        private static AgentResource Agent(string id) => new()
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Agent,
            Metadata = new ResourceMetadata { Name = "sql-expert" },
            Generation = 3,
            Definition = new AgentProperties
            {
                DisplayName = "SQL Expert",
                Instructions = "Test",
                ModelProfile = new ResourceReference("reasoning-default")
            }
        };

        private static AgentRevision Revision(string id, string agentId, Guid agentUid) => new()
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.AgentRevision,
            Metadata = new ResourceMetadata { Name = "sql-expert--000001" },
            AgentUid = agentUid,
            AgentName = agentId,
            AgentVersion = 3,
            DefinitionHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow,
            ProvisioningState = ProvisioningState.Succeeded,
            Definition = new ResolvedAgentDefinition
            {
                AgentId = agentUid,
                AgentKey = "sql-expert",
                DisplayName = "SQL Expert",
                Description = "Test",
                AgentVersion = 3,
                EffectiveInstructions = "Test",
                ModelProfileName = "reasoning-default",
                RuntimeProfileName = "maf-default",
                EffectiveToolNames = [],
                MiddlewareIds = [],
                Capabilities = [],
                Handler = "prompt-agent",
                DefinitionHash = "hash"
            }
        };

        private static AgentDeployment Deployment(string revisionId) => new()
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.AgentDeployment,
            Metadata = new ResourceMetadata { Name = "sql-expert" },
            RevisionName = revisionId,
            AgentName = "sql-expert",
            Environment = "local",
            RuntimeProfileName = "maf-default",
            HostingMode = AgentHostingMode.InProcess,
            DesiredState = DesiredAgentState.Running,
            ProvisioningState = ProvisioningState.Succeeded,
            OperationalState = OperationalState.Ready,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class TestRuntimeRunExecutionScope : IRuntimeRunExecutionScope
    {
        public ValueTask ValidateAsync(RuntimeRunScope scope, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public IDisposable Enter(RuntimeRunScope scope) => new EmptyScope();

        private sealed class EmptyScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class TestRuntimeRunQueue : IRuntimeRunQueue
    {
        public List<RuntimeRunQueueItem> Enqueued { get; } = [];
        public ValueTask EnqueueAsync(RuntimeRunQueueItem run, CancellationToken cancellationToken)
        {
            Enqueued.Add(run);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<RuntimeRunQueueItem> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ConcurrentTerminalRunStore(RuntimeRun run) : IRuntimeRunStore
    {
        public StoredRuntimeRun Current { get; private set; } = new(run, "etag-1", DateTimeOffset.UtcNow);
        public int UpdateAttempts { get; private set; }
        public List<RuntimeRunEvent> Events { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredRuntimeRun> CreateAsync(RuntimeRun value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredRuntimeRun?> GetAsync(Agentstration.Resources.WorkspaceId workspaceId, string runId, CancellationToken cancellationToken) => Task.FromResult<StoredRuntimeRun?>(Current);
        public Task<IReadOnlyList<StoredRuntimeRun>> ListAsync(Agentstration.Resources.WorkspaceId workspaceId, string? agentResourceId, int skip, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RuntimeRunKey>> ListRecoverableAsync(int skip, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredRuntimeRun> UpdateAsync(RuntimeRun value, string expectedETag, CancellationToken cancellationToken)
        {
            UpdateAttempts++;
            Current = new StoredRuntimeRun(
                Current.Value with { Status = Current.Value.Status with { State = RuntimeRunState.Cancelled } },
                "etag-2",
                DateTimeOffset.UtcNow);
            throw new RuntimeRunConcurrencyException("Concurrent terminal transition won.");
        }

        public Task<RuntimeRunEvent> AppendEventAsync(RuntimeRunEvent runEvent, CancellationToken cancellationToken)
        {
            Events.Add(runEvent);
            return Task.FromResult(runEvent);
        }

        public Task<IReadOnlyList<RuntimeRunEvent>> ListEventsAsync(Agentstration.Resources.WorkspaceId workspaceId, string runId, long afterSequence, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RuntimeRunEvent>>(Events);
    }

    private static RuntimeRunScope TestScope { get; } = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        new(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        Guid.Parse("33333333-3333-3333-3333-333333333333"));
}
