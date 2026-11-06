using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.Flows;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public void RepeatRoundTripsThroughJsonAndYamlAndContributesToTheDefinitionHash()
    {
        var graph = RepeatGraph(3);

        var json = JsonSerializer.Serialize(graph, JsonOptions);
        var restored = JsonSerializer.Deserialize<FlowGraphDefinition>(json, JsonOptions)!;
        var yaml = FlowDraftService.ToYaml(graph);
        var yamlRestored = new FlowDraftService(null!, null!, null!, TimeProvider.System).ParseSource(yaml, "yaml");

        StringAssert.Contains(json, "\"type\":\"repeat\"");
        var repeat = Assert.IsInstanceOfType<RepeatFlowStepDefinition>(restored.Steps[1]);
        Assert.AreEqual(3, repeat.MaximumIterations);
        Assert.AreEqual("${steps.read.output.endOfContent == true}", repeat.Until);
        Assert.AreEqual(
            FlowDefinitionHash.Compute(graph),
            FlowDefinitionHash.Compute(yamlRestored),
            $"{json}{Environment.NewLine}{JsonSerializer.Serialize(yamlRestored, JsonOptions)}");
        Assert.AreNotEqual(FlowDefinitionHash.Compute(graph), FlowDefinitionHash.Compute(RepeatGraph(4)));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1001)]
    public async Task RepeatValidationRejectsAnUnsafeIterationLimit(int maximumIterations)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { offset = new { type = "integer" } },
            required = new[] { "offset" }
        });
        var resolver = new FlowCallResolverStub(new(new("chunk-reader"), "1.0.0", schema, JsonSerializer.SerializeToElement(new { type = "object" })));

        var result = await new FlowGraphValidator(resolver).ValidateAsync(
            RepeatGraph(maximumIterations),
            new FlowValidationContext(true, TestScope.WorkspaceId, new("parent")),
            default);

        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "flow_repeat_iterations_invalid"));
    }

    [TestMethod]
    public async Task RepositoryResolverDetectsDependencyCyclesThroughRepeatSteps()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var parent = await CreatePublishedGraphAsync(fixture, "parent", FlowDraftTemplates.Create("empty"));
        var child = await CreatePublishedGraphAsync(fixture, "child", RepeatGraph(1, "parent"));
        var resolver = new ManagementFlowResourceReferenceResolver(null!, fixture.Repository);
        var target = await resolver.ResolveFlowAsync(TestScope.WorkspaceId, parent.Value.Id.Namespace,
            new(child.Value.Id.Value, FlowCallVersionStrategy.Exact, "1.0.0"), default);

        Assert.IsNotNull(target);
        Assert.IsTrue(await resolver.CreatesFlowCycleAsync(TestScope.WorkspaceId, parent.Value.Id, target, default));
    }

    private static FlowGraphDefinition RepeatGraph(int maximumIterations, string childFlowName = "chunk-reader") => new()
    {
        EntryStep = "input",
        Steps =
        [
            new InputFlowStepDefinition { Name = "input" },
            new RepeatFlowStepDefinition
            {
                Name = "read",
                Flow = new(childFlowName, FlowCallVersionStrategy.Exact, "1.0.0"),
                InputMapping = JsonSerializer.SerializeToElement(new { offset = "${input.offset}" }),
                NextInputMapping = JsonSerializer.SerializeToElement(new { offset = "${steps.read.output.nextOffset}" }),
                Until = "${steps.read.output.endOfContent == true}",
                MaximumIterations = maximumIterations
            },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.read.output}") }
        ],
        Transitions =
        [
            new("input-read", "input", "completed", "read"),
            new("read-output", "read", "completed", "output")
        ]
    };

}
