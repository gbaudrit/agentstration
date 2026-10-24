using System.Text.Json;
using Agentstration.Flows;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class FlowGraphContractResolutionTests
{
    [TestMethod]
    public void ResolveInputSchemaPrefersTheInputStepContract()
    {
        var legacy = Schema("legacy");
        var expected = Schema("step");
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            InputSchema = legacy,
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", Schema = expected },
                new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success }
            ]
        };

        var actual = graph.ResolveInputSchema();

        Assert.IsNotNull(actual);
        Assert.IsTrue(JsonElement.DeepEquals(expected, actual.Value));
    }

    [TestMethod]
    public void ResolveSingleSuccessOutputSchemaReadsTheNamedOutputContract()
    {
        var expected = Schema("result");
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new OutputFlowStepDefinition
                {
                    Name = "completed",
                    Outcome = FlowOutputOutcome.Success,
                    Schema = expected
                },
                new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }
            ]
        };

        var actual = graph.ResolveSingleSuccessOutputSchema(out var ambiguous);

        Assert.IsFalse(ambiguous);
        Assert.IsNotNull(actual);
        Assert.IsTrue(JsonElement.DeepEquals(expected, actual.Value));
    }

    [TestMethod]
    public void ResolveSingleSuccessOutputSchemaReportsDifferentNamedContractsAsAmbiguous()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new OutputFlowStepDefinition
                {
                    Name = "completed",
                    Outcome = FlowOutputOutcome.Success,
                    Schema = Schema("result")
                },
                new OutputFlowStepDefinition
                {
                    Name = "alternate",
                    Outcome = FlowOutputOutcome.Success,
                    Schema = Schema("alternate")
                }
            ]
        };

        var actual = graph.ResolveSingleSuccessOutputSchema(out var ambiguous);

        Assert.IsTrue(ambiguous);
        Assert.IsNull(actual);
    }

    private static JsonElement Schema(string property) => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            [property] = new { type = "string" }
        }
    });
}
