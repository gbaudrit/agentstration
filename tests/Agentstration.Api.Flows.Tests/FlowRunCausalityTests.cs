using System.Globalization;
using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task CausalityProjectsOriginDescendantsToolAttemptsAndWorkspaceIsolationWithoutSensitivePayloads()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "child", ChildGraph());
        var graph = ParentGraph();
        graph = graph with
        {
            Steps = graph.Steps.Select(step => step is FlowCallStepDefinition call
                ? call with { Flow = new("child", FlowCallVersionStrategy.Active) }
                : step).ToArray()
        };
        var parent = await CreatePublishedGraphAsync(fixture, "parent", graph);
        var runs = Service(fixture, new TestFlowRunQueue());
        using var input = JsonDocument.Parse("""{"article":"new item"}""");
        var root = await runs.EnsureRootAsync(new EnsureRootFlowRunCommand(
            "root-causal",
            parent.Value.Id,
            null,
            "local",
            FlowRunTrigger.Api,
            FlowInvocationOrigin.Mcp,
            "news-agent",
            "news-42",
            "delivery-42",
            "correlation-42",
            input.RootElement,
            Guid.NewGuid().ToString("D"),
            true,
            null,
            null,
            null,
            null,
            TestScope), default);
        var createdChild = await runs.CreateOrGetAssignedChildAsync(TestScope.WorkspaceId, root.Value.Id,
            "analyze", input.RootElement, "flowCall", null, default);
        var childId = createdChild.Value.Id;
        var now = DateTimeOffset.Parse("2026-09-10T07:59:00Z", CultureInfo.InvariantCulture);
        var stepExecutionId = Guid.NewGuid();
        var assignment = new RuntimeWorkerAssignment
        {
            WorkspaceId = TestScope.WorkspaceId,
            TenantId = TestScope.TenantId,
            Id = new(Guid.NewGuid()),
            TargetKind = RuntimeAssignmentTargetKind.FlowRun,
            TargetRunId = root.Value.Id,
            RuntimeCapability = AwpRuntimeKinds.MicrosoftAgentFramework,
            RuntimeCapabilityVersion = "1.0",
            ExecutionMaterialVersion = "1.0",
            ExecutionMaterialId = "material-causality",
            ExecutionMaterialDigest = "sha256:causality",
            State = RuntimeAssignmentState.Assigned,
            CreatedAt = now,
            UpdatedAt = now,
            StepExecutions =
            [
                new RuntimeAssignmentStepExecution
                {
                    CommandId = Guid.NewGuid(),
                    Id = stepExecutionId,
                    FlowRunId = root.Value.Id,
                    FlowVersion = root.Value.FlowVersion,
                    FlowDefinitionHash = root.Value.DefinitionHash!,
                    StepDefinitionId = "analyze",
                    StepName = "analyze",
                    StepType = "flowCall",
                    DefinitionPosition = 1,
                    OpenedAt = now
                }
            ]
        };
        var projection = new AwpAssignmentProjection(null!, fixture.Repository,
            new NullFlowRunEventSink(), TimeProvider.System, Options.Create(new FlowRunExecutionOptions()));
        await projection.ProjectEventsAsync(assignment,
        [
            new RuntimeAssignmentExecutionEvent
            {
                EventId = Guid.NewGuid(),
                AttemptEventSequence = 1,
                OccurredAt = now,
                Kind = "StepCompleted",
                RunId = root.Value.Id,
                StepExecutionId = stepExecutionId,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    status = "succeeded",
                    output = new { summary = "new item" },
                    selectedTransition = "analyze-output",
                    childFlowRunIds = new[] { childId }
                })
            }
        ], default);
        var startedAt = DateTimeOffset.Parse("2026-09-10T08:00:00Z", CultureInfo.InvariantCulture);
        var invocationId = $"flow:{childId}:step:notify:attempt:2";
        var toolCallId = $"flow:{childId}:step:notify";
        var common = new
        {
            ToolCallId = toolCallId,
            InvocationId = invocationId,
            ToolId = "notification.send",
            ToolNamespace = "agentstration",
            ToolName = "notification.send",
            ProviderId = "agentstration.internal",
            ProviderNamespace = "agentstration",
            ExternalToolId = "work.notification.create",
            FlowStepId = "notify",
            CorrelationId = "correlation-42",
            Arguments = new { secret = "must-not-leak" }
        };
        await fixture.Repository.AppendRunEventAsync(new(
            TestScope.WorkspaceId, childId, 0, FlowRunEventType.ToolCallStarted, "notify",
            JsonSerializer.SerializeToElement(common), startedAt), default);
        await fixture.Repository.AppendRunEventAsync(new(
            TestScope.WorkspaceId, childId, 0, FlowRunEventType.ToolCallGovernanceEvaluated, "notify",
            JsonSerializer.SerializeToElement(new
            {
                common.ToolCallId,
                common.InvocationId,
                common.ToolId,
                common.ToolName,
                common.ProviderId,
                Governance = new[] { new { Decision = "allowed", HookId = "workspace-policy" } }
            }), startedAt.AddMilliseconds(5)), default);
        await fixture.Repository.AppendRunEventAsync(new(
            TestScope.WorkspaceId, childId, 0, FlowRunEventType.ToolCallCompleted, "notify",
            JsonSerializer.SerializeToElement(new
            {
                common.ToolCallId,
                common.InvocationId,
                common.ToolId,
                common.ToolNamespace,
                common.ToolName,
                common.ProviderId,
                common.ProviderNamespace,
                common.ExternalToolId,
                common.CorrelationId,
                Outcome = "succeeded",
                DurationMilliseconds = 12d
            }), startedAt.AddMilliseconds(12)), default);

        var first = await runs.GetCausalityAsync(childId, 0, 1, TestScope, default);
        Assert.AreEqual("root-causal", first.Origin.RootFlowRunId);
        Assert.AreEqual(FlowInvocationOrigin.Mcp, first.Origin.InvocationOrigin);
        Assert.AreEqual("news-agent", first.Origin.CallerId);
        Assert.AreEqual("news-42", first.Origin.CausationId);
        Assert.AreEqual(2, first.TotalCount);
        Assert.IsTrue(first.HasMore);
        Assert.HasCount(1, first.Items);
        Assert.IsTrue(first.Items[0].ResolvedFromActiveReference);

        var second = await runs.GetCausalityAsync(childId, 1, 1, TestScope, default);
        Assert.HasCount(1, second.Items);
        var child = second.Items.Single();
        Assert.AreEqual("root-causal", child.ParentFlowRunId);
        Assert.AreEqual("analyze", child.ParentStepName);
        Assert.AreEqual("1.0.0", child.FlowVersion);
        Assert.IsTrue(child.ResolvedFromActiveReference);
        Assert.HasCount(1, child.ToolCalls);
        var call = child.ToolCalls.Single();
        Assert.AreEqual(toolCallId, call.LogicalCallId);
        Assert.AreEqual("agentstration.internal", call.ProviderId);
        Assert.HasCount(1, call.Attempts);
        var attempt = call.Attempts.Single();
        Assert.AreEqual(2, attempt.Attempt);
        Assert.AreEqual("succeeded", attempt.Status);
        Assert.AreEqual(1, attempt.GovernanceEvaluationCount);
        Assert.DoesNotContain("must-not-leak", JsonSerializer.Serialize(second), StringComparison.Ordinal);

        var otherScope = TestScope with { WorkspaceId = new(Guid.NewGuid()) };
        await Assert.ThrowsExactlyAsync<FlowRunNotFoundException>(() =>
            runs.GetCausalityAsync(childId, 0, 10, otherScope, default));
    }
}
