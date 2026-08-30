using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components.Models;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Console;

public interface IManagementApiClient
{
    Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TriggerResource>> GetTriggersAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<AgentResource>> GetAgentAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? GetAgentAsync(name, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Agents.");
    Task<ResourceSnapshot<AgentResource>> PutAgentAsync(AgentResourceRequest request, string? etag, bool createOnly, CancellationToken cancellationToken);
    Task DeleteAgentAsync(string name, string etag, CancellationToken cancellationToken);
    Task<ManagementSummary> GetSummaryAsync(CancellationToken cancellationToken);
}

public interface IAgentRunnerManagementClient
{
    Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string name, CancellationToken cancellationToken);
}

public interface IRuntimeApiClient
{
    Task<IReadOnlyList<ExecutionSummary>> GetExecutionsAsync(CancellationToken cancellationToken);
    Task<RuntimeRun> CreateRunAsync(CreateRuntimeRunRequest request, CancellationToken cancellationToken);
    Task<RuntimeRun> GetRunAsync(string runId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RuntimeRun>> GetRunsAsync(string? agentResourceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RuntimeRunEvent>> GetRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken);
    IAsyncEnumerable<RuntimeRunEvent> ObserveRunAsync(string runId, long afterSequence, CancellationToken cancellationToken);
    Task<RuntimeRun> CancelRunAsync(string runId, CancellationToken cancellationToken);
    Task<RuntimeRun> RetryRunAsync(string runId, CancellationToken cancellationToken);
}

public interface IAgentRunnerRuntimeClient : IRuntimeApiClient
{
    Task<AgentRuntimeReadinessResponse> GetAgentReadinessAsync(string agentName, long generation, CancellationToken cancellationToken);
    Task<AgentRuntimeReadinessResponse> GetAgentReadinessAsync(ResourceNamespace @namespace, string agentName, long generation, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? GetAgentReadinessAsync(agentName, generation, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Agent runtime readiness.");
    Task<PrepareAgentRuntimeResponse> PrepareAgentAsync(string agentName, long generation, CancellationToken cancellationToken);
    Task<PrepareAgentRuntimeResponse> PrepareAgentAsync(ResourceNamespace @namespace, string agentName, long generation, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? PrepareAgentAsync(agentName, generation, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Agent runtime preparation.");
}

public sealed record ToolGovernanceAuditFilters
{
    public string? ToolCallId { get; init; }
    public string? InvocationId { get; init; }
    public string? ToolId { get; init; }
    public string? HookId { get; init; }
    public long? ResourceGeneration { get; init; }
    public ToolExecutionHookEvaluationKind? Decision { get; init; }
}

public interface IToolGovernanceAuditClient
{
    Task<ToolGovernanceAuditPage> GetAsync(
        ToolExecutionOwnerKind ownerKind,
        string runId,
        long afterSequence,
        int limit,
        ToolGovernanceAuditFilters filters,
        CancellationToken cancellationToken);
}

public interface IWorkApiClient
{
    Task<IReadOnlyList<WorkSummary>> GetWorkItemsAsync(CancellationToken cancellationToken);
    Task<WorkTaskOperationsPageResponse> GetTasksAsync(string? workspaceId, WorkTaskStatus? status, string? search, bool? hasPendingAction, int page, int pageSize, string sort, string direction, CancellationToken cancellationToken);
    Task<WorkTaskOperationsCountersResponse> GetTaskSummaryAsync(string? workspaceId, CancellationToken cancellationToken);
    Task<WorkTaskOperationsDetailResponse> GetTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task<FlowRun> GetTaskFlowRunAsync(Guid taskId, string runId, CancellationToken cancellationToken);
    Task<PendingActionContract> RespondTaskPendingActionAsync(Guid taskId, Guid actionId, IReadOnlyDictionary<string, JsonElement> values, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkplaceWorkspaceResponse>> GetWorkspacesAsync(CancellationToken cancellationToken);
    Task PauseTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task ResumeTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task CancelTaskAsync(Guid taskId, CancellationToken cancellationToken);
}

public interface IEntryAdministrationApiClient
{
    Task<IReadOnlyList<EntryDraftResponse>> GetEntriesAsync(CancellationToken cancellationToken);
    Task<EntryDraftResponse> GetEntryAsync(string name, CancellationToken cancellationToken);
    Task<EntryDraftResponse> GetEntryAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? GetEntryAsync(name, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Entries.");
    Task<EntryDraft> SaveEntryAsync(EntryDraft draft, CancellationToken cancellationToken);
    Task<EntryDraft> SaveEntryAsync(ResourceNamespace @namespace, EntryDraft draft, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? SaveEntryAsync(draft, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Entries.");
    Task<EntryValidationResponse> ValidateEntryAsync(string name, CancellationToken cancellationToken);
    Task<EntryValidationResponse> ValidateEntryAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? ValidateEntryAsync(name, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Entries.");
    Task<EntryResource> PublishEntryAsync(string name, CancellationToken cancellationToken);
    Task<EntryResource> PublishEntryAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? PublishEntryAsync(name, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Entries.");
    Task<IReadOnlyList<EntryDependencyResponse>> GetDependenciesAsync(string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntryDependencyResponse>> GetDependenciesAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? GetDependenciesAsync(name, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Entries.");
    Task<IReadOnlyList<ResourcePickerItem>> GetResourcesAsync(EntryBindingKind kind, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntryResponse>> GetPublishedEntriesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkplaceWorkspaceResponse>> GetWorkspacesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkplaceDashboardDraftResponse>> GetDashboardsAsync(string workspaceName, CancellationToken cancellationToken);
    Task<WorkplaceDashboardDraftResponse> GetDashboardAsync(string workspaceName, string dashboardName, CancellationToken cancellationToken);
    Task<WorkplaceDashboardDraft> SaveDashboardAsync(WorkplaceDashboardDraft draft, CancellationToken cancellationToken);
    Task<WorkplaceDashboard> PublishDashboardAsync(string workspaceName, string dashboardName, CancellationToken cancellationToken);
    Task DeleteDashboardAsync(string workspaceName, string dashboardName, CancellationToken cancellationToken);
}

public interface IFlowApiClient
{
    Task<IReadOnlyList<FlowSummary>> GetFlowsAsync(CancellationToken cancellationToken);
    Task<FlowResponse> GetFlowAsync(string flowId, CancellationToken cancellationToken);
    Task<FlowResponse> GetFlowAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? GetFlowAsync(flowId, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Flows.");
    Task<FlowResourceSnapshot> GetFlowSnapshotAsync(string flowId, CancellationToken cancellationToken);
    Task<FlowResourceSnapshot> CreateFlowAsync(CreateFlowRequest request, CancellationToken cancellationToken);
    Task<FlowResourceSnapshot> UpdateFlowAsync(string flowId, UpdateFlowRequest request, string etag, CancellationToken cancellationToken);
    Task<FlowVersionResponse> CreateFlowVersionAsync(string flowId, CreateFlowVersionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(string flowId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? GetFlowVersionsAsync(flowId, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Flows.");
    async Task<FlowVersionResponse> GetFlowVersionAsync(ResourceNamespace @namespace, string flowId, string version, CancellationToken cancellationToken) =>
        (await GetFlowVersionsAsync(@namespace, flowId, cancellationToken)).Single(value => string.Equals(value.Version, version, StringComparison.Ordinal));
    Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(string? flowId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? GetFlowRunsAsync(flowId, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Flows.");
    Task<FlowRun> GetFlowRunAsync(string runId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowRunEvent>> GetFlowRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken);
    Task<IReadOnlyList<InputRequest>> GetFlowRunInputsAsync(string runId, CancellationToken cancellationToken);
    Task<InputRequest> RespondToFlowRunInputAsync(string runId, string inputId, JsonElement value, CancellationToken cancellationToken);
    Task<FlowRun> CreateFlowRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken);
    Task<FlowRun> CreateFlowRunAsync(ResourceNamespace @namespace, string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? CreateFlowRunAsync(flowId, request, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Flows.");
    Task<FlowRun> CancelFlowRunAsync(string runId, CancellationToken cancellationToken);
    IAsyncEnumerable<FlowRun> ObserveFlowRunAsync(string runId, CancellationToken cancellationToken);
    Task<FlowDraftResponse> CreateDraftAsync(CreateFlowDraftRequest request, CancellationToken cancellationToken);
    Task<FlowDraftResponse> GetDraftAsync(string flowId, CancellationToken cancellationToken);
    Task<FlowDraftResponse> SaveDraftAsync(string flowId, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken);
    Task<FlowValidationResponse> ValidateDraftAsync(string flowId, CancellationToken cancellationToken);
    Task<FlowSourceResponse> GetDraftSourceAsync(string flowId, string format, CancellationToken cancellationToken);
    Task<FlowDraftResponse> ReplaceDraftSourceAsync(string flowId, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken);
    Task<FlowVersionResponse> PublishDraftAsync(string flowId, PublishFlowDraftRequest request, CancellationToken cancellationToken);
    Task<FlowRun> CreateDraftRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken);
    Task<FlowDraftResponse> CreateDraftFromVersionAsync(string flowId, string version, CancellationToken cancellationToken);
}

public sealed record FlowResourceSnapshot(FlowResponse Value, string ETag);

public interface IAgentstrationEventStream
{
    Task<IReadOnlyList<EventListItem>> GetRecentEventsAsync(CancellationToken cancellationToken);
}
