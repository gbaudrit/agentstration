using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task FinalTokenUsageIsPersistedWithSeparateDirectAndInclusiveMetrics()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        const string rootId = "usage-root";
        const string childId = "usage-child";
        var root = UsageRun(rootId,
            [
                AgentStep("first", FlowStepRunStatus.Succeeded, new(20, 5, 25)),
                AgentStep("second", FlowStepRunStatus.Succeeded, null),
                new FlowStepRun
                {
                    StepName = "child",
                    StepType = "flowCall",
                    Status = FlowStepRunStatus.Succeeded,
                    ChildFlowRunId = childId,
                    ChildFlowRunIds = [childId, childId]
                }
            ]);
        var child = UsageRun(childId, [AgentStep("nested", FlowStepRunStatus.Succeeded, new(7, 3, 10))]) with
        {
            ParentFlowRunId = rootId,
            RootFlowRunId = rootId,
            NestingDepth = 1
        };
        var storedRoot = await fixture.Repository.CreateRunAsync(root, default);
        var storedChild = await fixture.Repository.CreateRunAsync(child, default);
        var consolidator = new FlowRunTokenUsageConsolidator(fixture.Repository);
        var childUsage = await consolidator.FinalizeAsync(child, default);
        await fixture.Repository.UpdateRunAsync(child with { TokenUsage = childUsage }, storedChild.ETag, default);

        var usage = await consolidator.FinalizeAsync(root, default);
        await fixture.Repository.UpdateRunAsync(root with { TokenUsage = usage }, storedRoot.ETag, default);
        var persisted = await fixture.Repository.GetRunAsync(TestScope.WorkspaceId, rootId, default);

        Assert.IsNotNull(persisted);
        Assert.AreEqual(FlowRunTokenUsageState.Final, persisted.Value.TokenUsage.State);
        Assert.AreEqual(20, persisted.Value.TokenUsage.Direct.InputTokens);
        Assert.AreEqual(5, persisted.Value.TokenUsage.Direct.OutputTokens);
        Assert.AreEqual(25, persisted.Value.TokenUsage.Direct.TotalTokens);
        Assert.AreEqual(FlowRunTokenUsageCoverage.Partial, persisted.Value.TokenUsage.Direct.Coverage);
        Assert.AreEqual(1, persisted.Value.TokenUsage.Direct.ReportedInvocationCount);
        Assert.AreEqual(2, persisted.Value.TokenUsage.Direct.InvocationCount);
        Assert.AreEqual(27, persisted.Value.TokenUsage.Inclusive.InputTokens);
        Assert.AreEqual(8, persisted.Value.TokenUsage.Inclusive.OutputTokens);
        Assert.AreEqual(35, persisted.Value.TokenUsage.Inclusive.TotalTokens);
        Assert.AreEqual(2, persisted.Value.TokenUsage.Inclusive.ReportedInvocationCount);
        Assert.AreEqual(3, persisted.Value.TokenUsage.Inclusive.InvocationCount);
        var foreignScope = TestScope with { WorkspaceId = new WorkspaceId(Guid.NewGuid()) };
        Assert.IsNull(await fixture.Repository.GetRunAsync(foreignScope, rootId, default));
    }

    [TestMethod]
    public async Task FinalTokenUsageIsIdempotentAndKeepsMeasuredZeroDistinctFromUnavailableUsage()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var run = UsageRun("usage-retry",
            [
                AgentStep("retried", FlowStepRunStatus.Failed, new(0, 0, 0)) with { Attempt = 3 },
                AgentStep("cancelled", FlowStepRunStatus.Cancelled, null)
            ]);
        var consolidator = new FlowRunTokenUsageConsolidator(fixture.Repository);

        var first = await consolidator.FinalizeAsync(run, default);
        var replayed = await consolidator.FinalizeAsync(run, default);

        Assert.AreEqual(first, replayed);
        Assert.AreEqual(0, first.Direct.InputTokens);
        Assert.AreEqual(0, first.Direct.OutputTokens);
        Assert.AreEqual(0, first.Direct.TotalTokens);
        Assert.AreEqual(FlowRunTokenUsageCoverage.Partial, first.Direct.Coverage);
        Assert.AreEqual(1, first.Direct.ReportedInvocationCount);
        Assert.AreEqual(2, first.Direct.InvocationCount);

        var unavailable = await consolidator.FinalizeAsync(
            UsageRun("usage-unavailable", [AgentStep("missing", FlowStepRunStatus.Succeeded, null)]), default);
        Assert.IsNull(unavailable.Direct.InputTokens);
        Assert.IsNull(unavailable.Direct.OutputTokens);
        Assert.IsNull(unavailable.Direct.TotalTokens);
        Assert.AreEqual(FlowRunTokenUsageCoverage.Unavailable, unavailable.Direct.Coverage);
        Assert.AreEqual(0, unavailable.Direct.ReportedInvocationCount);
        Assert.AreEqual(1, unavailable.Direct.InvocationCount);
    }

    private static FlowRun UsageRun(string id, IReadOnlyList<FlowStepRun> steps)
    {
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent"));
        return new FlowRun
        {
            WorkspaceId = TestScope.WorkspaceId,
            Id = id,
            FlowId = new FlowId("usage-flow"),
            FlowVersion = "1.0.0",
            Status = FlowRunStatus.Succeeded,
            Trigger = FlowRunTrigger.Manual,
            Scope = TestScope,
            Input = JsonSerializer.SerializeToElement(new { }),
            CreatedAt = Now,
            CompletedAt = Now,
            DefinitionSnapshot = new FlowVersion(ResourceScopeRef.Workspace(TestScope.WorkspaceId.Value), new FlowId("usage-flow"), "1.0.0", null,
                definition, new Dictionary<string, string>(), Now),
            Steps = steps
        };
    }

    private static FlowStepRun AgentStep(string name, FlowStepRunStatus status, FlowStepRunUsage? usage) => new()
    {
        StepName = name,
        StepType = "agent",
        Status = status,
        StartedAt = Now,
        CompletedAt = Now,
        Attempt = 1,
        Usage = usage
    };
}
