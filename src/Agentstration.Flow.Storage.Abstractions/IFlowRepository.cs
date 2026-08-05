using Agentstration.Flow;

namespace Agentstration.Flow.Storage.Abstractions;

public sealed record StoredFlow(FlowDefinition Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record StoredFlowVersion(FlowVersion Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record FlowPage(IReadOnlyList<StoredFlow> Items, bool HasMore);
public sealed record StoredFlowRun(FlowRun Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record StoredFlowDraft(FlowDraft Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record FlowRunPage(IReadOnlyList<StoredFlowRun> Items, bool HasMore);

public interface IFlowRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredFlow> CreateAsync(FlowDefinition definition, CancellationToken cancellationToken);
    Task<StoredFlow?> GetAsync(FlowId id, CancellationToken cancellationToken);
    Task<FlowPage> ListAsync(int skip, int take, CancellationToken cancellationToken);
    Task<StoredFlow> UpdateAsync(FlowDefinition definition, string expectedETag, CancellationToken cancellationToken);
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
