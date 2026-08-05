using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Infrastructure.Flows;

public sealed class LocalFlowRunQueue : IFlowRunQueue
{
    private readonly Channel<string> channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(string runId, CancellationToken cancellationToken) => channel.Writer.WriteAsync(runId, cancellationToken);

    public async IAsyncEnumerable<string> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var runId in channel.Reader.ReadAllAsync(cancellationToken)) yield return runId;
    }
}

public sealed class LocalFlowRunCancellationRegistry : IFlowRunCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> sources = new(StringComparer.Ordinal);

    public CancellationToken Register(string runId, CancellationToken stoppingToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!sources.TryAdd(runId, source))
        {
            source.Dispose();
            return sources[runId].Token;
        }
        return source.Token;
    }

    public bool Cancel(string runId) => sources.TryGetValue(runId, out var source) && TryCancel(source);

    public void Complete(string runId)
    {
        if (sources.TryRemove(runId, out var source)) source.Dispose();
    }

    private static bool TryCancel(CancellationTokenSource source)
    {
        try { source.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }
}

public sealed class ManagedFlowAgentExecutor(AgentManagementService management, IControlPlaneStore store) : IFlowAgentExecutor
{
    public async Task<FlowAgentExecutionResult> ExecuteAsync(FlowTargetReference target, JsonElement input, string correlationId, CancellationToken cancellationToken)
    {
        if (target.Kind != FlowTargetKind.Agent)
            throw new FlowValidationException("flow_target_kind_unsupported", "Flow Runs currently execute explicit Agent targets.");

        var prompt = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("prompt", out var promptProperty) && promptProperty.ValueKind == JsonValueKind.String
            ? promptProperty.GetString()!
            : input.GetRawText();
        var selected = await management.SelectAgentAsync(prompt, ResourceName(target.Id), cancellationToken);
        var deployment = await store.GetAsync<AgentDeployment>(selected.DeploymentId, cancellationToken)
            ?? throw new InvalidOperationException("The selected agent deployment no longer exists.");
        var revision = await store.GetAsync<AgentRevision>(deployment.Value.RevisionId, cancellationToken)
            ?? throw new InvalidOperationException("The selected agent revision no longer exists.");
        var result = await management.ExecuteSelectedAsync(selected, prompt, cancellationToken);
        return new FlowAgentExecutionResult(
            JsonSerializer.SerializeToElement(result.Output),
            revision.Value.AgentResourceId,
            revision.Value.AgentVersion,
            deployment.Value.ModelProfileId ?? revision.Value.Definition.ModelProfileId,
            result.ProviderType,
            result.Usage is null ? null : new FlowStepRunUsage(result.Usage.InputTokens, result.Usage.OutputTokens),
            revision.Value.Definition.EffectiveToolIds.ToArray(),
            [$"Runtime deployment {deployment.Value.Id} executed for correlation {correlationId}.", $"Model: {result.ModelName ?? "unspecified"}."]);
    }

    private static string ResourceName(string id)
    {
        var slash = id.LastIndexOf('/');
        return slash < 0 ? id : id[(slash + 1)..];
    }
}

public sealed class ManagementFlowResourceReferenceResolver(IControlPlaneStore store) : IFlowResourceReferenceResolver
{
    public async Task<bool> ExistsAsync(string resourceId, CancellationToken cancellationToken) =>
        await store.GetAsync<AgentResource>(resourceId, cancellationToken) is not null;
}
