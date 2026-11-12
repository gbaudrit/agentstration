using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Flows;
using Agentstration.Schemas;

namespace Agentstration.Runtime.Tests;

public sealed partial class AwpAssignmentExecutorTests
{
    [TestMethod]
    public async Task AgentFixedOutputIsInSystemInstructionsAndBecomesStructuredFlowOutput()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "safe" }, properties = new { safe = new { type = "boolean" } } });
        var graph = AgentAnswerGraph();
        graph = graph with { Steps = graph.Steps.Select(step => step is AgentFlowStepDefinition agent
            ? (FlowStepDefinition)(agent with { Agent = agent.Agent with { OutputSchema = schema } }) : step).ToArray() };
        var (result, handler) = await ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { }),
            new GraphHandler(TestNow) { ModelOutput = "{\"safe\":true}" },
            agentMaterial: AgentMaterial() with { ParticipantId = "answer", AgentName = "assistant", OutputSchema = schema });
        Assert.AreEqual("success", result.Outcome);
        Assert.IsTrue(result.Output?.GetProperty("safe").GetBoolean());
        StringAssert.Contains(FlowDataContracts.AgentInstructions("base", null, DataContracts.Declared(schema, "agent"), null), "Output data contract for this invocation");
        var evidence = handler.Events.Single(item => item.Kind == AwpExecutionEventKind.StepCompleted && item.Location.FlowStep?.StepDefinitionId == "answer");
        Assert.IsTrue(evidence.Payload?.GetProperty("contracts").GetProperty("ExpectedOutput").GetProperty("Validated").GetBoolean());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidAgentJsonRemainsACausalContractFailureAtRoot(bool customFailure)
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var graph = AgentAnswerGraph();
        graph = graph with { Steps = [.. graph.Steps, new OutputFlowStepDefinition { Name = "failure", Outcome = FlowOutputOutcome.Error,
                Code = customFailure ? "ANONYMIZATION_FAILED" : null, DetailsExpression = customFailure ? "${transition.output}" : null }],
            Transitions = [.. graph.Transitions, new("failed", "answer", "error", "failure")] };
        var (result, handler) = await ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { }),
            new GraphHandler(TestNow) { ModelOutput = "private fixture text" },
            agentMaterial: AgentMaterial() with { ParticipantId = "answer", AgentName = "assistant", OutputSchema = schema });
        Assert.AreEqual("error", result.Outcome);
        Assert.AreEqual(customFailure ? "ANONYMIZATION_FAILED" : "data_contract_json_invalid", result.ErrorCode);
        var failure = DataContracts.ParseFailure(result.ErrorDetails)!;
        Assert.AreEqual("answer", failure.StepName);
        Assert.AreEqual("flowrun-root", failure.FlowRunId);
        Assert.DoesNotContain("private fixture text", result.ErrorMessage! + result.ErrorDetails);
        Assert.IsTrue(handler.Events.Any(item => item.Kind == AwpExecutionEventKind.StepCompleted
            && item.Payload?.GetProperty("errorCode").GetString() == "data_contract_json_invalid"));
    }
    [TestMethod]
    public async Task InvalidNamedSuccessOutputCannotRouteAParentThroughItsSuccessTransition()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var child = new FlowGraphDefinition { EntryStep = "completed", Steps = [new OutputFlowStepDefinition {
            Name = "completed", Outcome = FlowOutputOutcome.Success, Schema = schema,
            OutputMapping = JsonSerializer.SerializeToElement("${input.missing}") }] };
        var graph = new FlowGraphDefinition { EntryStep = "child", Steps = [
            new FlowCallStepDefinition { Name = "child", Flow = new("child") },
            new TransformFlowStepDefinition { Name = "postAggregate", Schema = schema,
                Mapping = JsonSerializer.SerializeToElement("${steps.child.output}") },
            new OutputFlowStepDefinition { Name = "done", Outcome = FlowOutputOutcome.Success }],
            Transitions = [new("child-success", "child", "completed", "postAggregate"), new("done", "postAggregate", "completed", "done")] };
        var handler = new GraphHandler(TestNow) { ChildGraph = child };
        var error = await Assert.ThrowsAsync<Agentstration.Runtime.Worker.MicrosoftAgentFramework.AwpExecutionNotSupportedException>(
            () => ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { }), handler));
        Assert.AreEqual("data_contract_violation", error.Code);
        var failure = DataContracts.ParseFailure(error.Details)!;
        Assert.AreEqual("completed", failure.StepName);
        Assert.AreEqual("flow-output", failure.Boundary);
        Assert.IsFalse(handler.Events.Any(item => item.Kind == AwpExecutionEventKind.StepStarted && item.Location.FlowStep?.StepDefinitionId == "postAggregate"));
    }

    [TestMethod]
    public async Task DanglingOutputMappingIdentifiesTheReferenceInsteadOfReportingANullSchemaViolation()
    {
        var graph = new FlowGraphDefinition { EntryStep = "completed", Steps = [new OutputFlowStepDefinition {
            Name = "completed", Outcome = FlowOutputOutcome.Success, Schema = JsonSerializer.SerializeToElement(new { type = "object" }),
            OutputMapping = JsonSerializer.SerializeToElement("${steps.transform.output}") }] };
        var (result, _) = await ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { }));
        Assert.AreEqual("data_contract_mapping_unavailable", result.ErrorCode);
        var failure = DataContracts.ParseFailure(result.ErrorDetails)!;
        Assert.AreEqual("steps.transform.output", failure.Path);
        Assert.AreEqual("completed", failure.StepName);
        Assert.AreEqual("output-mapping", failure.Boundary);
        Assert.IsNull(result.OutputName);
    }

    [TestMethod]
    public async Task AvailableStepWithAnExplicitNullOutputRemainsValidForANullableContract()
    {
        var graph = new FlowGraphDefinition { EntryStep = "input", Steps = [
            new InputFlowStepDefinition { Name = "input" },
            new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success,
                Schema = JsonSerializer.SerializeToElement(new { type = new[] { "object", "null" } }),
                OutputMapping = JsonSerializer.SerializeToElement("${steps.input.output}") }],
            Transitions = [new("done", "input", "completed", "completed")] };
        var (result, _) = await ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement<object?>(null));
        Assert.AreEqual("success", result.Outcome);
        Assert.AreEqual(JsonValueKind.Null, result.Output!.Value.ValueKind);
    }
}
