using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;
using Microsoft.Agents.AI;

namespace Agentstration.Runtime.AgentFramework;

public sealed class AgentFrameworkRuntimeFactory : IAgentRuntimeFactory
{
    public string Handler => "prompt-agent";

    public Task<IAgentRuntime> CreateAsync(ResolvedAgentDefinition definition, string revisionId, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chatClient = context.ChatClients.Resolve(definition.ModelProfileId);
        var tools = context.Tools.Resolve(definition.EffectiveToolIds).ToList();
        AIAgent agent = new ChatClientAgent(
            chatClient,
            instructions: definition.EffectiveInstructions,
            name: definition.AgentKey,
            description: definition.Description,
            tools: tools);
        return Task.FromResult<IAgentRuntime>(new AgentFrameworkRuntime(definition.AgentKey, revisionId, agent));
    }

    private sealed class AgentFrameworkRuntime(string agentId, string revisionId, AIAgent agent) : IAgentRuntime
    {
        public string AgentId { get; } = agentId;
        public string RevisionId { get; } = revisionId;

        public async Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Input);
            var response = await agent.RunAsync(request.Input, cancellationToken: cancellationToken);
            return new AgentExecutionResult(response.Text, request.SessionId);
        }
    }
}

public sealed class AgentFrameworkAgentRouter(IChatClientResolver chatClients) : IAgentRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AgentRouteResult> SelectAsync(AgentRouteRequest request, IReadOnlyCollection<RoutableAgent> candidates, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Input);
        if (candidates.Count == 0) throw new ArgumentException("At least one routing candidate is required.", nameof(candidates));
        var client = chatClients.Resolve("router-default");
        AIAgent router = new ChatClientAgent(
            client,
            instructions: "AGENTSTRATION_ROUTER: Select exactly one candidate for the request. Return only JSON with agentId, confidence, and reason. Never execute the selected agent.",
            name: "agentstration-router",
            description: "Selects one deployed specialized agent.");
        var payload = JsonSerializer.Serialize(new { request = request.Input, candidates }, JsonOptions);
        var response = await router.RunAsync(payload, cancellationToken: cancellationToken);
        var result = JsonSerializer.Deserialize<AgentRouteResult>(response.Text.Trim().Trim('`'), JsonOptions)
            ?? throw new InvalidOperationException("The routing agent returned an invalid result.");
        if (!candidates.Any(candidate => string.Equals(candidate.AgentId, result.AgentId, StringComparison.Ordinal)))
            throw new InvalidOperationException("The routing agent selected an unknown candidate.");
        return result;
    }
}
