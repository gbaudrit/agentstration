using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Flows;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.AI;

namespace Agentstration.Runtime.Worker.MicrosoftAgentFramework;

internal sealed class AwpFlowExecutionCoordinator(
    AwpAssignmentSession session,
    AwpRootFlowExecutionMaterial material)
{
    private readonly ConcurrentDictionary<string, Task<AwpFlowStepLocation>> steps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AwpAgentTurnLocation> turnsByParticipant = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AwpAgentTurnLocation> turnsByCall = new(StringComparer.Ordinal);

    public AwpExecutionAgentMaterial Agent(string participantId) => material.Agents.Single(value =>
        string.Equals(value.ParticipantId, participantId, StringComparison.Ordinal));

    public Task<AwpFlowStepLocation> StepAsync(string participantId, CancellationToken cancellationToken) =>
        steps.GetOrAdd(participantId, _ => OpenStepAsync(participantId, cancellationToken));

    public async Task<AwpAgentTurnLocation> OpenTurnAsync(string participantId, CancellationToken cancellationToken)
    {
        var step = await StepAsync(participantId, cancellationToken);
        var turn = (await session.Client.OpenTurnAsync(new(session.Context, material.RunId, step, participantId),
            Guid.NewGuid(), cancellationToken)).Turn;
        turnsByParticipant[participantId] = turn;
        await session.AppendEventAsync(AwpExecutionEventKind.TurnStarted,
            new(material.RunId, step, turn), JsonSerializer.SerializeToElement(new { participantId }), null,
            cancellationToken);
        return turn;
    }

    public void CaptureCalls(string participantId, AwpAgentTurnLocation turn, ChatResponse response)
    {
        turnsByParticipant[participantId] = turn;
        foreach (var call in response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>())
            turnsByCall[call.CallId] = turn;
    }

    public AwpAgentTurnLocation Turn(string participantId, string callId) =>
        turnsByCall.TryGetValue(callId, out var turn) ? turn
        : turnsByParticipant.TryGetValue(participantId, out turn) ? turn
        : throw new InvalidOperationException($"Participant '{participantId}' has no active AWP Turn.");

    private async Task<AwpFlowStepLocation> OpenStepAsync(string participantId, CancellationToken cancellationToken)
    {
        var response = await session.Client.OpenStepAsync(new(session.Context, material.RunId, material.FlowVersion,
            material.FlowDefinitionHash, participantId), Guid.NewGuid(), cancellationToken);
        await session.AppendEventAsync(AwpExecutionEventKind.StepStarted,
            new(material.RunId, response.Step), JsonSerializer.SerializeToElement(new { participantId }), null,
            cancellationToken);
        return response.Step;
    }
}

internal sealed class AwpFlowChatClientResolver(
    AwpAssignmentSession session,
    AwpRootFlowExecutionMaterial material,
    AwpFlowExecutionCoordinator coordinator) : IChatClientResolver
{
    private readonly string runId = material.RunId;
    public ValueTask<IChatClient> ResolveAsync(string modelProfileName, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IChatClient>(Create(modelProfileName));

    public ValueTask<IChatClient> ResolveAsync(ResourceNamespace @namespace, string modelProfileName,
        CancellationToken cancellationToken = default) => ValueTask.FromResult<IChatClient>(Create(modelProfileName));

    private IChatClient Create(string profile)
    {
        const string prefix = "awp-participant:";
        if (!profile.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Worker-local model profile '{profile}' is invalid.");
        var participantId = profile[prefix.Length..];
        return new CoordinatedChatClient(session, coordinator, coordinator.Agent(participantId), runId);
    }

    private sealed class CoordinatedChatClient(
        AwpAssignmentSession session,
        AwpFlowExecutionCoordinator coordinator,
        AwpExecutionAgentMaterial agent,
        string runId) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var turn = await coordinator.OpenTurnAsync(agent.ParticipantId, cancellationToken);
            using var client = new AwpModelChatClient(session, agent, turn);
            var response = await client.GetResponseAsync(messages, options, cancellationToken);
            coordinator.CaptureCalls(agent.ParticipantId, turn, response);
            var content = response.Text;
            if (!string.IsNullOrEmpty(content))
                await session.AppendEventAsync(AwpExecutionEventKind.ResponseDelta,
                    new(runId, await coordinator.StepAsync(agent.ParticipantId, cancellationToken), turn),
                    JsonSerializer.SerializeToElement(new { participantId = agent.ParticipantId, content }), null,
                    cancellationToken);
            await session.AppendEventAsync(AwpExecutionEventKind.TurnCompleted,
                new(runId, await coordinator.StepAsync(agent.ParticipantId, cancellationToken), turn),
                JsonSerializer.SerializeToElement(new { participantId = agent.ParticipantId }), null,
                cancellationToken);
            return response;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}

internal sealed class AwpFlowAgentResolver(AwpRootFlowExecutionMaterial material) : IRuntimeAgentResolver
{
    public Task<ResolvedRuntimeAgent> ResolveAsync(RuntimeAgentReference reference, CancellationToken cancellationToken)
    {
        var agent = material.Agents.Single(value => value.AgentName == reference.ResourceId
            && value.Generation == reference.Version);
        return Task.FromResult(ToResolved(agent));
    }

    public Task<ResolvedRuntimeAgent> ResolveLatestAsync(string resourceId, CancellationToken cancellationToken) =>
        ResolveLatestAsync(resourceId, ResourceNamespace.Default, cancellationToken);

    public Task<ResolvedRuntimeAgent> ResolveLatestAsync(string resourceId, ResourceNamespace @namespace,
        CancellationToken cancellationToken)
    {
        var agent = material.Agents.Single(value => value.ParticipantId == resourceId);
        return Task.FromResult(ToResolved(agent));
    }

    private static ResolvedRuntimeAgent ToResolved(AwpExecutionAgentMaterial agent) => new(
        agent.AgentId, agent.AgentName, agent.Generation, $"awp:{agent.MaterialId}", agent.RevisionId,
        AwpRuntimeKinds.MicrosoftAgentFramework, $"awp-participant:{agent.ParticipantId}",
        new ExecutableAgentDefinition
        {
            AgentId = agent.AgentId,
            AgentKey = agent.ParticipantId,
            DisplayName = agent.DisplayName,
            Description = agent.Description,
            AgentVersion = agent.Generation,
            EffectiveInstructions = agent.Instructions,
            ModelProfileName = $"awp-participant:{agent.ParticipantId}",
            ModelProfileNamespace = ResourceNamespace.Default,
            RuntimeProfileName = AwpRuntimeKinds.MicrosoftAgentFramework,
            EffectiveToolNames = agent.Tools.Select(tool => tool.Id).ToArray(),
            MiddlewareIds = [], ContextProviderIds = [], Capabilities = [],
            Handler = agent.Handler,
            DefinitionHash = agent.DefinitionHash
        }, true, "Ready", null);
}

internal sealed class AwpFlowToolExecutionPipeline(
    AwpAssignmentSession session,
    AwpFlowExecutionCoordinator coordinator) : IToolExecutionPipeline
{
    public async ValueTask<JsonElement?> ExecuteAsync(ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var participantId = context.AgentId
            ?? throw new InvalidOperationException("The Flow tool call has no participant identity.");
        var turn = coordinator.Turn(participantId, context.ToolCallId);
        var response = await session.Client.InvokeToolAsync(new(session.Context, participantId,
            turn.TurnId, turn.Attempt.TurnAttemptId, new(StableGuid(context.ToolCallId)), context.ToolId,
            context.Arguments), cancellationToken);
        return response.Result?.Clone();
    }

    private static Guid StableGuid(string value)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty) return parsed;
        return new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))[..16]);
    }
}

internal sealed class AwpRuntimeExecutionStateStore(
    AwpAssignmentSession session,
    AwpRootFlowExecutionMaterial material) : IRuntimeExecutionStateStore
{
    private readonly ConcurrentDictionary<string, RuntimeExecutionState> known = new(StringComparer.Ordinal);

    public async Task StoreAsync(RuntimeExecutionState state, CancellationToken cancellationToken)
    {
        await session.Client.StoreCheckpointAsync(new(session.Context, state.StateId, "maf-json-v1",
            material.Digest, state.Payload), cancellationToken);
        known[state.StateId] = state;
    }

    public async Task<RuntimeExecutionState?> GetAsync(WorkspaceId workspaceId, string runId,
        string runtimeType, string stateId, CancellationToken cancellationToken)
    {
        if (known.TryGetValue(stateId, out var state)) return state;
        var checkpoint = await session.Client.GetCheckpointAsync(new(session.Context, stateId), cancellationToken);
        state = new(workspaceId, runId, runtimeType, stateId, checkpoint.Payload,
            checkpoint.PersistedAt);
        known[stateId] = state;
        return state;
    }

    public async Task<IReadOnlyList<RuntimeExecutionState>> ListAsync(WorkspaceId workspaceId, string runId,
        string runtimeType, string? parentStateId, CancellationToken cancellationToken)
    {
        if (material.Resume is { } resume && !known.ContainsKey(resume.StateId))
            _ = await GetAsync(workspaceId, runId, runtimeType, resume.StateId, cancellationToken);
        return known.Values.Where(value => value.WorkspaceId == workspaceId && value.RunId == runId
            && value.RuntimeType == runtimeType && value.ParentStateId == parentStateId).ToArray();
    }

    public Task DeleteAsync(WorkspaceId workspaceId, string runId, string? runtimeType,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class AwpWaitingForInputException : Exception;
