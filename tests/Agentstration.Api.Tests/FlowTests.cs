using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Flows.Storage.Sqlite;
using Agentstration.Infrastructure.Flows;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed partial class FlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static void AssertValidationCode(string code, FlowDefinition definition)
    {
        var exception = Assert.ThrowsExactly<FlowValidationException>(() => FlowValidator.Validate(Definition($"invalid-{code}", definition)));
        Assert.AreEqual(code, exception.Code);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static FlowResource Definition(string name, FlowDefinition definition) => new(ResourceScopeRef.Workspace(TestScope.WorkspaceId.Value), new FlowId(name), name, null, "1.0.0", true, null, definition, new Dictionary<string, string>(), Now, Now);

    private sealed class FlowFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly ServiceProvider _provider;
        public FlowService Service => _provider.GetRequiredService<FlowService>();
        public IFlowRepository Repository => _provider.GetRequiredService<IFlowRepository>();
        private FlowFixture(string directory, ServiceProvider provider) { _directory = directory; _provider = provider; }
        public static async Task<FlowFixture> CreateAsync(DbCommandInterceptor? commandInterceptor = null, TimeProvider? timeProvider = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"agentstration-flow-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var services = new ServiceCollection();
            services.AddSingleton(timeProvider ?? TimeProvider.System);
            var connectionString = $"Data Source={Path.Combine(directory, "flow.db")};Pooling=False";
            if (commandInterceptor is null)
            {
                services.AddSqliteFlowStorage(connectionString);
            }
            else
            {
                services.AddDbContextFactory<FlowDbContext>(options => options.UseSqlite(connectionString).AddInterceptors(commandInterceptor));
                services.AddSingleton<IFlowRepository, SqliteFlowRepository>();
            }
            services.AddSingleton<FlowService>();
            var provider = services.BuildServiceProvider();
            var fixture = new FlowFixture(directory, provider);
            await fixture.Service.InitializeAsync(default);
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class TestFlowRunQueue : IFlowRunQueue
    {
        public List<FlowRunQueueItem> Enqueued { get; } = [];
        public ValueTask EnqueueAsync(FlowRunQueueItem item, CancellationToken cancellationToken) { Enqueued.Add(item); return ValueTask.CompletedTask; }
    }

    private sealed class DeniedFlowRunExecutionScope : IFlowRunExecutionScope
    {
        public ValueTask ValidateAsync(FlowRunScope scope, CancellationToken cancellationToken) =>
            ValueTask.FromException(new FlowValidationException("flow_run_authorization_denied", "Execution permission was revoked."));
        public IDisposable Enter(FlowRunScope scope) => throw new AssertFailedException("A denied scope must not be entered.");
    }

    private static FlowRunScope TestScope { get; } = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), new(Guid.Parse("22222222-2222-2222-2222-222222222222")), Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private sealed class TestCancellationRegistry : IFlowRunCancellationRegistry
    {
        public CancellationToken Register(FlowRunKey run, CancellationToken stoppingToken) => stoppingToken;
        public bool Cancel(FlowRunKey run) => true;
        public void Complete(FlowRunKey run) { }
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset current = initial;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }

    private sealed class TestRuntimeExecutionStateStore : IRuntimeExecutionStateStore
    {
        private readonly Dictionary<(WorkspaceId WorkspaceId, string RunId, string RuntimeType, string StateId), RuntimeExecutionState> states = [];

        public Task StoreAsync(RuntimeExecutionState state, CancellationToken cancellationToken)
        {
            states[(state.WorkspaceId, state.RunId, state.RuntimeType, state.StateId)] = state;
            return Task.CompletedTask;
        }

        public Task<RuntimeExecutionState?> GetAsync(
            WorkspaceId workspaceId,
            string runId,
            string runtimeType,
            string stateId,
            CancellationToken cancellationToken)
        {
            states.TryGetValue((workspaceId, runId, runtimeType, stateId), out var state);
            return Task.FromResult(state);
        }

        public Task<IReadOnlyList<RuntimeExecutionState>> ListAsync(
            WorkspaceId workspaceId,
            string runId,
            string runtimeType,
            string? parentStateId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RuntimeExecutionState>>(
                states.Values.Where(state => state.WorkspaceId == workspaceId
                    && state.RunId == runId
                    && state.RuntimeType == runtimeType
                    && (parentStateId is null || state.ParentStateId == parentStateId)).ToArray());

        public Task DeleteAsync(WorkspaceId workspaceId, string runId, string? runtimeType, CancellationToken cancellationToken)
        {
            foreach (var key in states.Keys.Where(key => key.WorkspaceId == workspaceId
                         && key.RunId == runId
                         && (runtimeType is null || key.RuntimeType == runtimeType)).ToArray())
                states.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed record InteractionApiCase(
        string Kind,
        InputRequestType Type,
        IReadOnlyList<string> Options,
        JsonElement ValidValue,
        JsonElement InvalidValue);

    private sealed class ExistingResourceResolver : IFlowResourceReferenceResolver
    {
        public Task<bool> ExistsAsync(string resourceId, ResourceNamespace? @namespace, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
