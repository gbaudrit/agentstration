using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Web.FlowDesigner.Components;

namespace Agentstration.Web.FlowDesigner.Tests;

[TestClass]
public sealed class FlowTopologyProjectorTests
{
    [TestMethod]
    public void WorkflowProjectionPreservesBranchesPositionsAndConditions()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new ConditionFlowStepDefinition { Name = "decision", Mode = "Simple" },
                new OutputFlowStepDefinition { Name = "accepted" },
                new FailureFlowStepDefinition { Name = "rejected" }
            ],
            Transitions =
            [
                new("to-decision", "input", "completed", "decision"),
                new("yes", "decision", "true", "accepted", "${input.approved}"),
                new("no", "decision", "false", "rejected")
            ],
            Designer = new FlowDesignerMetadata
            {
                NodePositions = new Dictionary<string, FlowNodePosition>
                {
                    ["input"] = new(10, 20),
                    ["decision"] = new(200, 20),
                    ["accepted"] = new(400, 0),
                    ["rejected"] = new(400, 140)
                }
            }
        };

        var topology = FlowTopologyProjector.Project(WorkflowDefinition(), graph);

        Assert.HasCount(4, topology.Nodes);
        Assert.HasCount(3, topology.Edges);
        Assert.AreEqual(10d, topology.FindBySelection("input")!.X);
        Assert.AreEqual(FlowTopologyEdgeKind.Conditional, topology.Edges.Single(edge => edge.Id == "yes").Kind);
        Assert.AreEqual("true · ${input.approved}", topology.Edges.Single(edge => edge.Id == "yes").Label);
        Assert.AreNotEqual(topology.FindBySelection("accepted")!.Y, topology.FindBySelection("rejected")!.Y);
    }

    [TestMethod]
    public void ConcurrentProjectionUsesFanOutInsteadOfParticipantSequence()
    {
        var definition = Orchestration(new ConcurrentOrchestrationPattern());

        var topology = FlowTopologyProjector.Project(definition);

        Assert.AreEqual("fan-out", topology.Layout);
        Assert.HasCount(2, topology.Edges.Where(edge => edge.From == "system:input"));
        Assert.IsFalse(topology.Edges.Any(edge => edge.From == "participant:researcher" && edge.To == "participant:reviewer"));
    }

    [TestMethod]
    public void HandoffProjectionShowsDirectedRoutesAndObservedTransfer()
    {
        var definition = Orchestration(new HandoffOrchestrationPattern(
            "researcher",
            [new FlowHandoff("researcher", "reviewer"), new FlowHandoff("reviewer", "researcher")],
            Autonomous: true));
        var run = Run(definition);
        var events = new[]
        {
            Event(1, "researcher"),
            HandoffEvent(2, "researcher", "reviewer"),
            Event(3, "reviewer")
        };

        var topology = FlowTopologyProjector.Project(run, events);

        Assert.IsTrue(topology.FindBySelection("researcher")!.IsInitial);
        Assert.AreEqual(FlowTopologyEdgeState.Observed,
            topology.Edges.Single(edge => edge.From == "participant:researcher" && edge.To == "participant:reviewer").State);
        Assert.Contains("#1", topology.Edges.Single(edge => edge.From == "participant:researcher" && edge.To == "participant:reviewer").Label!, StringComparison.Ordinal);
        Assert.AreEqual(FlowTopologyEdgeState.Declared,
            topology.Edges.Single(edge => edge.From == "participant:reviewer" && edge.To == "participant:researcher").State);
        Assert.HasCount(1, topology.Transfers);
        Assert.AreEqual(new FlowTopologyTransfer(1, "researcher", "reviewer", 2), topology.Transfers[0]);
    }

    [TestMethod]
    public void HandoffProjectionNumbersRepeatedTransfersInExecutionOrder()
    {
        var definition = Orchestration(new HandoffOrchestrationPattern(
            "researcher",
            [new FlowHandoff("researcher", "reviewer"), new FlowHandoff("reviewer", "researcher")],
            Autonomous: true));
        var events = new[]
        {
            HandoffEvent(10, "researcher", "reviewer"),
            HandoffEvent(20, "reviewer", "researcher"),
            HandoffEvent(30, "researcher", "reviewer")
        };

        var topology = FlowTopologyProjector.Project(Run(definition), events);

        Assert.HasCount(3, topology.Transfers);
        Assert.AreEqual("handoff · #1, #3",
            topology.Edges.Single(edge => edge.From == "participant:researcher" && edge.To == "participant:reviewer").Label);
        Assert.AreEqual("handoff · #2",
            topology.Edges.Single(edge => edge.From == "participant:reviewer" && edge.To == "participant:researcher").Label);
    }

    [TestMethod]
    public void HandoffProjectionRanksParticipantsFromInitialRouteAndLeavesRoomBetweenLanes()
    {
        var definition = new OrchestrationFlowDefinition(
            [
                new(FlowTargetKind.Agent, "welcome"),
                new(FlowTargetKind.Agent, "solution-advisor"),
                new(FlowTargetKind.Agent, "technical-expert"),
                new(FlowTargetKind.Agent, "integration-expert")
            ],
            new HandoffOrchestrationPattern(
                "welcome",
                [
                    new("welcome", "solution-advisor"),
                    new("welcome", "technical-expert"),
                    new("solution-advisor", "integration-expert"),
                    new("technical-expert", "integration-expert"),
                    new("integration-expert", "welcome")
                ]));

        var topology = FlowTopologyProjector.Project(definition);

        var welcome = topology.FindBySelection("welcome")!;
        var advisor = topology.FindBySelection("solution-advisor")!;
        var technical = topology.FindBySelection("technical-expert")!;
        var integration = topology.FindBySelection("integration-expert")!;
        var output = topology.Nodes.Single(node => node.Id == "system:output");
        Assert.IsTrue(advisor.X - welcome.X >= 300);
        Assert.AreEqual(advisor.X, technical.X);
        Assert.IsTrue(Math.Abs(advisor.Y - technical.Y) >= 170);
        Assert.IsTrue(integration.X - advisor.X >= 300);
        Assert.IsTrue(output.X - integration.X >= 300);
    }

    [TestMethod]
    public void GroupChatProjectionUsesSharedConversationHubAndObservedTurnOrder()
    {
        var definition = Orchestration(new GroupChatOrchestrationPattern(8));

        var topology = FlowTopologyProjector.Project(Run(definition), [Event(1, "researcher"), Event(2, "reviewer")]);

        Assert.AreEqual("hub", topology.Layout);
        Assert.IsTrue(topology.Nodes.Single(node => node.Id == "coordinator:conversation").IsCoordinator);
        Assert.HasCount(2, topology.Edges.Where(edge => edge.Kind == FlowTopologyEdgeKind.Collaboration && edge.Bidirectional));
        Assert.IsTrue(topology.Edges.Any(edge => edge.Kind == FlowTopologyEdgeKind.Dynamic && edge.State == FlowTopologyEdgeState.Observed));
        Assert.AreEqual(1, topology.FindBySelection("researcher")!.TurnCount);
    }

    [TestMethod]
    public void MagenticProjectionKeepsManagerDistinctFromParticipants()
    {
        var definition = Orchestration(new MagenticOrchestrationPattern(new(FlowTargetKind.Agent, "manager"), MaximumRounds: 6));

        var topology = FlowTopologyProjector.Project(definition);

        var manager = topology.Nodes.Single(node => node.Id == "coordinator:manager");
        Assert.IsTrue(manager.IsCoordinator);
        Assert.AreEqual("manager", manager.SelectionKey);
        Assert.HasCount(2, topology.Nodes.Where(node => node.Id.StartsWith("participant:", StringComparison.Ordinal)));
        Assert.HasCount(2, topology.Edges.Where(edge => edge.From == manager.Id && edge.Kind == FlowTopologyEdgeKind.Collaboration));
    }

    [TestMethod]
    public void RunProjectionHighlightsSelectedWorkflowTransitionAndStatuses()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps = [new InputFlowStepDefinition { Name = "input" }, new OutputFlowStepDefinition { Name = "output" }, new FailureFlowStepDefinition { Name = "failure" }],
            Transitions = [new("success", "input", "completed", "output"), new("failed", "input", "failed", "failure")]
        };
        var run = Run(WorkflowDefinition(), graph) with
        {
            Steps =
            [
                new FlowStepRun { StepName = "input", StepType = "input", Status = FlowStepRunStatus.Succeeded, SelectedTransition = "success" },
                new FlowStepRun { StepName = "output", StepType = "output", Status = FlowStepRunStatus.Succeeded },
                new FlowStepRun { StepName = "failure", StepType = "failure", Status = FlowStepRunStatus.Skipped }
            ]
        };

        var topology = FlowTopologyProjector.Project(run);

        Assert.AreEqual(FlowTopologyEdgeState.Observed, topology.Edges.Single(edge => edge.Id == "success").State);
        Assert.AreEqual(FlowTopologyEdgeState.Inactive, topology.Edges.Single(edge => edge.Id == "failed").State);
        Assert.AreEqual("Skipped", topology.FindBySelection("failure")!.Status);
    }

    private static WorkflowFlowDefinition WorkflowDefinition() => new(
        "input",
        [new FlowNode("input", FlowNodeKind.Input)],
        []);

    private static OrchestrationFlowDefinition Orchestration(FlowOrchestrationPattern pattern) => new(
        [new(FlowTargetKind.Agent, "researcher"), new(FlowTargetKind.Agent, "reviewer")],
        pattern);

    private static FlowRun Run(FlowDefinition definition, FlowGraphDefinition? graph = null) => new()
    {
        WorkspaceId = WorkspaceId,
        Id = "run-1",
        FlowId = new("flow"),
        FlowVersion = "1.0.0",
        Input = JsonSerializer.SerializeToElement(new { prompt = "test" }),
        CreatedAt = DateTimeOffset.UtcNow,
        Scope = new FlowRunScope(Guid.Empty, WorkspaceId, Guid.Empty),
        DefinitionSnapshot = new FlowVersion(WorkspaceId, new("flow"), "1.0.0", null, definition, new Dictionary<string, string>(), DateTimeOffset.UtcNow, graph)
    };

    private static FlowRunEvent Event(long sequence, string participant) => new(
        WorkspaceId,
        "run-1",
        sequence,
        FlowRunEventType.ParticipantTurnStarted,
        participant,
        JsonSerializer.SerializeToElement(new { turn = sequence }),
        DateTimeOffset.UtcNow);

    private static FlowRunEvent HandoffEvent(long sequence, string from, string to) => new(
        WorkspaceId,
        "run-1",
        sequence,
        FlowRunEventType.ParticipantHandoff,
        from,
        JsonSerializer.SerializeToElement(new { from, to }),
        DateTimeOffset.UtcNow);

    private static readonly Agentstration.Resources.WorkspaceId WorkspaceId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
}
