using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Flows;
using Agentstration.Schemas;

namespace Agentstration.Runtime.Worker.MicrosoftAgentFramework;

internal sealed partial class AwpAssignmentExecutor
{
    private static InvocationDataContracts? Contracts(AwpRootFlowExecutionMaterial material) =>
        material.ContractContext?.Deserialize<InvocationDataContracts>(JsonOptions);

    private static ResolvedDataContract? AgentDeclaredOutput(AwpExecutionAgentMaterial agent,
        InvocationDataContracts? context) => DataContracts.Declared(agent.OutputSchema,
            $"agent:{agent.AgentNamespace}/{agent.AgentName}@{agent.Generation}/output",
            agent.OutputContract?.Deserialize<DataContractDefinition>(JsonOptions) is { Mode: DataContractMode.Fixed } definition ? definition.MediaType : null)
        ?? DataContracts.Resolve(agent.OutputContract?.Deserialize<DataContractDefinition>(JsonOptions),
            DataContracts.Declared(agent.InputSchema, $"agent:{agent.AgentName}@{agent.Generation}/input") ?? context?.Input, context?.ExpectedOutput, $"agent:{agent.AgentName}@{agent.Generation}/output");

    private static ResolvedDataContract? ExpectedAgentOutput(FlowGraphDefinition graph, AgentFlowStepDefinition step,
        AwpRootFlowExecutionMaterial material, AwpExecutionAgentMaterial? executable = null)
    {
        var effectiveAgent = executable ?? material.Agents.SingleOrDefault(value => value.ParticipantId == step.Name)
            ?? material.Agents.SingleOrDefault(value => value.AgentName == step.Agent.ResourceId);
        if (effectiveAgent is null) return null;
        return FlowDataContracts.AgentOutput(graph, step.Name, material.RunId,
            $"agent:{effectiveAgent.AgentNamespace}/{effectiveAgent.AgentName}@{effectiveAgent.Generation}/output",
            effectiveAgent.OutputSchema, effectiveAgent.OutputContract?.Deserialize<DataContractDefinition>(JsonOptions), Contracts(material), effectiveAgent.InputSchema);
    }

    private static JsonElement DefaultToolArguments(FlowStepDefinition step, JsonElement payload)
    {
        if (step is ToolFlowStepDefinition { Tool.InputSchema: { } schema }
            && schema.TryGetProperty("properties", out var properties))
        {
            var business = properties.EnumerateObject().Where(property => property.Name != "outputContract").ToArray();
            if (business.Length == 1) return JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { [business[0].Name] = payload });
        }
        return JsonSerializer.SerializeToElement(new { });
    }

    private static bool IsIdentity(JsonElement? mapping) => mapping is null || mapping is { ValueKind: JsonValueKind.String }
        && mapping.Value.GetString() == "${transition.output}";

    private static JsonElement AgentResultValue(AwpExecutionAgentMaterial agent, string text,
        ResolvedDataContract? expected, string runId, string stepName) => expected is null
        ? JsonSerializer.SerializeToElement(text)
        : DataContracts.ReadText(expected, text, "agent-output", runId, stepName);

    private static InvocationDataContracts? StepContracts(AwpRootFlowExecutionMaterial material, FlowGraphDefinition graph,
        FlowStepDefinition step, bool succeeded)
    {
        var context = Contracts(material);
        JsonElement? input = step switch
        {
            InputFlowStepDefinition => graph.ResolveInputSchema(),
            AgentFlowStepDefinition agent => agent.Agent.InputSchema,
            ToolFlowStepDefinition tool => tool.Tool.InputSchema,
            FlowCallStepDefinition flow => flow.Flow.InputSchema,
            _ => null
        };
        JsonElement? output = step switch
        {
            InputFlowStepDefinition => graph.ResolveInputSchema(),
            AgentFlowStepDefinition agent => agent.Agent.OutputSchema,
            ToolFlowStepDefinition tool => tool.Tool.OutputSchema,
            FlowCallStepDefinition flow => flow.Flow.OutputSchema,
            TransformFlowStepDefinition transform => transform.Schema,
            OutputFlowStepDefinition final => graph.ResolveOutputSchema(final, out _),
            _ => null
        };
        var expected = DataContracts.Declared(output, $"flow:{material.RunId}/step:{step.Name}/output");
        if (step is AgentFlowStepDefinition agentStep)
        {
            try { expected = ExpectedAgentOutput(graph, agentStep, material); }
            catch (DataContractException) { /* The structured failure records the unresolved declaration. */ }
        }
        var resolvedInput = DataContracts.Declared(input, $"flow:{material.RunId}/step:{step.Name}/input") ?? context?.Input;
        return new(resolvedInput,
            expected is null ? null : expected with { Validated = succeeded }, context?.ArtifactDraft ?? false, context?.FinalResultMapped ?? false);
    }

    private static IReadOnlyList<InvocationDataContracts> BoundedArtifactContracts(IReadOnlyList<InvocationDataContracts> contracts)
    {
        var result = new List<InvocationDataContracts>();
        var budget = 262144;
        foreach (var contract in contracts)
        {
            var length = JsonSerializer.Serialize(contract).Length;
            if (length > budget) break;
            result.Add(contract); budget -= length;
        }
        return result;
    }

    private static string? PreserveContractFailure(string? causal, string? selected)
    {
        if (DataContracts.ParseFailure(causal) is not { } failure) return selected;
        var details = JsonSerializer.Serialize(new { contractFailure = failure, details = selected }, JsonOptions);
        return details.Length <= 8192 ? details
            : JsonSerializer.Serialize(new { contractFailure = failure, details = "additional_details_omitted" }, JsonOptions);
    }

    private static string FailureDetails(Exception exception) => exception switch
    {
        AwpClientException { Details: { } details } => details,
        DataContractException contract => JsonSerializer.Serialize(contract.Failure, JsonOptions),
        AwpExecutionNotSupportedException { Details: { } details } => details,
        _ => string.Empty
    };
}
