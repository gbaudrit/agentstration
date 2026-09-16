namespace Agentstration.Flows;

public sealed record FlowRunCausalityPage(
    FlowRunCausalityOrigin Origin,
    IReadOnlyList<FlowRunCausalityNode> Items,
    int TotalCount,
    bool HasMore);

public sealed record FlowRunCausalityOrigin(
    string RootFlowRunId,
    FlowInvocationOrigin? InvocationOrigin,
    FlowRunTrigger Trigger,
    string? CallerId,
    string? CausationId,
    string? CorrelationId,
    string? WorkItemResourceId);

public sealed record FlowRunCausalityNode(
    string FlowRunId,
    FlowId FlowId,
    string FlowVersion,
    bool ResolvedFromActiveReference,
    FlowDefinitionState DefinitionState,
    FlowRunStatus Status,
    string? ParentFlowRunId,
    string? ParentStepName,
    int NestingDepth,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    IReadOnlyList<FlowRunAgentExecution> AgentExecutions,
    IReadOnlyList<FlowRunToolCall> ToolCalls);

public sealed record FlowRunAgentExecution(
    string StepName,
    FlowStepRunStatus Status,
    string AgentResourceId,
    long? AgentVersion,
    string? ModelProfileResourceId,
    string? Provider,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode);

public sealed record FlowRunToolCall(
    string LogicalCallId,
    string? StepName,
    string? ToolId,
    string? ToolNamespace,
    string? ToolName,
    string? ProviderId,
    string? ProviderNamespace,
    string? ExternalToolId,
    string Status,
    string? CorrelationId,
    IReadOnlyList<FlowRunToolAttempt> Attempts);

public sealed record FlowRunToolAttempt(
    string InvocationId,
    int Attempt,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    double? DurationMilliseconds,
    string? ErrorCode,
    string? FailureKind,
    int GovernanceEvaluationCount);
