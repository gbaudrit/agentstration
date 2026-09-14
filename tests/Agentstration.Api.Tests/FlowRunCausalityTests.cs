using System.Globalization;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;

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
        await runs.ExecuteAsync(new(root.Value.Id, TestScope), default);
        var waiting = (await runs.GetAsync(root.Value.Id, TestScope, default))!.Value;
        var childId = waiting.Steps.Single(step => step.StepName == "analyze").ChildFlowRunId!;
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
