using Agentstration.Flows;
using System.Text.Json;

namespace Agentstration.Web.FlowDesigner.Components;

public partial class FlowDesigner
{
    private string? DescribeStepResource(string name) => Editor.State.Resource?.Definition.Steps.FirstOrDefault(step => step.Name == name) is AgentFlowStepDefinition agent
        ? ResolveAgent(agent.Agent)?.DisplayName ?? agent.Agent.ResourceId : null;
    private IReadOnlyList<FlowMappingOption> MappingOptions()
    {
        var result = new List<FlowMappingOption> { new("${transition.output}", T("MappingPrevious")), new("${input}", T("MappingFlowInput")) };
        if (SelectedStep is null || Editor.State.Resource?.Definition is not { } graph) return result;
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(graph.Transitions.Where(edge => edge.ToStep == SelectedStep.Name).Select(edge => edge.FromStep));
        while (queue.TryDequeue(out var name) && ancestors.Count < 128)
        {
            if (name == SelectedStep.Name || !ancestors.Add(name)) continue;
            foreach (var edge in graph.Transitions.Where(edge => edge.ToStep == name)) queue.Enqueue(edge.FromStep);
        }
        foreach (var owner in graph.Steps.OfType<ArtifactTransformFlowStepDefinition>().Where(owner => ancestors.Contains(owner.Name)))
        {
            try
            {
                if (!FlowArtifactProcessingGraph.Extract(graph, owner).Steps.Any(step => step.Name == SelectedStep.Name)) continue;
                if (owner.Mode == ArtifactProcessingMode.Content) result.Add(new("${input.content}", T("MappingArtifactContent")));
                result.Add(new("${input.artifact}", T("MappingArtifactMetadata")));
                result.Add(new("${input.inputAccess}", T("MappingArtifactInputAccess")));
                result.Add(new("${input.outputAccess}", T("MappingArtifactOutputAccess")));
                result.Add(new("${input.parameters}", T("MappingParameters")));
                // The processing region receives its own input and step results.
                ancestors.IntersectWith(FlowArtifactProcessingGraph.Extract(graph, owner).Steps.Select(step => step.Name));
                break;
            }
            catch (InvalidOperationException) { /* Incomplete draft: keep only proven suggestions. */ }
        }
        foreach (var step in graph.Steps.Where(step => ancestors.Contains(step.Name) && step is not InputFlowStepDefinition))
        {
            var label = step.DisplayName ?? step.Name;
            result.Add(new("${steps." + step.Name + ".output}", T("MappingStepOutput", label)));
            if (ResolveStepOutputSchema(step) is { ValueKind: JsonValueKind.Object } schema && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
                foreach (var property in properties.EnumerateObject().Take(16))
                    if (property.Name.All(character => char.IsLetterOrDigit(character) || character is '_' or '-'))
                        result.Add(new("${steps." + step.Name + ".output." + property.Name + "}", T("MappingStepProperty", label, property.Name)));
        }
        return result.DistinctBy(option => option.Expression).ToArray();
    }
}
