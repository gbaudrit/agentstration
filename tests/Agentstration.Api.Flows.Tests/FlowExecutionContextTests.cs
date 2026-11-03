using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public void ExecutionContextExpressionsAcceptOnlyDocumentedProperties()
    {
        var parser = new FlowExpressionParser();

        Assert.IsTrue(parser.Parse("${execution.flowRunId}").IsValid);
        Assert.IsTrue(parser.Parse("${execution.rootFlowRunId}").IsValid);
        Assert.IsTrue(parser.Parse("${execution.parentFlowRunId}").IsValid);
        Assert.IsTrue(parser.Parse("${execution.stepName}").IsValid);
        Assert.IsTrue(parser.Parse("${execution.correlationId}").IsValid);
        Assert.IsTrue(parser.Parse("${step.displayName}").IsValid);

        var unknown = parser.Parse("${execution.workspaceId}");
        Assert.IsFalse(unknown.IsValid);
        StringAssert.Contains(unknown.Error, "unknown execution context property");
    }

    [TestMethod]
    public async Task StepDisplayNameExpressionFallsBackToTheTechnicalName()
    {
        var parser = new FlowExpressionParser();
        var expression = parser.Parse("${step.displayName}").Expression!;
        var input = JsonSerializer.SerializeToElement(new { });
        var outputs = new Dictionary<string, JsonElement?>();

        var fallback = await parser.EvaluateAsync(expression,
            new(input, outputs, Execution: new("run", "run", null, "capture", null)), default);
        var displayName = await parser.EvaluateAsync(expression,
            new(input, outputs, Execution: new("run", "run", null, "capture", null, "Capture result")),
            default);

        Assert.AreEqual("capture", fallback?.GetString());
        Assert.AreEqual("Capture result", displayName?.GetString());
    }

    [TestMethod]
    public async Task FlowAuthoringRejectsUnknownExecutionContextProperties()
    {
        var graph = ExecutionContextGraph() with
        {
            Steps = ExecutionContextGraph().Steps.Select(step => step is TransformFlowStepDefinition transform
                ? transform with { Mapping = JsonSerializer.SerializeToElement(new { value = "${execution.workspaceId}" }) }
                : step).ToArray()
        };

        var result = await new FlowGraphValidator(null!).ValidateAsync(
            graph,
            new FlowValidationContext(ResolveResources: false),
            default);

        var issue = result.Issues.Single(value => value.Code == "expression_invalid");
        StringAssert.Contains(issue.Message, "unknown execution context property");
        Assert.AreEqual("capture", issue.StepId);
    }

    private static FlowGraphDefinition ExecutionContextGraph() => new()
    {
        EntryStep = "input",
        Steps =
        [
            new InputFlowStepDefinition { Name = "input" },
            new TransformFlowStepDefinition
            {
                Name = "capture",
                Mapping = JsonSerializer.SerializeToElement(new
                {
                    flowRunId = "${execution.flowRunId}",
                    rootFlowRunId = "${execution.rootFlowRunId}",
                    parentFlowRunId = "${execution.parentFlowRunId}",
                    stepName = "${execution.stepName}",
                    correlationId = "${execution.correlationId}"
                })
            },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.capture.output}") }
        ],
        Transitions =
        [
            new("input-capture", "input", "completed", "capture"),
            new("capture-output", "capture", "completed", "output")
        ]
    };

}
