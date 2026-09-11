using Agentstration.ResourceManagement;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class FlowToolStepTests
{
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
        public Task<bool> ExistsAsync(string resourceId, CancellationToken cancellationToken) => Task.FromResult(true);

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
    }
}
