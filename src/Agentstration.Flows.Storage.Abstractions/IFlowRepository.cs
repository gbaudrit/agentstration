using Agentstration.Flows;
using Agentstration.Resources;

namespace Agentstration.Flows.Storage.Abstractions;

public sealed record StoredFlow(FlowResource Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record StoredFlowVersion(FlowVersion Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record FlowVersionPage(IReadOnlyList<StoredFlowVersion> Items, bool HasMore);
public sealed record FlowPage(IReadOnlyList<StoredFlow> Items, bool HasMore);
public sealed record StoredFlowRun(FlowRun Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record StoredFlowDraft(FlowDraft Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record StoredInputRequest(InputRequest Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record FlowRunPage(IReadOnlyList<StoredFlowRun> Items, bool HasMore);

public sealed record FlowRunIdentityQuery(FlowRunScope Invocation, ResourceScopeRef DefinitionOwner, ResourceNamespace Namespace,
    Guid ResourceUid, string? Version, DateTimeOffset WindowStart, int Take = 100, string? DefinitionHash = null);

public sealed record FlowRunAgentBindingQuery(FlowRunScope Invocation, ResourceScopeRef AgentOwner, ResourceNamespace AgentNamespace,
    Guid AgentUid, Guid? RevisionUid, long? Generation, string? DefinitionHash, DateTimeOffset WindowStart, int Take = 100);

public interface IFlowRepository
{
    Task<FlowRunPage> ListRunsByAgentBindingAsync(FlowRunAgentBindingQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The store does not support attested Agent bindings.");
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredFlow> CreateAsync(FlowResource resource, CancellationToken cancellationToken);
    Task<StoredFlow?> GetByUidAsync(Guid uid, CancellationToken cancellationToken);
    Task<StoredFlow?> GetExactAsync(ResourceScopeRef scopeRef, FlowId id, CancellationToken cancellationToken);
    Task<FlowPage> ListExactAsync(ResourceScopeRef scopeRef, int skip, int take, CancellationToken cancellationToken);
    Task<FlowPage> ListExactAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, int skip, int take, CancellationToken cancellationToken);
    Task<StoredFlow> UpdateAsync(FlowResource resource, string expectedETag, CancellationToken cancellationToken);
    Task DeleteExactAsync(ResourceScopeRef scopeRef, FlowId id, string? expectedETag, CancellationToken cancellationToken);
    Task<StoredFlowVersion> CreateVersionAsync(FlowVersion version, CancellationToken cancellationToken);
    Task<StoredFlowVersion?> GetVersionExactAsync(ResourceScopeRef scopeRef, FlowId id, string version, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredFlowVersion>> ListVersionsExactAsync(ResourceScopeRef scopeRef, FlowId id, CancellationToken cancellationToken);
    async Task<FlowVersionPage> ListVersionsPageExactAsync(ResourceScopeRef scopeRef, FlowId id,
        int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 200);
        var values = (await ListVersionsExactAsync(scopeRef, id, cancellationToken))
            .OrderBy(value => value.Value.Version, StringComparer.Ordinal).Skip(skip).Take(take + 1).ToArray();
        return new(values.Take(take).ToArray(), values.Length > take);
    }


    Task<StoredFlow?> GetAsync(WorkspaceId workspaceId, FlowId id, CancellationToken cancellationToken) =>
        GetExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, cancellationToken);
    Task<FlowPage> ListAsync(WorkspaceId workspaceId, int skip, int take, CancellationToken cancellationToken) =>
        ListExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), skip, take, cancellationToken);
    Task<FlowPage> ListAsync(WorkspaceId workspaceId, ResourceNamespace @namespace, int skip, int take, CancellationToken cancellationToken) =>
        ListExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), @namespace, skip, take, cancellationToken);
    Task DeleteAsync(WorkspaceId workspaceId, FlowId id, string? expectedETag, CancellationToken cancellationToken) =>
        DeleteExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, expectedETag, cancellationToken);
    Task<StoredFlowVersion?> GetVersionAsync(WorkspaceId workspaceId, FlowId id, string version, CancellationToken cancellationToken) =>
        GetVersionExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, version, cancellationToken);
    Task<IReadOnlyList<StoredFlowVersion>> ListVersionsAsync(WorkspaceId workspaceId, FlowId id, CancellationToken cancellationToken) =>
        ListVersionsExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, cancellationToken);
    Task<StoredFlowRun> CreateRunAsync(FlowRun run, CancellationToken cancellationToken);
    /// <summary>
    /// Retrieves a Flow Run for trusted system and runtime processing. User-facing callers must use the
    /// <see cref="GetRunAsync(FlowRunScope, string, CancellationToken)"/> overload.
    /// </summary>
    Task<StoredFlowRun?> GetRunExactAsync(ResourceScopeRef executionScopeRef, string runId, CancellationToken cancellationToken);
    Task<StoredFlowRun?> GetRunAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken) =>
        GetRunExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), runId, cancellationToken);
    Task<StoredFlowRun?> GetRunAsync(FlowRunScope scope, string runId, CancellationToken cancellationToken);
    Task<FlowRunPage> ListRunsAsync(FlowRunScope scope, FlowId? flowId, FlowRunStatus? status, int skip, int take, CancellationToken cancellationToken);
    Task<FlowRunPage> ListRunsByIdentityAsync(FlowRunIdentityQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Exact Flow execution identity projection is unavailable.");
    Task<IReadOnlyList<FlowRunKey>> ListRunKeysAsync(int skip, int take, CancellationToken cancellationToken);
    /// <summary>
    /// Lists recoverable runs after an exclusive, immutable run key in storage order. Pass null for
    /// the first page and the last returned key for the next page. Status and update-time changes
    /// must not move unvisited runs behind the cursor. Intended for trusted system processing.
    /// </summary>
    Task<IReadOnlyList<FlowRunKey>> ListRecoverableRunsAsync(FlowRunKey? after, int take, CancellationToken cancellationToken);
    Task<StoredFlowRun> UpdateRunAsync(FlowRun run, string expectedETag, CancellationToken cancellationToken);
    Task DeleteRunExactAsync(ResourceScopeRef executionScopeRef, string runId, string expectedETag, CancellationToken cancellationToken);
    Task DeleteRunAsync(WorkspaceId workspaceId, string runId, string expectedETag, CancellationToken cancellationToken) =>
        DeleteRunExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), runId, expectedETag, cancellationToken);
    Task<StoredFlowDraft> CreateDraftAsync(FlowDraft draft, CancellationToken cancellationToken);
    Task<StoredFlowDraft?> GetDraftExactAsync(WorkspaceId workspaceId, ResourceScopeRef ownerScopeRef, FlowId flowId, CancellationToken cancellationToken);
    Task<StoredFlowDraft?> GetDraftAsync(WorkspaceId workspaceId, FlowId flowId, CancellationToken cancellationToken) =>
        GetDraftExactAsync(workspaceId, ResourceScopeRef.Workspace(workspaceId.Value), flowId, cancellationToken);
    Task<StoredFlowDraft> UpdateDraftAsync(FlowDraft draft, string expectedETag, CancellationToken cancellationToken);
    Task<FlowRunEvent> AppendRunEventAsync(FlowRunEvent runEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowRunEvent>> ListRunEventsExactAsync(ResourceScopeRef executionScopeRef, string runId, long afterSequence, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowRunEvent>> ListRunEventsAsync(WorkspaceId workspaceId, string runId, long afterSequence, CancellationToken cancellationToken) =>
        ListRunEventsExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), runId, afterSequence, cancellationToken);
    Task<StoredInputRequest> CreateInputRequestAsync(InputRequest request, CancellationToken cancellationToken);
    Task<StoredInputRequest?> GetInputRequestExactAsync(ResourceScopeRef executionScopeRef, string runId, string requestId, CancellationToken cancellationToken);
    Task<StoredInputRequest?> GetInputRequestAsync(WorkspaceId workspaceId, string runId, string requestId, CancellationToken cancellationToken) =>
        GetInputRequestExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), runId, requestId, cancellationToken);
    Task<IReadOnlyList<StoredInputRequest>> ListInputRequestsExactAsync(ResourceScopeRef executionScopeRef, string runId, InputRequestStatus? status, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredInputRequest>> ListInputRequestsAsync(WorkspaceId workspaceId, string runId, InputRequestStatus? status, CancellationToken cancellationToken) =>
        ListInputRequestsExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), runId, status, cancellationToken);
    Task<StoredInputRequest> UpdateInputRequestAsync(InputRequest request, string expectedETag, CancellationToken cancellationToken);
}

public sealed class FlowConcurrencyException(string message) : Exception(message);
public sealed class FlowNotFoundException(FlowId id) : KeyNotFoundException($"Flow '{id}' was not found.");
public sealed class FlowRunNotFoundException(string id) : KeyNotFoundException($"Flow run '{id}' was not found.");
public sealed class FlowRunNotTerminalException(string id, FlowRunStatus status)
    : Exception($"Flow run '{id}' is '{status}' and cannot be deleted until it reaches a terminal status.");
