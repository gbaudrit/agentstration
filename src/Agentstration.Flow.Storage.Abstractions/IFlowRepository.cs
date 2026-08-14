using Agentstration.Flow;
using Agentstration.Resources;

namespace Agentstration.Flow.Storage.Abstractions;

public sealed record StoredFlow(FlowResource Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record StoredFlowVersion(FlowVersion Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record FlowPage(IReadOnlyList<StoredFlow> Items, bool HasMore);
public sealed record StoredFlowRun(FlowRun Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record StoredFlowDraft(FlowDraft Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record FlowRunPage(IReadOnlyList<StoredFlowRun> Items, bool HasMore);

public interface IFlowRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredFlow> CreateAsync(FlowResource resource, CancellationToken cancellationToken);
    Task<StoredFlow?> GetAsync(FlowId id, CancellationToken cancellationToken);
    Task<FlowPage> ListAsync(int skip, int take, CancellationToken cancellationToken);
    async Task<FlowPage> ListAsync(ResourceNamespace @namespace, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        var matches = new List<StoredFlow>();
        var offset = 0;
        const int pageSize = 1000;
        while (matches.Count < skip + take)
        {
            var page = await ListAsync(offset, pageSize, cancellationToken);
            matches.AddRange(page.Items.Where(flow => flow.Value.Id.Namespace == @namespace));
            if (page.Items.Count < pageSize) break;
            offset += page.Items.Count;
        }
        return new FlowPage(matches.Skip(skip).Take(take).ToArray(), matches.Count > skip + take);
    }
    Task<StoredFlow> UpdateAsync(FlowResource resource, string expectedETag, CancellationToken cancellationToken);
    Task DeleteAsync(FlowId id, string? expectedETag, CancellationToken cancellationToken);
    Task<StoredFlowVersion> CreateVersionAsync(FlowVersion version, CancellationToken cancellationToken);
    Task<StoredFlowVersion?> GetVersionAsync(FlowId id, string version, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredFlowVersion>> ListVersionsAsync(FlowId id, CancellationToken cancellationToken);
    Task<StoredFlowRun> CreateRunAsync(FlowRun run, CancellationToken cancellationToken);
    Task<StoredFlowRun?> GetRunAsync(string runId, CancellationToken cancellationToken);
    Task<FlowRunPage> ListRunsAsync(FlowId? flowId, FlowRunStatus? status, int skip, int take, CancellationToken cancellationToken);
    Task<StoredFlowRun> UpdateRunAsync(FlowRun run, string expectedETag, CancellationToken cancellationToken);
    Task<StoredFlowDraft> CreateDraftAsync(FlowDraft draft, CancellationToken cancellationToken);
    Task<StoredFlowDraft?> GetDraftAsync(FlowId flowId, CancellationToken cancellationToken);
    Task<StoredFlowDraft> UpdateDraftAsync(FlowDraft draft, string expectedETag, CancellationToken cancellationToken);
    Task<FlowRunEvent> AppendRunEventAsync(FlowRunEvent runEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowRunEvent>> ListRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken);
}

public sealed class FlowConcurrencyException(string message) : Exception(message);
public sealed class FlowNotFoundException(FlowId id) : KeyNotFoundException($"Flow '{id}' was not found.");
public sealed class FlowRunNotFoundException(string id) : KeyNotFoundException($"Flow run '{id}' was not found.");
