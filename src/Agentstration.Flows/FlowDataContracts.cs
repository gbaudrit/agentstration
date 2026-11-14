using System.Text.Json;
using Agentstration.Schemas;

namespace Agentstration.Flows;

/// <summary>Contract inference for an unchanged Agent result. The authority and Worker use the same rules.</summary>
public static class FlowDataContracts
{
    public static ResolvedDataContract? AgentOutput(FlowGraphDefinition? graph, string stepName, string runId,
        string agentOrigin, JsonElement? declaredSchema, DataContractDefinition? declaration, InvocationDataContracts? context, JsonElement? inputSchema = null)
    {
        try
        {
            var declared = DataContracts.Declared(declaredSchema, agentOrigin, declaration is { Mode: DataContractMode.Fixed } ? declaration.MediaType : null)
                ?? DataContracts.Resolve(declaration, DataContracts.Declared(inputSchema ?? AgentInput(graph, stepName), agentOrigin + "/input") ?? context?.Input, context?.ExpectedOutput, agentOrigin);
            if (declared is not null) return declared;
            if (graph is null) return context is { ArtifactDraft: false, FinalResultMapped: false } ? context.ExpectedOutput : null;
            var edges = graph.Transitions.Where(edge => edge.FromStep == stepName && edge.Event == "success").ToArray();
            if (edges.Length != 1 || edges[0].Condition is not null) return null;
            var next = graph.Steps.First(step => step.Name == edges[0].ToStep);
            var schema = next switch
            {
                AgentFlowStepDefinition agent when IsIdentity(agent.InputMapping, stepName) => agent.Agent.InputSchema,
                ToolFlowStepDefinition tool when tool.ArgumentsMapping is null => SingleToolParameter(tool.Tool.InputSchema),
                ToolFlowStepDefinition tool when IsIdentity(tool.ArgumentsMapping, stepName) => tool.Tool.InputSchema,
                FlowCallStepDefinition flow when IsIdentity(flow.InputMapping, stepName) => flow.Flow.InputSchema,
                OutputFlowStepDefinition output when IsIdentity(output.OutputMapping, stepName) => graph.ResolveOutputSchema(output, out _),
                _ => null
            };
            if (schema is not null) return DataContracts.Declared(schema, $"flow:{runId}/step:{next.Name}/input");
            return next is OutputFlowStepDefinition outputStep && IsIdentity(outputStep.OutputMapping, stepName)
                && context is { ArtifactDraft: false, FinalResultMapped: false } ? context.ExpectedOutput : null;
        }
        catch (DataContractException exception) when (exception.Failure.FlowRunId is null)
        { throw new DataContractException(exception.Code, exception.Failure with { FlowRunId = runId, StepName = stepName }); }
    }

    /// <summary>Keep the parent goal as context, without imposing a final shape on an intermediate child.</summary>
    public static InvocationDataContracts? ChildContext(FlowGraphDefinition? graph, string stepName, InvocationDataContracts? parent)
    {
        if (parent is null) return null;
        var success = graph?.Transitions.Where(edge => edge.FromStep == stepName && edge.Event is not "error" and not "failed").ToArray() ?? [];
        var forwardsFinal = success.Length == 1 && success[0].Condition is null
            && graph!.Steps.FirstOrDefault(step => step.Name == success[0].ToStep) is OutputFlowStepDefinition { Outcome: not FlowOutputOutcome.Error } output
            && IsIdentity(output.OutputMapping, stepName);
        return parent with { FinalResultMapped = parent.FinalResultMapped || !forwardsFinal };
    }

    public static string? OutputMediaType(FlowGraphDefinition graph)
    {
        var outputs = graph.Steps.OfType<OutputFlowStepDefinition>().Where(output => output.Outcome is not FlowOutputOutcome.Error).ToArray();
        var types = new List<string?>();
        foreach (var output in outputs)
        {
            var incoming = graph.Transitions.Where(edge => edge.ToStep == output.Name).ToArray();
            if (incoming.Length != 1 || !IsIdentity(output.OutputMapping, incoming[0].FromStep)) return null;
            types.Add(graph.Steps.First(step => step.Name == incoming[0].FromStep) switch
            {
                AgentFlowStepDefinition agent when agent.Agent.OutputContract is { Mode: DataContractMode.Fixed } contract => contract.MediaType,
                FlowCallStepDefinition flow => flow.Flow.OutputMediaType,
                _ => null
            });
        }
        return types.Count > 0 && types.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? types[0] : null;
    }

    public static string AgentInstructions(string instructions, JsonElement? inputSchema, ResolvedDataContract? response,
        InvocationDataContracts? context)
    {
        if (inputSchema is null && context?.Input is { } input)
            instructions += "\n\nInput data contract inherited for this invocation (metadata, not a business parameter). In Artifact processing this describes content, separately from the invocation envelope and access handles:\n" + JsonSerializer.Serialize(input);
        if (response is not null) instructions += DataContracts.Instruction(response);
        if (context?.ExpectedOutput is { } goal && (context.ArtifactDraft || context.FinalResultMapped || goal.Digest != response?.Digest))
            instructions += context.ArtifactDraft ? DataContracts.Instruction(goal, true)
                : "\n\nThe final processing result after the configured processing and result mapping must satisfy this contract. An intermediate result may differ: " + JsonSerializer.Serialize(goal);
        return instructions;
    }

    private static JsonElement? AgentInput(FlowGraphDefinition? graph, string stepName)
    {
        if (graph is null || graph.Steps.FirstOrDefault(step => step.Name == stepName) is not AgentFlowStepDefinition agent
            || !IsIdentity(agent.InputMapping, graph.Transitions.FirstOrDefault(edge => edge.ToStep == stepName)?.FromStep ?? string.Empty)) return null;
        var incoming = graph.Transitions.Where(edge => edge.ToStep == stepName).Select(edge => graph.Steps.First(step => step.Name == edge.FromStep)).ToArray();
        if (incoming.Length == 0) return graph.EntryStep == stepName ? graph.ResolveInputSchema() : null;
        var schemas = incoming.Select(step => step switch
        {
            InputFlowStepDefinition => graph.ResolveInputSchema(), AgentFlowStepDefinition prior => prior.Agent.OutputSchema,
            ToolFlowStepDefinition tool => tool.Tool.OutputSchema, FlowCallStepDefinition flow => flow.Flow.OutputSchema,
            TransformFlowStepDefinition transform => transform.Schema, _ => null
        }).ToArray();
        return schemas.All(schema => schema is { } value && schemas[0] is { } first && JsonElement.DeepEquals(first, value)) ? schemas[0] : null;
    }

    private static JsonElement? SingleToolParameter(JsonElement? inputSchema)
    {
        if (inputSchema is not { ValueKind: JsonValueKind.Object } schema || !schema.TryGetProperty("properties", out var properties)) return null;
        var business = properties.EnumerateObject().Where(property => property.Name != "outputContract").ToArray();
        return business.Length == 1 ? business[0].Value : null;
    }
    private static bool IsIdentity(JsonElement? mapping, string previous) => mapping is null
        || mapping is { ValueKind: JsonValueKind.String } && (mapping.Value.GetString() == "${transition.output}" || mapping.Value.GetString() == "${steps." + previous + ".output}");
}
