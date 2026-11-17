using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.SystemGraph.Contracts;
using Agentstration.SystemOperations.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SystemFlowBindingApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task SqliteMetadataOnlyOverlayCorrelatesBindingTurnsAndFencingWithoutMaterializationOrForeignRuns()
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IRuntimeExecutionMaterialResolver, ForbiddenMaterialResolver>())));
        var current = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var resources = factory.Services.GetRequiredService<IResourceStore>();
        var flows = factory.Services.GetRequiredService<IFlowRepository>();
        var assignments = factory.Services.GetRequiredService<IRuntimeWorkerAssignmentStore>();
        var owner = ResourceScopeRef.Workspace(current.WorkspaceId);
        var ns = ResourceNamespace.Parse("binding-target");
        var clock = factory.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        Guid agentUid, revisionUid = Guid.NewGuid(), stepId = Guid.NewGuid(), attemptId = Guid.NewGuid(), sessionId = Guid.NewGuid();
        using (scopes.PushSystem())
        {
            var agent = await resources.PutExactAsync(owner, new AgentResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = AgentResourceKinds.Agent,
                ScopeRef = owner,
                Metadata = new() { Name = "same-name", Namespace = ns },
                Generation = 1,
                Definition = new() { DisplayName = "Safe", Instructions = "private-agent-instructions", ModelProfile = new("metadata-only") }
            }, null, true, default);
            agentUid = agent.Value.Uid;
            var binding = new RuntimeExecutionBinding
            {
                ParticipantId = "respond",
                AgentUid = agentUid,
                RevisionUid = revisionUid,
                AgentScopeRef = owner,
                AgentNamespace = ns,
                AgentResourceId = "same-name",
                AgentGeneration = 2,
                RevisionId = "revision-v2",
                DefinitionHash = "agent-hash",
                DeploymentId = "metadata-only",
                ModelProfileName = "metadata-only",
                RuntimeProfileName = "metadata-only"
            };
            var scope = FlowRunScope.Workspace(current.TenantId, new(current.WorkspaceId), current.PrincipalId);
            var flowId = new FlowId("consumer", ResourceNamespace.Parse("consumer"));
            var run = new FlowRun
            {
                Id = "own-consumer",
                Scope = scope,
                WorkspaceId = scope.WorkspaceId,
                FlowId = flowId,
                FlowVersion = "1.0",
                DefinitionHash = "flow-execution-hash",
                CreatedAt = now,
                StartedAt = now,
                Status = FlowRunStatus.Running,
                RuntimeBindings = [binding],
                DefinitionSnapshot = new(owner, flowId, "1.0", null, new DirectFlowDefinition(new(FlowTargetKind.Agent, "same-name",
                    "2", ns, owner, agentUid, revisionUid, "agent-hash")), new Dictionary<string, string>(), now),
                Input = JsonSerializer.SerializeToElement(new { prompt = "private-flow-input" }),
                TokenUsage = new(FlowRunTokenUsageState.Final, new(1000000, 2000000, 3000000, FlowRunTokenUsageCoverage.Complete, 1, 1),
                    new(1000000, 2000000, 3000000, FlowRunTokenUsageCoverage.Complete, 1, 1))
            };
            await flows.CreateRunAsync(run, default);
            await flows.CreateRunAsync(run with { Id = "foreign-principal-consumer", Scope = scope with { PrincipalId = Guid.NewGuid() } }, default);
            var attempt = new RuntimeAssignmentAttempt
            {
                Id = new(attemptId),
                WorkerId = new(Guid.NewGuid()),
                WorkerSessionId = new(sessionId),
                FencingGeneration = 3,
                AcquiredAt = now,
                LeaseExpiresAt = now.AddHours(1),
                LastHeartbeatAt = now,
                State = RuntimeAssignmentAttemptState.Active
            };
            var turn = new RuntimeAssignmentTurn
            {
                CommandId = Guid.NewGuid(),
                Id = Guid.NewGuid(),
                AttemptId = attemptId,
                RunId = run.Id,
                StepExecutionId = stepId,
                ParticipantId = binding.ParticipantId,
                OpenedAt = now
            };
            await assignments.CreateAsync(new()
            {
                Id = new(Guid.NewGuid()),
                WorkspaceId = scope.WorkspaceId,
                ScopeRef = scope.ScopeRef,
                TenantId = scope.TenantId,
                TargetKind = RuntimeAssignmentTargetKind.FlowRun,
                TargetRunId = run.Id,
                State = RuntimeAssignmentState.Assigned,
                RuntimeCapability = "flow",
                RuntimeCapabilityVersion = "1.0",
                ExecutionMaterialVersion = "1.0",
                ExecutionMaterialId = "metadata-only",
                ExecutionMaterialDigest = "metadata-only",
                CreatedAt = now,
                UpdatedAt = now,
                FencingGeneration = 3,
                Attempts = [attempt],
                Turns = [turn, turn with { Id = Guid.NewGuid(), CommandId = Guid.NewGuid() }],
                StepExecutions = [new() { CommandId = Guid.NewGuid(), Id = stepId, FlowRunId = run.Id, FlowVersion = run.FlowVersion,
                    FlowDefinitionHash = run.DefinitionHash, StepDefinitionId = "respond", StepName = "respond", StepType = "agent",
                    DefinitionPosition = 1, OpenedAt = now }]
            }, default);
        }
        using var client = factory.CreateClient(); SelectConsoleContext(client, owner);
        using var response = await client.GetAsync("/api/system-operations?root=" + Uri.EscapeDataString(SystemGraphIds.Definition(agentUid)) + "&namespace=" + ns.Value);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-flow-input", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-agent-instructions", json, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign-principal-consumer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("1000000", json, StringComparison.Ordinal);
        var facts = (await response.Content.ReadFromJsonAsync<SystemOperationalSnapshot>())!.Facts!;
        Assert.IsNull(facts.Metrics); Assert.IsEmpty(facts.Runs);
        Assert.HasCount(1, facts.FlowBindings);
        var evidence = facts.FlowBindings[0];
        Assert.AreEqual("own-consumer", evidence.FlowRunId); Assert.AreEqual(agentUid, evidence.AgentUid); Assert.AreEqual(revisionUid, evidence.RevisionUid);
        Assert.HasCount(1, evidence.Invocations);
        Assert.AreEqual(stepId, evidence.Invocations[0].StepExecutionId);
        Assert.AreEqual(2, evidence.Invocations[0].ObservedTurns);
        Assert.AreEqual(sessionId, evidence.Invocations[0].SessionId); Assert.AreEqual(3L, evidence.Invocations[0].FencingGeneration);
    }
    private sealed class ForbiddenMaterialResolver : IRuntimeExecutionMaterialResolver
    {
        public Task<RuntimeExecutionMaterial> ResolveAsync(RuntimeWorkerAssignment assignment, CancellationToken ct) =>
            throw new AssertFailedException("Operational reads must not materialize execution or content.");
        public Task<RuntimeFlowStepMaterial> ResolveStepAsync(RuntimeWorkerAssignment assignment, string flowRunId, string flowVersion,
            string flowDefinitionHash, string stepDefinitionId, CancellationToken ct) =>
            throw new AssertFailedException("Operational reads must not materialize execution or content.");
    }
}
