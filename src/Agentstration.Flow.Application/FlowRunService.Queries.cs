using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flow.Application;

public sealed partial class FlowRunService
{
    public Task<StoredFlowRun?> GetAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken) => repository.GetRunAsync(workspaceId, runId, cancellationToken);

    public async Task<StoredFlowRun?> GetAsync(string runId, FlowRunScope scope, CancellationToken cancellationToken)
    {
        var stored = await repository.GetRunAsync(scope.WorkspaceId, runId, cancellationToken);
        return stored is not null && HasScope(stored.Value, scope) ? stored : null;
    }

    public Task<FlowRunPage> ListAsync(FlowId? flowId, FlowRunStatus? status, int skip, int take, FlowRunScope scope, CancellationToken cancellationToken) =>
        repository.ListRunsAsync(scope.WorkspaceId, flowId, status, skip, take, cancellationToken);

    public async Task<IReadOnlyList<StoredInputRequest>> ListInputsAsync(
        string runId,
        InputRequestStatus? status,
        FlowRunScope scope,
        CancellationToken cancellationToken)
    {
        _ = await RequiredAsync(scope.WorkspaceId, runId, cancellationToken);
        return await repository.ListInputRequestsAsync(scope.WorkspaceId, runId, status, cancellationToken);
    }

    public async Task<StoredInputRequest?> GetInputAsync(
        string runId,
        string requestId,
        FlowRunScope scope,
        CancellationToken cancellationToken)
    {
        _ = await RequiredAsync(scope.WorkspaceId, runId, cancellationToken);
        return await repository.GetInputRequestAsync(scope.WorkspaceId, runId, requestId, cancellationToken);
    }

    public async IAsyncEnumerable<FlowRun> ObserveAsync(string runId, FlowRunScope scope, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? etag = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var stored = await RequiredAsync(scope.WorkspaceId, runId, cancellationToken);
            if (!string.Equals(etag, stored.ETag, StringComparison.Ordinal))
            {
                etag = stored.ETag;
                yield return stored.Value;
            }
            if (stored.Value.Status.IsTerminal()) yield break;
            await Task.Delay(TimeSpan.FromMilliseconds(300), timeProvider, cancellationToken);
        }
    }

    public Task<IReadOnlyList<FlowRunEvent>> ListEventsAsync(FlowRunScope scope, string runId, long afterSequence, CancellationToken cancellationToken) =>
        repository.ListRunEventsAsync(scope.WorkspaceId, runId, afterSequence, cancellationToken);

    private async Task<StoredFlowRun> RequiredAsync(WorkspaceId workspaceId, string id, CancellationToken token) => await repository.GetRunAsync(workspaceId, id, token) ?? throw new FlowRunNotFoundException(id);

    private async Task<StoredFlowRun> RequiredAsync(string id, FlowRunScope scope, CancellationToken token) =>
        await GetAsync(id, scope, token) ?? throw new FlowRunNotFoundException(id);

    private static bool HasScope(FlowRun run, FlowRunScope scope) => run.Scope == scope;
}

