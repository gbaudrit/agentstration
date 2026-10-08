using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class FlowToolStepTests
{
    [TestMethod]
    public async Task ToolRouteStepPinsAnExactToolSetVersionAndValidatesTheSelectedToolSchema()
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { query = new { type = "string" } },
            required = new[] { "query" }
        });
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new ToolRouteFlowStepDefinition
                {
                    Name = "search",
                    ToolSet = new("docs", "1.0.0"),
                    Capability = "knowledge.search",
                    Route = "search",
                    ArgumentsMapping = JsonSerializer.SerializeToElement(new { query = "${input.query}" })
                },
                new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.search.output}") }
            ],
            Transitions =
            [
                new("input-search", "input", "completed", "search"),
                new("search-output", "search", "success", "output"),
                new("search-error", "search", "error", "output")
            ]
        };
        var yaml = FlowDraftService.ToYaml(graph);
        var restored = new FlowDraftService(null!, null!, null!, TimeProvider.System).ParseSource(yaml, "yaml");
        var route = Assert.IsInstanceOfType<ToolRouteFlowStepDefinition>(restored.Steps[1]);
        Assert.AreEqual("1.0.0", route.ToolSet.Version);
        var result = await new FlowGraphValidator(new ToolResolver(schema)).ValidateAsync(
            restored, new(true, Workspace, new FlowId("parent")), default);
        Assert.IsTrue(result.IsValid, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
    }

    [TestMethod]
    public void YamlInputSchemaPreservesBooleanAndNumericConstraintTypes()
    {
        const string yaml = """
            entryStep: input
            inputSchema:
              type: object
              properties:
                prompt:
                  type: string
                  minLength: 1
                  maxLength: 16000
              required:
              - prompt
              additionalProperties: false
            steps:
            - type: input
              name: input
            transitions: []
            """;

        var graph = new FlowDraftService(null!, null!, null!, TimeProvider.System).ParseSource(yaml, "yaml");
        var schema = graph.InputSchema!.Value;
        var prompt = schema.GetProperty("properties").GetProperty("prompt");

        Assert.AreEqual(JsonValueKind.Number, prompt.GetProperty("minLength").ValueKind);
        Assert.AreEqual(1, prompt.GetProperty("minLength").GetInt32());
        Assert.AreEqual(JsonValueKind.Number, prompt.GetProperty("maxLength").ValueKind);
        Assert.AreEqual(16000, prompt.GetProperty("maxLength").GetInt32());
        Assert.AreEqual(JsonValueKind.False, schema.GetProperty("additionalProperties").ValueKind);
    }

    [TestMethod]
    public async Task ToolStepRoundTripsAndValidatesMappedSchema()
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { message = new { type = "string" } },
            required = new[] { "message" }
        });
        var graph = Graph(JsonSerializer.SerializeToElement(new { message = "${input.text}" }));
        var yaml = FlowDraftService.ToYaml(graph);
        var restored = new FlowDraftService(null!, null!, null!, TimeProvider.System).ParseSource(yaml, "yaml");
        var step = Assert.IsInstanceOfType<ToolFlowStepDefinition>(restored.Steps[1]);
        Assert.AreEqual("notification.send", step.Tool.ResourceId);

        var validator = new FlowGraphValidator(new ToolResolver(schema));
        var valid = await validator.ValidateAsync(restored, new(true, Workspace, new FlowId("parent")), default);
        Assert.IsTrue(valid.IsValid, string.Join(Environment.NewLine, valid.Issues.Select(issue => issue.Message)));

        var invalid = Graph(JsonSerializer.SerializeToElement(new { unexpected = "value" }));
        var result = await validator.ValidateAsync(invalid, new(true, Workspace, new FlowId("parent")), default);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "tool_argument_unknown"));
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "tool_argument_required"));
    }

    [TestMethod]
    public async Task AgentAndToolTransitionsRejectLegacyOutcomeEvents()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new AgentFlowStepDefinition { Name = "agent", Agent = new("assistant") },
                new ToolFlowStepDefinition { Name = "tool", Tool = new("notification.send") },
                new OutputFlowStepDefinition { Name = "output" }
            ],
            Transitions =
            [
                new("input-agent", "input", "completed", "agent"),
                new("agent-tool", "agent", "completed", "tool"),
                new("tool-output", "tool", "failed", "output")
            ]
        };

        var result = await new FlowGraphValidator(new ToolResolver(schema)).ValidateAsync(
            graph,
            new(true, Workspace, new FlowId("parent")),
            default);

        CollectionAssert.AreEquivalent(
            new[] { "agent-tool", "tool-output" },
            result.Issues.Where(issue => issue.Code == "transition_event_invalid").Select(issue => issue.TransitionId).ToArray());
    }

    private static FlowGraphDefinition Graph(JsonElement mapping) => new()
    {
        EntryStep = "input",
        Steps =
        [
            new InputFlowStepDefinition { Name = "input" },
            new ToolFlowStepDefinition { Name = "notify", Tool = new("notification.send"), ArgumentsMapping = mapping },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.notify.output}") },
            new FailureFlowStepDefinition { Name = "failure" }
        ],
        Transitions =
        [
            new("input-notify", "input", "completed", "notify"),
            new("notify-output", "notify", "success", "output"),
            new("notify-error", "notify", "error", "failure")
        ]
    };

    private static readonly WorkspaceId Workspace = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    private sealed class ToolResolver(JsonElement schema) : IFlowResourceReferenceResolver
    {
        public Task<bool> ExistsAsync(string resourceId, ResourceNamespace? @namespace, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<ResolvedFlowTool?> ResolveToolAsync(
            WorkspaceId workspaceId,
            ResourceNamespace ownerNamespace,
            FlowToolReference reference,
            CancellationToken cancellationToken) => Task.FromResult<ResolvedFlowTool?>(new(
                reference.ResourceId,
                reference.ResolveNamespace(ownerNamespace),
                schema,
                null,
                Enabled: true,
                Available: true,
                RequiresApproval: false));

        public Task<ResolvedFlowToolRoute?> ResolveToolRouteAsync(
            WorkspaceId workspaceId,
            ResourceNamespace ownerNamespace,
            ToolRouteFlowStepDefinition step,
            CancellationToken cancellationToken) => Task.FromResult<ResolvedFlowToolRoute?>(new(
                new FlowToolReference("docs.search"),
                step.ToolSet.ResourceId,
                step.ToolSet.ResolveNamespace(ownerNamespace),
                step.ToolSet.Version,
                step.Capability,
                step.Route ?? "search",
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                1,
                "internal",
                ResourceNamespace.Default,
                schema,
                null,
                false));
    }
}
