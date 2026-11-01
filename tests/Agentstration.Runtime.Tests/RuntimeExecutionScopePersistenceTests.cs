using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class RuntimeExecutionScopePersistenceTests
{
    [TestMethod]
    public async Task RunsEventsAndTechnicalStateAreIsolatedByExactScope()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"agentstration-runtime-scope-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSqliteRuntimeRuns($"Data Source={databasePath};Pooling=False");
        await using var provider = services.BuildServiceProvider();
        try
        {
            var runs = provider.GetRequiredService<IRuntimeRunStore>();
            var states = provider.GetRequiredService<IRuntimeExecutionStateStore>();
            var assignments = provider.GetRequiredService<IRuntimeWorkerAssignmentStore>();
            await runs.InitializeAsync(default);
            await assignments.InitializeAsync(default);
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var workspaceId = new WorkspaceId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
            var principalId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
            var scopes = new[]
            {
                RuntimeRunScope.Instance(principalId),
                RuntimeRunScope.Tenant(tenantId, principalId),
                RuntimeRunScope.Workspace(tenantId, workspaceId, principalId)
            };

            foreach (var scope in scopes)
            {
                var run = Run(scope, "run-shared");
                await runs.CreateAsync(run, default);
                await runs.AppendEventAsync(new RuntimeRunEvent
                {
                    ScopeRef = scope.ScopeRef,
                    EventId = Guid.NewGuid(),
                    RunId = run.Id,
                    Kind = RuntimeRunEventKind.RunCreated,
                    Timestamp = run.Status.CreatedAt
                }, default);
                await states.StoreAsync(new RuntimeExecutionState(
                    scope.ScopeRef,
                    run.Id,
                    "test",
                    "state-shared",
                    JsonSerializer.SerializeToElement(new { scope = scope.ScopeRef.Value }),
                    run.Status.CreatedAt), default);
                await assignments.CreateAsync(new RuntimeWorkerAssignment
                {
                    WorkspaceId = scope.WorkspaceId,
                    ScopeRef = scope.ScopeRef,
                    TenantId = scope.TenantId,
                    Id = new RuntimeAssignmentId(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")),
                    TargetRunId = run.Id,
                    RuntimeCapability = "test-runtime",
                    RuntimeCapabilityVersion = "1.0",
                    ExecutionMaterialVersion = "1.0",
                    ExecutionMaterialId = $"material:{scope.ScopeRef.Value}",
                    ExecutionMaterialDigest = $"digest:{scope.ScopeRef.Value}",
                    CreatedAt = run.Status.CreatedAt,
                    UpdatedAt = run.Status.CreatedAt
                }, default);
            }

            foreach (var scope in scopes)
            {
                var stored = await runs.GetExactAsync(scope.ScopeRef, "run-shared", default);
                var events = await runs.ListEventsExactAsync(scope.ScopeRef, "run-shared", 0, default);
                var state = await states.GetExactAsync(scope.ScopeRef, "run-shared", "test", "state-shared", default);
                var assignment = await assignments.GetExactAsync(
                    scope.ScopeRef,
                    new RuntimeAssignmentId(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")),
                    default);

                Assert.IsNotNull(stored);
                Assert.AreEqual(scope.ScopeRef, stored.Value.ExecutionScopeRef);
                Assert.HasCount(1, events);
                Assert.AreEqual(scope.ScopeRef, events[0].ScopeRef);
                Assert.IsNotNull(state);
                Assert.AreEqual(scope.ScopeRef, state.ScopeRef);
                Assert.IsNotNull(assignment);
                Assert.AreEqual(scope.ScopeRef, assignment.Value.ExecutionScopeRef);
            }
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static RuntimeRun Run(RuntimeRunScope scope, string id) => new()
    {
        WorkspaceId = scope.WorkspaceId,
        Scope = scope,
        Id = id,
        Name = id,
        Properties = new RuntimeRunProperties
        {
            Agent = new RuntimeAgentReference("assistant", 1),
            Input = new RuntimeRunInput { Messages = [new(RuntimeMessageRole.User, "test")] },
            Execution = new RuntimeExecutionOptions(),
            Scope = scope
        },
        Status = new RuntimeRunStatus
        {
            State = RuntimeRunState.Pending,
            CreatedAt = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero)
        }
    };
}
