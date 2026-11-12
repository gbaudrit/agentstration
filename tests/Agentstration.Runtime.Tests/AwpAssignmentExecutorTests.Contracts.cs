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
}
