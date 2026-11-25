using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Flows;
using Agentstration.Runtime.Worker.MicrosoftAgentFramework;

namespace Agentstration.Runtime.Tests;

public sealed partial class AwpAssignmentExecutorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestoringSuccessfulGraphCheckpointsDoesNotReinvokeSourceOrTransformations(bool publicationAcknowledged)
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "source",
            Steps = [
                new ToolFlowStepDefinition { Name = "source", Tool = new("source") },
                new ToolFlowStepDefinition { Name = "transformations", Tool = new("transform") },
                new ToolFlowStepDefinition { Name = "publication", Tool = new("publish") },
                new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.transformations.output}") }
            ],
            Transitions = [new("source-next", "source", "success", "transformations"), new("transformed", "transformations", "success", "publication"), new("published", "publication", "success", "output")]
        };
        var retained = JsonSerializer.SerializeToElement(new { value = "accepted transformed output" });
        var completed = new List<FlowStepRun>
        {
            new() { StepName = "source", StepType = "tool", Status = FlowStepRunStatus.Succeeded, Output = retained, SelectedTransition = "source-next" },
            new() { StepName = "transformations", StepType = "tool", Status = FlowStepRunStatus.Succeeded, Output = retained, SelectedTransition = "transformed" }
        };
        if (publicationAcknowledged) completed.Add(new() { StepName = "publication", StepType = "tool", Status = FlowStepRunStatus.Succeeded, Output = retained, SelectedTransition = "published" });
        var (result, handler) = await ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { }), completedSteps: completed);
        Assert.AreEqual(publicationAcknowledged ? 0 : 1, handler.FlowToolInvocationCount);
        Assert.IsTrue(JsonElement.DeepEquals(retained, result.Output!.Value));
        Assert.IsFalse(handler.Events.Any(value => value.Kind == AwpExecutionEventKind.StepStarted && value.Location.FlowStep?.StepDefinitionId is "source" or "transformations"));
    }

    [TestMethod]
    public async Task CheckpointCannotSelectATransitionFromAnotherStep()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "source",
            Steps = [new InputFlowStepDefinition { Name = "source" }, new OutputFlowStepDefinition { Name = "output" }],
            Transitions = [new("source-next", "source", "completed", "output")]
        };
        await Assert.ThrowsAsync<AwpExecutionNotSupportedException>(() => ExecuteGraphAsync(graph,
            JsonSerializer.SerializeToElement(new { }), completedSteps: [new() { StepName = "source", StepType = "input", Status = FlowStepRunStatus.Succeeded, SelectedTransition = "foreign-transition" }]));
    }
}
