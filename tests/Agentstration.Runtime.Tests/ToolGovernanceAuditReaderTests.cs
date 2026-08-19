using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Flow.Storage.Sqlite;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class ToolGovernanceAuditReaderTests
{
    private static readonly WorkspaceId Workspace = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [TestMethod]
    public async Task RuntimeAuditFiltersAndPaginatesDurableGovernanceEvents()
    {
        await WithStoresAsync(async (runtimeRuns, flowRuns) =>
        {
            await runtimeRuns.CreateAsync(RuntimeRun(), default);
            await runtimeRuns.AppendEventAsync(RuntimeEvent(1, "attempt-1", "lookup", Evaluation("managed:guard", "default/ToolExecutionHook/guard", 3, ToolExecutionHookEvaluationKind.Allowed)), default);
            await runtimeRuns.AppendEventAsync(RuntimeEvent(2, "attempt-2", "lookup", Evaluation("managed:guard", "default/ToolExecutionHook/guard", 4, ToolExecutionHookEvaluationKind.Denied, "blocked")), default);
            await runtimeRuns.AppendEventAsync(RuntimeEvent(3, "attempt-3", "other", Evaluation("local:audit", null, null, ToolExecutionHookEvaluationKind.Allowed)), default);
            var reader = new ToolGovernanceAuditReader(runtimeRuns, flowRuns);

            var invalid = await Assert.ThrowsExactlyAsync<ToolGovernanceAuditValidationException>(() => reader.ListAsync(new ToolGovernanceAuditQuery
            {
                OwnerKind = ToolExecutionOwnerKind.RuntimeRun,
                WorkspaceId = Workspace,
                RunId = "runtime-run",
                ResourceGeneration = 0
            }));
            Assert.AreEqual("invalid_resource_generation", invalid.Code);

            var first = await reader.ListAsync(new ToolGovernanceAuditQuery
            {
                OwnerKind = ToolExecutionOwnerKind.RuntimeRun,
                WorkspaceId = Workspace,
                RunId = "runtime-run",
                Limit = 1,
                ToolId = "lookup",
                HookId = "default/ToolExecutionHook/guard"
            });

            Assert.HasCount(1, first.Items);
            Assert.AreEqual("attempt-1", first.Items[0].InvocationId);
            Assert.AreEqual(3L, first.Items[0].Evaluations[0].Hook.ResourceGeneration);
            Assert.AreEqual(1L, first.NextSequence);

            var denied = await reader.ListAsync(new ToolGovernanceAuditQuery
            {
                OwnerKind = ToolExecutionOwnerKind.RuntimeRun,
                WorkspaceId = Workspace,
                RunId = "runtime-run",
                AfterSequence = first.NextSequence!.Value,
                ToolCallId = "logical-call",
                InvocationId = "attempt-2",
                ResourceGeneration = 4,
                Decision = ToolExecutionHookEvaluationKind.Denied
            });

            Assert.HasCount(1, denied.Items);
            Assert.AreEqual("attempt-2", denied.Items[0].InvocationId);
            Assert.AreEqual("blocked", denied.Items[0].Evaluations[0].Code);
            Assert.IsNull(denied.NextSequence);
        });
    }

    [TestMethod]
    public async Task FlowAuditUsesTheSameContractAndOmitsArgumentsAndResults()
    {
        await WithStoresAsync(async (runtimeRuns, flowRuns) =>
        {
            await flowRuns.CreateRunAsync(FlowRun(), default);
            var governance = new[]
            {
                Evaluation("managed:guard", "default/ToolExecutionHook/guard", 7, ToolExecutionHookEvaluationKind.Denied, "blocked")
            };
            await flowRuns.AppendRunEventAsync(new FlowRunEvent(
                Workspace,
                "flow-run",
                0,
                FlowRunEventType.ToolCallGovernanceEvaluated,
                null,
                JsonSerializer.SerializeToElement(new
                {
                    ToolCallId = "logical-call",
                    InvocationId = "attempt-1",
                    ToolId = "lookup",
                    ToolName = "lookup",
                    ProviderId = "provider",
                    AgentId = "agent",
                    CorrelationId = "correlation",
                    Governance = governance,
                    Arguments = new { secret = "must-not-project" },
                    Result = new { secret = "must-not-project" }
                }),
                DateTimeOffset.UnixEpoch), default);
            var reader = new ToolGovernanceAuditReader(runtimeRuns, flowRuns);

            var page = await reader.ListAsync(new ToolGovernanceAuditQuery
            {
                OwnerKind = ToolExecutionOwnerKind.FlowRun,
                WorkspaceId = Workspace,
                RunId = "flow-run",
                HookId = "managed:guard",
                Decision = ToolExecutionHookEvaluationKind.Denied
            });

            var record = page.Items[0];
            Assert.AreEqual(ToolExecutionOwnerKind.FlowRun, record.OwnerKind);
            Assert.AreEqual("attempt-1", record.InvocationId);
            Assert.AreEqual(7L, record.Evaluations[0].Hook.ResourceGeneration);
            var serialized = JsonSerializer.Serialize(record);
            Assert.DoesNotContain("Arguments", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("Result", serialized, StringComparison.Ordinal);
            await Assert.ThrowsExactlyAsync<ToolGovernanceAuditRunNotFoundException>(() => reader.ListAsync(new ToolGovernanceAuditQuery
            {
                OwnerKind = ToolExecutionOwnerKind.FlowRun,
                WorkspaceId = new WorkspaceId(Guid.NewGuid()),
                RunId = "flow-run"
            }));
        });
    }

    [TestMethod]
    public async Task AuditExposesArgumentsOnlyWhenTheDurableFactCapturedThem()
    {
        await WithStoresAsync(async (runtimeRuns, flowRuns) =>
        {
            await runtimeRuns.CreateAsync(RuntimeRun(), default);
            var runEvent = RuntimeEvent(
                1,
                "attempt-1",
                "lookup",
                Evaluation("managed:guard", null, null, ToolExecutionHookEvaluationKind.Allowed)) with
            {
                ToolCall = RuntimeEvent(
                    1,
                    "attempt-1",
                    "lookup",
                    Evaluation("managed:guard", null, null, ToolExecutionHookEvaluationKind.Allowed)).ToolCall! with
                {
                    Arguments = "{\"query\":\"dotnet\"}"
                }
            };
            await runtimeRuns.AppendEventAsync(runEvent, default);
            var reader = new ToolGovernanceAuditReader(runtimeRuns, flowRuns);

            var page = await reader.ListAsync(new ToolGovernanceAuditQuery
            {
                OwnerKind = ToolExecutionOwnerKind.RuntimeRun,
                WorkspaceId = Workspace,
                RunId = "runtime-run"
            });

            Assert.AreEqual("{\"query\":\"dotnet\"}", page.Items[0].Arguments);
        });
    }

    private static async Task WithStoresAsync(Func<IRuntimeRunStore, IFlowRepository, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-governance-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSqliteRuntimeRuns($"Data Source={Path.Combine(directory, "runtime.db")}");
            services.AddSqliteFlowStorage($"Data Source={Path.Combine(directory, "flow.db")}");
            await using var provider = services.BuildServiceProvider();
            var runtimeRuns = provider.GetRequiredService<IRuntimeRunStore>();
            var flowRuns = provider.GetRequiredService<IFlowRepository>();
            await runtimeRuns.InitializeAsync(default);
            await flowRuns.InitializeAsync(default);
            await test(runtimeRuns, flowRuns);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static RuntimeRunEvent RuntimeEvent(
        long sequence,
        string invocationId,
        string toolId,
        ToolExecutionHookEvaluation evaluation) => new()
        {
            WorkspaceId = Workspace,
            EventId = Guid.NewGuid(),
            RunId = "runtime-run",
            Kind = RuntimeRunEventKind.ToolCallGovernanceEvaluated,
            Sequence = sequence,
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            ToolCall = new RuntimeToolCall
            {
                Id = "logical-call",
                InvocationId = invocationId,
                ToolId = toolId,
                Name = toolId,
                State = RuntimeRunState.Running,
                Attempt = (int)sequence,
                StartedAt = DateTimeOffset.UnixEpoch,
                ProviderId = "provider",
                Governance = [evaluation]
            }
        };

    private static ToolExecutionHookEvaluation Evaluation(
        string id,
        string? resourceId,
        long? generation,
        ToolExecutionHookEvaluationKind decision,
        string? code = null) => new(
            new ToolExecutionHookIdentity(
                id,
                10,
                resourceId is null ? ToolExecutionHookSource.Local : ToolExecutionHookSource.Managed,
                resourceId,
                generation),
            decision,
            code);

    private static RuntimeRun RuntimeRun() => new()
    {
        WorkspaceId = Workspace,
        Scope = new RuntimeRunScope(Guid.NewGuid(), Workspace, Guid.NewGuid()),
        Id = "runtime-run",
        Name = "runtime-run",
        Properties = new RuntimeRunProperties
        {
            Agent = new RuntimeAgentReference("agent", 1),
            Input = new RuntimeRunInput { Messages = [new RuntimeRunMessage(RuntimeMessageRole.User, "test")] },
            Execution = new RuntimeExecutionOptions()
        },
        Status = new RuntimeRunStatus { State = RuntimeRunState.Running, CreatedAt = DateTimeOffset.UnixEpoch }
    };

    private static FlowRun FlowRun()
    {
        var version = new FlowVersion(
            Workspace,
            new FlowId("flow"),
            "1.0.0",
            null,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent")),
            new Dictionary<string, string>(),
            DateTimeOffset.UnixEpoch);
        return new FlowRun
        {
            WorkspaceId = Workspace,
            Id = "flow-run",
            FlowId = version.FlowId,
            FlowVersion = version.Version,
            Trigger = FlowRunTrigger.Manual,
            Scope = new FlowRunScope(Guid.NewGuid(), Workspace, Guid.NewGuid()),
            Input = JsonSerializer.SerializeToElement(new { prompt = "test" }),
            CreatedAt = DateTimeOffset.UnixEpoch,
            DefinitionSnapshot = version
        };
    }
}
