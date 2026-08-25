using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Agentstration.Runtime.AgentFramework;

#pragma warning disable MAAIW001
internal sealed class AgentFrameworkCheckpointStore(
    IRuntimeExecutionStateStore states,
    WorkspaceId workspaceId,
    TimeProvider timeProvider) : ICheckpointStore<JsonElement>
{
    public const string RuntimeType = "microsoft-agent-framework";

    public async ValueTask<CheckpointInfo> CreateCheckpointAsync(
        string sessionId,
        JsonElement value,
        CheckpointInfo? parent = null)
    {
        var checkpoint = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"));
        await states.StoreAsync(new RuntimeExecutionState(
            workspaceId,
            sessionId,
            RuntimeType,
            checkpoint.CheckpointId,
            value.Clone(),
            timeProvider.GetUtcNow(),
            parent?.CheckpointId), CancellationToken.None);
        return checkpoint;
    }

    public async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        var state = await states.GetAsync(workspaceId, sessionId, RuntimeType, key.CheckpointId, CancellationToken.None)
            ?? throw new KeyNotFoundException($"Runtime state '{sessionId}/{key.CheckpointId}' was not found.");
        return state.Payload.Clone();
    }

    public async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
    {
        var values = await states.ListAsync(workspaceId, sessionId, RuntimeType, withParent?.CheckpointId, CancellationToken.None);
        return values.Select(value => new CheckpointInfo(sessionId, value.StateId)).ToArray();
    }
}
#pragma warning restore MAAIW001
