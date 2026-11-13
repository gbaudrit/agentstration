using System.Text.Json;
using Agentstration.Flows;
using Microsoft.AspNetCore.Components.Web;

namespace Agentstration.Web.FlowDesigner.Components;

public partial class FlowDesigner
{
    private sealed record PortInspection(string Step, string Port, string State, string Details);
    private PortInspection? portInspection;
    private static readonly JsonSerializerOptions PortJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string DescribePort(string name, string port)
    {
        var step = Editor.State.Resource?.Definition.Steps.FirstOrDefault(value => value.Name == name);
        var schema = PortSchema(step, port);
        var mode = PortMode(step, port, schema);
        var summary = schema?.GetRawText() ?? T("PortContractUnresolved");
        if (summary.Length > 320) summary = summary[..320] + "…";
        return T("PortContractSummary", port, T($"DataContractMode.{mode}"), summary);
    }
    private string PortMode(FlowStepDefinition? step, string port, JsonElement? schema)
    {
        if (port == "input" || port is "error" or "failed") return schema is null ? "Absent" : "Fixed";
        var definition = step switch
        {
            ArtifactTransformFlowStepDefinition artifact when port == "result" => artifact.OutputContract,
            AgentFlowStepDefinition agent when port == "success" => agent.Agent.OutputContract ?? (ReadOnly ? null : ResolveAgent(agent.Agent)?.OutputContract),
            _ => null
        };
        return definition?.Mode.ToString() ?? (schema is null ? "Absent" : "Fixed");
    }
    private JsonElement? PortSchema(FlowStepDefinition? step, string port)
    {
        if (step is TransformFlowStepDefinition { Processing: { } process })
            return port switch
            {
                "process" => JsonSerializer.SerializeToElement(new { type = process.Partition == FlowProcessPartition.TextChunks ? "string" : "array" }),
                "completed" => ResolveStepOutputSchema(step),
                _ => null
            };
        if (step is ArtifactTransformFlowStepDefinition artifact)
            return port switch
            {
                "input" => ArtifactTransformContracts.Input,
                "completed" => ArtifactTransformContracts.Manifest,
                "process" => MappedSchema(artifact.ProcessingInputMapping, ArtifactTransformContracts.ProcessingInput),
                "result" => artifact.OutputContract?.Schema,
                _ => null
            };
        if (port is "error" or "failed" || Editor.State.Diagram.Nodes.FirstOrDefault(node => node.Name == step?.Name)?.OutputOutcomes.GetValueOrDefault(port) == FlowOutputOutcome.Error) return null;
        return port == "input" ? step switch
    {
        InputFlowStepDefinition => EffectiveInputSchema,
        ArtifactTransformFlowStepDefinition => ArtifactTransformContracts.Input,
        AgentFlowStepDefinition agent => agent.Agent.InputSchema ?? (ReadOnly ? null : ResolveAgent(agent.Agent)?.InputSchema),
        ToolFlowStepDefinition tool => tool.Tool.InputSchema ?? (ReadOnly ? null : ResolveTool(tool.Tool)?.InputSchema),
        FlowCallStepDefinition call => call.Flow.InputSchema ?? FlowVersion(call)?.InputSchema,
        OutputFlowStepDefinition output => output.Schema,
        _ => null
    } : ResolveStepOutputSchema(step);
    }
    private async Task InspectPortAsync(string name, string port)
    {
        var step = Editor.State.Resource?.Definition.Steps.FirstOrDefault(value => value.Name == name);
        if (step is null) return;
        // Fetch exact nested version for this inspection without changing selection or the graph.
        JsonElement? schema;
        if (step is FlowCallStepDefinition call && !(ReadOnly && (port == "input" ? call.Flow.InputSchema is not null : call.Flow.OutputSchema is not null)))
        {
            var versions = await Resources.GetFlowVersionsAsync(ResolveFlowScope(call.Flow), call.Flow.Namespace ?? Namespace, call.Flow.ResourceId, cancellation.Token);
            var version = ResolveFlowVersion(call, versions);
            var named = version?.NamedOutputs.FirstOrDefault(output => output.Name == port);
            schema = port == "input" ? version?.InputSchema : named is not null ? named.Schema : port is "completed" or "success" or "output" ? version?.OutputSchema : null;
        }
        else schema = PortSchema(step, port);
        var artifact = step as ArtifactTransformFlowStepDefinition;
        var mode = PortMode(step, port, schema);
        portInspection = new(name, port, T($"DataContractMode.{mode}"), JsonSerializer.Serialize(new
        {
            source = ReadOnly ? "published-definition" : "draft-definition",
            reference = step switch { AgentFlowStepDefinition agent => JsonSerializer.SerializeToElement(agent.Agent),
                ToolFlowStepDefinition tool => JsonSerializer.SerializeToElement(tool.Tool), FlowCallStepDefinition flow => JsonSerializer.SerializeToElement(flow.Flow), _ => (JsonElement?)null },
            envelopeSchema = schema,
            artifactDataContract = artifact is not null && port != "input" ? artifact.OutputContract : null,
            artifactContent = artifact is null ? null : port is "input" or "process" ? T("ArtifactInputContentContract") : T("ArtifactOutputContentContract"),
            mediaType = artifact is not null && port is "result" or "completed" ? artifact.OutputMediaType : null,
            maximumArtifacts = artifact?.MaximumArtifacts,
            maximumContentBytes = artifact?.MaximumContentBytes,
            maximumOutputBytes = artifact?.MaximumOutputBytes,
            mapping = step switch { AgentFlowStepDefinition agent => agent.InputMapping, ToolFlowStepDefinition tool => tool.ArgumentsMapping,
                FlowCallStepDefinition flow => flow.InputMapping, TransformFlowStepDefinition transform => transform.Mapping,
                ArtifactTransformFlowStepDefinition transform => port == "input" ? transform.ArtifactsMapping : port == "process" ? transform.ProcessingInputMapping : transform.ResultMapping,
                OutputFlowStepDefinition output => output.OutputMapping, _ => null },
            origin = step switch { AgentFlowStepDefinition agent => agent.Agent.ResourceId, ToolFlowStepDefinition tool => tool.Tool.ResourceId,
                FlowCallStepDefinition flow => flow.Flow.ResourceId, _ => step.Name }
        }, PortJson));
        await InvokeAsync(StateHasChanged);
    }
    private void ClosePortInspection() => portInspection = null;
    private void PortInspectionKeyDown(KeyboardEventArgs args) { if (args.Key == "Escape") ClosePortInspection(); }
}
