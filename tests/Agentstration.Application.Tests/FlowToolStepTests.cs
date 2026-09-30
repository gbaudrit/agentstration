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
                new("search-output", "search", "completed", "output")
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

    private static FlowGraphDefinition Graph(JsonElement mapping) => new()
    {
        EntryStep = "input",
        Steps =
        [
            new InputFlowStepDefinition { Name = "input" },
            new ToolFlowStepDefinition { Name = "notify", Tool = new("notification.send"), ArgumentsMapping = mapping },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.notify.output}") }
        ],
        Transitions =
        [
            new("input-notify", "input", "completed", "notify"),
            new("notify-output", "notify", "completed", "output")
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
