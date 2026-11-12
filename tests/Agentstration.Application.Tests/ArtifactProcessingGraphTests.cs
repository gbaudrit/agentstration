using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class ArtifactProcessingGraphTests
{
    private static readonly JsonSerializerOptions LegacyJson = new(JsonSerializerDefaults.Web);
    [TestMethod]
    public void AbsentParallelismPreservesLegacyGraphSerializationAndBoundaryIdentity()
    {
        var graph = Graph();
        var owner = graph.Steps.OfType<ArtifactTransformFlowStepDefinition>().Single();
        var json = JsonSerializer.Serialize<FlowStepDefinition>(owner, LegacyJson);
        Assert.DoesNotContain("maximumConcurrency", json);
        var body = FlowArtifactProcessingGraph.Extract(graph, owner);
        Assert.AreEqual("artifact-processing-start", body.Transitions[0].Id);
        var restored = JsonSerializer.Deserialize<FlowStepDefinition>(json, LegacyJson);
        Assert.AreEqual(1, restored!.Region()!.MaximumConcurrency);
    }

    [TestMethod]
    public async Task ProcessingConnectionsCanContainAnAgentToolMappingAndFlowWithoutASeparatePublication()
    {
        var graph = Graph();
        var result = await new FlowGraphValidator(new References()).ValidateAsync(graph, new(false), default);
        Assert.IsTrue(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        var step = graph.Steps.OfType<ArtifactTransformFlowStepDefinition>().Single();
        var body = FlowArtifactProcessingGraph.Extract(graph, step);
        CollectionAssert.AreEquivalent(new[] { "artifact_processing_input", "agent", "tool", "map", "child", "error", "artifact_processing_result" }, body.Steps.Select(value => value.Name).ToArray());
        Assert.AreEqual("artifact_processing_result", body.Transitions.Single(edge => edge.FromStep == "child" && edge.Event == "completed").ToStep);
        Assert.IsFalse(FlowArtifactProcessingGraph.ExecutionTransitions(graph).Any(edge => edge.Event == "process" || edge.FromStep == "child" && edge.ToStep == step.Name));
        var restored = JsonSerializer.Deserialize<FlowGraphDefinition>(JsonSerializer.Serialize(graph));
        Assert.AreEqual(FlowDefinitionHash.Compute(graph), FlowDefinitionHash.Compute(restored!));
    }

    [TestMethod]
    [DataRow("missing-return", "artifact_processing_connections_invalid")]
    [DataRow("missing-entry", "artifact_processing_connections_invalid")]
    [DataRow("cycle", "artifact_processing_cycle")]
    [DataRow("outside-entry", "artifact_processing_connections_invalid")]
    [DataRow("limits", "artifact_processing_limits_invalid")]
    public async Task InvalidProcessingRegionsCannotBePublished(string variant, string code)
    {
        var graph = Graph();
        graph = variant switch
        {
            "missing-return" => graph with { Transitions = graph.Transitions.Where(edge => edge.Id != "return").ToArray() },
            "missing-entry" => graph with { Transitions = graph.Transitions.Where(edge => edge.Event != "process").ToArray() },
            "cycle" => graph with { Transitions = [.. graph.Transitions, new("cycle", "map", "completed", "agent")] },
            "outside-entry" => graph with { Transitions = [.. graph.Transitions, new("outside", "input", "completed", "tool")] },
            _ => graph with { Steps = graph.Steps.Select(step => step is ArtifactTransformFlowStepDefinition processing ? processing with { MaximumArtifacts = 129 } : step).ToArray() }
        };
        var result = await new FlowGraphValidator(new References()).ValidateAsync(graph, new(false), default);
        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == code), string.Join("; ", result.Issues.Select(issue => issue.Code)));
    }

    private static FlowGraphDefinition Graph() => new()
    {
        EntryStep = "input",
        Steps = [new InputFlowStepDefinition { Name = "input" },
            new ArtifactTransformFlowStepDefinition { Name = "process", ArtifactsMapping = JsonSerializer.SerializeToElement("${input.artifacts}") },
            new AgentFlowStepDefinition { Name = "agent", Agent = new("anonymize") },
            new ToolFlowStepDefinition { Name = "tool", Tool = new("generic-tool") },
            new TransformFlowStepDefinition { Name = "map", Mapping = JsonSerializer.SerializeToElement("${transition.output}") },
            new FlowCallStepDefinition { Name = "child", Flow = new("verify") },
            new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success },
            new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }],
        Transitions = [new("start", "input", "completed", "process"), new("body", "process", "process", "agent"),
            new("agent-tool", "agent", "success", "tool"), new("agent-error", "agent", "error", "error"),
            new("tool-map", "tool", "success", "map"), new("tool-error", "tool", "error", "error"),
            new("map-child", "map", "completed", "child"), new("return", "child", "completed", "process"),
            new("done", "process", "completed", "completed"), new("failed", "process", "error", "error")]
    };
    private sealed class References : IFlowResourceReferenceResolver
    {
        public Task<bool> ExistsAsync(string resourceId, ResourceNamespace? ns, CancellationToken token) => Task.FromResult(true);
    }
}
