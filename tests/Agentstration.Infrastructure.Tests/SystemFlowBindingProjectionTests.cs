using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Infrastructure.SystemOperations;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class SystemFlowBindingProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    [TestMethod]
    public void LedgerProofSeparatesAttemptsSessionsAndStepsAndCountsDistinctTurnsWithoutPayloads()
    {
        var (run, assignment) = Fixture();
        var turns = assignment.Turns;
        var second = assignment.Attempts[0] with { Id = new(Guid.NewGuid()), WorkerSessionId = new(Guid.NewGuid()), FencingGeneration = 2 };
        assignment = assignment with
        {
            Attempts = [assignment.Attempts[0], second],
            Turns = [turns[0], turns[0],
            turns[0] with { Id = Guid.NewGuid(), AttemptId = second.Id.Value, OpenedAt = Now.AddSeconds(1) },
            turns[0] with { Id = Guid.NewGuid(), RunId = "foreign" },
            turns[0] with { Id = Guid.NewGuid(), ParticipantId = "other-agent" }],
            ExecutionEvents = [new() { EventId = Guid.NewGuid(), AttemptEventSequence = 1, Kind = "private", RunId = run.Id,
                OccurredAt = Now, Payload = JsonSerializer.SerializeToElement(new { prompt = "private-input-not-for-overlay" }) }]
        };
        var result = SystemFlowBindingReader.ProjectInvocations(run, "participant", assignment);
        Assert.HasCount(2, result);
        CollectionAssert.AreEquivalent(new[] { 1L, 2L }, result.Select(item => item.FencingGeneration).ToArray());
        CollectionAssert.AreEquivalent(new[] { assignment.Attempts[0].WorkerSessionId.Value, second.WorkerSessionId.Value }, result.Select(item => item.SessionId).ToArray());
        Assert.IsTrue(result.All(item => item.ObservedTurns == 1 && item.StepExecutionId == assignment.StepExecutions[0].Id));
        Assert.DoesNotContain("private-input-not-for-overlay", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }
    [TestMethod]
    public void WrongScopeRunVersionHashOrAmbiguousBindingCannotAttestInvocation()
    {
        var (run, assignment) = Fixture();
        Assert.IsEmpty(SystemFlowBindingReader.ProjectInvocations(run, "participant", assignment with { ScopeRef = ResourceScopeRef.Instance }));
        Assert.IsEmpty(SystemFlowBindingReader.ProjectInvocations(run, "participant", assignment with { TargetRunId = "foreign" }));
        Assert.IsEmpty(SystemFlowBindingReader.ProjectInvocations(run with { FlowVersion = "2.0" }, "participant", assignment));
        Assert.IsEmpty(SystemFlowBindingReader.ProjectInvocations(run with { DefinitionHash = "other" }, "participant", assignment));
        Assert.IsEmpty(SystemFlowBindingReader.ProjectInvocations(run with { DefinitionHash = null }, "participant", assignment));
        var binding = run.RuntimeBindings[0];
        Assert.IsEmpty(SystemFlowBindingReader.ProjectInvocations(run with { RuntimeBindings = [binding, binding with { RevisionUid = Guid.NewGuid() }] }, "participant", assignment));
        Assert.IsEmpty(SystemFlowBindingReader.ProjectInvocations(run, "unrecorded", assignment));
    }
    [TestMethod]
    public void ChildFlowEvidenceRetainsExactRunCoordinateAndInvocationGroupsAreBounded()
    {
        var (run, assignment) = Fixture();
        var child = run with { Id = "child", RootFlowRunId = run.Id };
        assignment = assignment with
        {
            StepExecutions = [assignment.StepExecutions[0] with { FlowRunId = child.Id }],
            Turns = Enumerable.Range(0, 20).Select(index => assignment.Turns[0] with
            {
                Id = Guid.NewGuid(),
                RunId = child.Id,
                StepExecutionId = null,
                OpenedAt = Now.AddSeconds(index)
            }).ToArray()
        };
        var direct = SystemFlowBindingReader.ProjectInvocations(child, "participant", assignment);
        Assert.HasCount(1, direct); Assert.AreEqual(20, direct[0].ObservedTurns);
        assignment = assignment with
        {
            Turns = Enumerable.Range(0, 20).Select(index => assignment.Turns[0] with
            { Id = Guid.NewGuid(), StepExecutionId = assignment.StepExecutions[0].Id, RunId = child.Id, OpenedAt = Now.AddSeconds(index) }).ToArray()
        };
        Assert.HasCount(1, SystemFlowBindingReader.ProjectInvocations(child, "participant", assignment));
        Assert.IsEmpty(SystemFlowBindingReader.ProjectInvocations(run, "participant", assignment));
        var attempts = Enumerable.Range(1, 20).Select(index => assignment.Attempts[0] with
        { Id = new(Guid.NewGuid()), FencingGeneration = index, WorkerSessionId = new(Guid.NewGuid()) }).ToArray();
        assignment = assignment with
        {
            Attempts = attempts,
            Turns = attempts.Select((attempt, index) => assignment.Turns[0] with
            { Id = Guid.NewGuid(), AttemptId = attempt.Id.Value, OpenedAt = Now.AddSeconds(index) }).ToArray()
        };
        var bounded = SystemFlowBindingReader.ProjectInvocations(child, "participant", assignment);
        Assert.HasCount(8, bounded);
        CollectionAssert.AreEqual(Enumerable.Range(13, 8).Reverse().Select(value => (long)value).ToArray(), bounded.Select(item => item.FencingGeneration).ToArray());
        Assert.HasCount(9, SystemFlowBindingReader.ProjectInvocations(child, "participant", assignment, 9));
    }
    private static (FlowRun, RuntimeWorkerAssignment) Fixture()
    {
        var scope = FlowRunScope.Workspace(Guid.NewGuid(), new(Guid.NewGuid()), Guid.NewGuid());
        var binding = new RuntimeExecutionBinding
        {
            ParticipantId = "participant",
            AgentNamespace = ResourceNamespace.Default,
            AgentScopeRef = ResourceScopeRef.Instance,
            AgentUid = Guid.NewGuid(),
            RevisionUid = Guid.NewGuid(),
            AgentGeneration = 1,
            AgentResourceId = "agent",
            DeploymentId = "deployment",
            RevisionId = "revision",
            RuntimeProfileName = "metadata",
            ModelProfileName = "metadata",
            DefinitionHash = "agent-hash"
        };
        var run = new FlowRun
        {
            Id = "flow",
            FlowId = new("flow"),
            FlowVersion = "1.0",
            Scope = scope,
            WorkspaceId = scope.WorkspaceId,
            DefinitionHash = "flow-hash",
            DefinitionSnapshot = new(scope.ScopeRef, new("flow"), "1.0", null,
                new DirectFlowDefinition(new(FlowTargetKind.Agent, "agent")), new Dictionary<string, string>(), Now),
            Input = JsonSerializer.SerializeToElement(new { }),
            CreatedAt = Now,
            RuntimeBindings = [binding]
        };
        var attempt = new RuntimeAssignmentAttempt
        {
            Id = new(Guid.NewGuid()),
            WorkerId = new(Guid.NewGuid()),
            WorkerSessionId = new(Guid.NewGuid()),
            FencingGeneration = 1,
            AcquiredAt = Now,
            LeaseExpiresAt = Now.AddMinutes(1),
            LastHeartbeatAt = Now,
            State = RuntimeAssignmentAttemptState.Active
        };
        var step = new RuntimeAssignmentStepExecution
        {
            CommandId = Guid.NewGuid(),
            Id = Guid.NewGuid(),
            FlowRunId = run.Id,
            FlowVersion = run.FlowVersion,
            FlowDefinitionHash = "flow-hash",
            StepDefinitionId = "respond",
            StepName = "respond",
            StepType = "agent",
            DefinitionPosition = 1,
            OpenedAt = Now
        };
        var assignment = new RuntimeWorkerAssignment
        {
            WorkspaceId = scope.WorkspaceId,
            ScopeRef = scope.ScopeRef,
            TenantId = scope.TenantId,
            Id = new(Guid.NewGuid()),
            TargetKind = RuntimeAssignmentTargetKind.FlowRun,
            TargetRunId = run.Id,
            RuntimeCapability = "flow",
            RuntimeCapabilityVersion = "1.0",
            ExecutionMaterialVersion = "1.0",
            ExecutionMaterialId = "metadata",
            ExecutionMaterialDigest = "metadata",
            CreatedAt = Now,
            UpdatedAt = Now,
            Attempts = [attempt],
            StepExecutions = [step],
            Turns = [new() { CommandId = Guid.NewGuid(), Id = Guid.NewGuid(),
                AttemptId = attempt.Id.Value, RunId = run.Id, ParticipantId = "participant", StepExecutionId = step.Id, OpenedAt = Now }]
        };
        return (run, assignment);
    }
}
