using Agentstration.Flow;
using Agentstration.Resources;

namespace Agentstration.Flow.Storage.Abstractions;

public sealed record StoredFlow(FlowResource Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record StoredFlowVersion(FlowVersion Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record FlowPage(IReadOnlyList<StoredFlow> Items, bool HasMore);
public sealed record StoredFlowRun(FlowRun Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record StoredFlowDraft(FlowDraft Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record StoredInputRequest(InputRequest Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record FlowRunPage(IReadOnlyList<StoredFlowRun> Items, bool HasMore);

public interface IFlowRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredFlow> CreateAsync(FlowResource resource, CancellationToken cancellationToken);
    Task<StoredFlow?> GetAsync(WorkspaceId workspaceId, FlowId id, CancellationToken cancellationToken);
    Task<FlowPage> ListAsync(WorkspaceId workspaceId, int skip, int take, CancellationToken cancellationToken);
    Task<FlowPage> ListAsync(WorkspaceId workspaceId, ResourceNamespace @namespace, int skip, int take, CancellationToken cancellationToken);
    Task<StoredFlow> UpdateAsync(FlowResource resource, string expectedETag, CancellationToken cancellationToken);
    Task DeleteAsync(WorkspaceId workspaceId, FlowId id, string? expectedETag, CancellationToken cancellationToken);
    Task<StoredFlowVersion> CreateVersionAsync(FlowVersion version, CancellationToken cancellationToken);
    Task<StoredFlowVersion?> GetVersionAsync(WorkspaceId workspaceId, FlowId id, string version, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredFlowVersion>> ListVersionsAsync(WorkspaceId workspaceId, FlowId id, CancellationToken cancellationToken);
    Task<StoredFlowRun> CreateRunAsync(FlowRun run, CancellationToken cancellationToken);
    /// <summary>
    /// Retrieves a Flow Run for trusted system and runtime processing. User-facing callers must use the
    /// <see cref="GetRunAsync(FlowRunScope, string, CancellationToken)"/> overload.
    /// </summary>
    Task<StoredFlowRun?> GetRunAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken);
    Task<StoredFlowRun?> GetRunAsync(FlowRunScope scope, string runId, CancellationToken cancellationToken);
    Task<FlowRunPage> ListRunsAsync(FlowRunScope scope, FlowId? flowId, FlowRunStatus? status, int skip, int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowRunKey>> ListRunKeysAsync(int skip, int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowRunKey>> ListRecoverableRunsAsync(int skip, int take, CancellationToken cancellationToken);
    Task<StoredFlowRun> UpdateRunAsync(FlowRun run, string expectedETag, CancellationToken cancellationToken);
    Task DeleteRunAsync(WorkspaceId workspaceId, string runId, string expectedETag, CancellationToken cancellationToken);
    Task<StoredFlowDraft> CreateDraftAsync(FlowDraft draft, CancellationToken cancellationToken);
    Task<StoredFlowDraft?> GetDraftAsync(WorkspaceId workspaceId, FlowId flowId, CancellationToken cancellationToken);
    Task<StoredFlowDraft> UpdateDraftAsync(FlowDraft draft, string expectedETag, CancellationToken cancellationToken);
    Task<FlowRunEvent> AppendRunEventAsync(FlowRunEvent runEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowRunEvent>> ListRunEventsAsync(WorkspaceId workspaceId, string runId, long afterSequence, CancellationToken cancellationToken);
    Task<StoredInputRequest> CreateInputRequestAsync(InputRequest request, CancellationToken cancellationToken);
    Task<StoredInputRequest?> GetInputRequestAsync(WorkspaceId workspaceId, string runId, string requestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredInputRequest>> ListInputRequestsAsync(WorkspaceId workspaceId, string runId, InputRequestStatus? status, CancellationToken cancellationToken);
    Task<StoredInputRequest> UpdateInputRequestAsync(InputRequest request, string expectedETag, CancellationToken cancellationToken);
}

public sealed class FlowConcurrencyException(string message) : Exception(message);
public sealed class FlowNotFoundException(FlowId id) : KeyNotFoundException($"Flow '{id}' was not found.");
public sealed class FlowRunNotFoundException(string id) : KeyNotFoundException($"Flow run '{id}' was not found.");
public sealed class FlowRunNotTerminalException(string id, FlowRunStatus status)
    : Exception($"Flow run '{id}' is '{status}' and cannot be deleted until it reaches a terminal status.");
