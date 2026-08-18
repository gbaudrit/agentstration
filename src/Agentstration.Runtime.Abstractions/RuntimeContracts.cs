using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Runtime.Abstractions;

public enum RuntimeStreamingMode { Automatic, Enabled, Disabled }

public sealed record ModelExecutionOptions(
    float? Temperature = null,
    int? MaxOutputTokens = null,
    double? TopP = null,
    int? TopK = null,
    int? Seed = null,
    IReadOnlyList<string>? StopSequences = null,
    RuntimeStreamingMode Streaming = RuntimeStreamingMode.Automatic);
public sealed record AgentExecutionOptions
{
    public RuntimeStreamingMode Streaming { get; init; } = RuntimeStreamingMode.Automatic;
}
public sealed record AgentExecutionRequest(
    string Input,
    string? SessionId = null,
    ModelExecutionOptions? Options = null,
    AgentExecutionOptions? Execution = null,
    ToolExecutionScope? ToolExecution = null);
public sealed record AgentExecutionResult(
    string Output,
    string? SessionId = null,
    string? ProviderType = null,
    string? ModelName = null,
    ModelExecutionOptions? EffectiveOptions = null,
    AgentExecutionUsage? Usage = null);

public sealed record AgentExecutionUsage(int? InputTokens = null, int? OutputTokens = null);
public sealed record AgentExecutionError(string Code, string Message, bool Retryable = false);

public abstract record AgentExecutionEvent;
public sealed record ExecutionStarted(string ExecutionId) : AgentExecutionEvent;
public sealed record ContentDelta(string Content) : AgentExecutionEvent;
public sealed record ReasoningDelta(string Content) : AgentExecutionEvent;
public sealed record ToolCallStarted(string CallId, string ToolName, JsonElement? Arguments) : AgentExecutionEvent;
public sealed record ToolCallCompleted(string CallId, JsonElement? Result) : AgentExecutionEvent;
public sealed record UsageUpdated(AgentExecutionUsage Usage) : AgentExecutionEvent;
public sealed record ExecutionCompleted(AgentExecutionResult Result) : AgentExecutionEvent;
public sealed record ExecutionFailed(AgentExecutionError Error) : AgentExecutionEvent;

public enum CapabilitySupport { Unsupported, Native, Emulated, Partial }

public sealed record FeatureCapability(CapabilitySupport Support = CapabilitySupport.Unsupported);

public sealed record ReasoningCapability
{
    public CapabilitySupport Support { get; init; }
    public IReadOnlySet<string> SupportedEfforts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record AgentRuntimeCapabilities
{
    public FeatureCapability Streaming { get; init; } = new();
    public FeatureCapability Sessions { get; init; } = new();
    public FeatureCapability Tools { get; init; } = new();
    public FeatureCapability StructuredOutput { get; init; } = new();
    public ReasoningCapability Reasoning { get; init; } = new();
}

public sealed record EffectiveCapabilities(
    FeatureCapability Streaming,
    FeatureCapability Sessions,
    FeatureCapability Tools,
    FeatureCapability StructuredOutput,
    ReasoningCapability Reasoning);

public static class EffectiveCapabilityResolver
{
    public static EffectiveCapabilities Intersect(params AgentRuntimeCapabilities[] levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Length == 0) throw new ArgumentException("At least one capability level is required.", nameof(levels));
        return new EffectiveCapabilities(
            IntersectFeature(levels.Select(level => level.Streaming)),
            IntersectFeature(levels.Select(level => level.Sessions)),
            IntersectFeature(levels.Select(level => level.Tools)),
            IntersectFeature(levels.Select(level => level.StructuredOutput)),
            IntersectReasoning(levels.Select(level => level.Reasoning)));
    }

    private static FeatureCapability IntersectFeature(IEnumerable<FeatureCapability> capabilities)
    {
        var values = capabilities.Select(capability => capability.Support).ToArray();
        if (values.Contains(CapabilitySupport.Unsupported)) return new();
        if (values.Contains(CapabilitySupport.Partial)) return new(CapabilitySupport.Partial);
        if (values.Contains(CapabilitySupport.Emulated)) return new(CapabilitySupport.Emulated);
        return new(CapabilitySupport.Native);
    }

    private static ReasoningCapability IntersectReasoning(IEnumerable<ReasoningCapability> capabilities)
    {
        var values = capabilities.ToArray();
        var feature = IntersectFeature(values.Select(value => new FeatureCapability(value.Support)));
        if (feature.Support == CapabilitySupport.Unsupported) return new();
        var efforts = values.Select(value => value.SupportedEfforts)
            .Aggregate((IEnumerable<string>?)null, (current, next) => current is null ? next : current.Intersect(next, StringComparer.OrdinalIgnoreCase))
            ?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var support = values.Any(value => value.SupportedEfforts.Count > 0) && efforts.Count == 0
            ? CapabilitySupport.Partial
            : feature.Support;
        return new ReasoningCapability { Support = support, SupportedEfforts = efforts };
    }
}
public sealed record AgentRuntimeContext
{
    public AgentRuntimeContext(IToolCatalog tools) => Tools = tools;
    public AgentRuntimeContext(IToolCatalog tools, IToolExecutionPipeline toolExecution)
    {
        Tools = tools;
        ToolExecution = toolExecution;
    }

    public IToolCatalog Tools { get; }
    public IToolExecutionPipeline ToolExecution { get; } = UnavailableToolExecutionPipeline.Instance;
}
public sealed record ExecutableAgentDefinition
{
    public required Guid AgentId { get; init; }
    public required string AgentKey { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required long AgentVersion { get; init; }
    public required string EffectiveInstructions { get; init; }
    public required string ModelProfileName { get; init; }
    public required string RuntimeProfileName { get; init; }
    public required IReadOnlyCollection<string> EffectiveToolNames { get; init; }
    public required IReadOnlyCollection<string> MiddlewareIds { get; init; }
    public required IReadOnlyCollection<string> ContextProviderIds { get; init; }
    public required IReadOnlyCollection<string> Capabilities { get; init; }
    public required string Handler { get; init; }
    public required string DefinitionHash { get; init; }
}

public sealed record ResolvedRuntimeAgent(
    Guid AgentId,
    string AgentName,
    long Generation,
    string DeploymentId,
    string RevisionId,
    string RuntimeProfileName,
    string ModelProfileName,
    ExecutableAgentDefinition Definition,
    bool Ready,
    string State,
    string? Error);

public interface IRuntimeAgentResolver
{
    Task<ResolvedRuntimeAgent> ResolveAsync(RuntimeAgentReference reference, CancellationToken cancellationToken);

    Task<ResolvedRuntimeAgent> ResolveLatestAsync(string resourceId, CancellationToken cancellationToken) =>
        throw new RuntimeAgentResolutionException("latest_agent_resolution_unsupported", $"Latest-version resolution is not supported for agent '{resourceId}'.");

    Task<ResolvedRuntimeAgent> ResolveLatestAsync(string resourceId, ResourceNamespace @namespace, CancellationToken cancellationToken) =>
        @namespace == ResourceNamespace.Default
            ? ResolveLatestAsync(resourceId, cancellationToken)
            : throw new RuntimeAgentResolutionException("latest_agent_resolution_unsupported", $"Latest-version resolution is not supported for agent '{@namespace}/{resourceId}'.");
}

public sealed class RuntimeAgentResolutionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
public sealed record AgentRouteRequest(string Input);
public sealed record RoutableAgent(string AgentId, string Description, IReadOnlyCollection<string> Capabilities);
public sealed record AgentRouteResult(string AgentId, double Confidence, string Reason);
public sealed record AgentRuntimeReadiness(
    string AgentId,
    long Generation,
    bool Ready,
    string State,
    string? DeploymentId,
    string? RevisionId,
    string? Error,
    string? RuntimeProfileId = null,
    string? ModelProfileId = null);

public interface IAgentRuntime
{
    string AgentId { get; }
    string RevisionId { get; }
    string RuntimeType => "unknown";
    AgentRuntimeCapabilities Capabilities => new();
    Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken);

    async IAsyncEnumerable<AgentExecutionEvent> ExecuteEventsAsync(
        AgentExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var executionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        yield return new ExecutionStarted(executionId);
        AgentExecutionResult? result = null;
        AgentExecutionError? error = null;
        try
        {
            result = await ExecuteAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            error = new AgentExecutionError("runtime_execution_failed", exception.Message);
        }
        if (error is not null)
        {
            yield return new ExecutionFailed(error);
            yield break;
        }
        if (result is null) yield break;
        if (!string.IsNullOrEmpty(result.Output)) yield return new ContentDelta(result.Output);
        if (result.Usage is not null) yield return new UsageUpdated(result.Usage);
        yield return new ExecutionCompleted(result);
    }
}

public interface IAgentRuntimeFactory
{
    string Handler { get; }
    Task<IAgentRuntime> CreateAsync(ExecutableAgentDefinition definition, string revisionId, AgentRuntimeContext context, CancellationToken cancellationToken);
}

public interface IAgentTool
{
    string Id { get; }
    string Name { get; }
    string? Description { get; }
    string? ProviderId { get; }
    string? ExternalId { get; }
    JsonElement InputSchema { get; }
    JsonElement? OutputSchema { get; }
    bool RequiresApproval { get; }
}

public interface IToolCatalog
{
    ValueTask<IReadOnlyCollection<IAgentTool>> ResolveAsync(IEnumerable<string> toolIds, CancellationToken cancellationToken = default);
}

public enum ToolExecutionOwnerKind { Unspecified, RuntimeRun, FlowRun }

public sealed record ToolExecutionScope
{
    public ToolExecutionOwnerKind OwnerKind { get; init; }
    public Guid? TenantId { get; init; }
    public WorkspaceId? WorkspaceId { get; init; }
    public Guid? PrincipalId { get; init; }
    public string? ExecutionId { get; init; }
    public string? CorrelationId { get; init; }
    public long? AgentGeneration { get; init; }
}

public sealed record ToolExecutionContext
{
    public ToolExecutionOwnerKind OwnerKind { get; init; }
    public required string ToolCallId { get; init; }
    public required string InvocationId { get; init; }
    public required string ToolId { get; init; }
    public required string ToolName { get; init; }
    public string? ToolProviderId { get; init; }
    public string? ExternalToolId { get; init; }
    public Guid? TenantId { get; init; }
    public WorkspaceId? WorkspaceId { get; init; }
    public Guid? PrincipalId { get; init; }
    public string? RunId { get; init; }
    public string? AgentId { get; init; }
    public long? AgentVersion { get; init; }
    public long? AgentGeneration { get; init; }
    public string? AgentRevisionId { get; init; }
    public string? CorrelationId { get; init; }
    public JsonElement? Arguments { get; init; }
}

public interface IToolExecutionPipeline
{
    ValueTask<JsonElement?> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default);
}

public abstract record ToolExecutionLifecycleEvent(
    ToolExecutionContext Context,
    DateTimeOffset Timestamp);

public sealed record ToolExecutionStarted(
    ToolExecutionContext Context,
    DateTimeOffset Timestamp) : ToolExecutionLifecycleEvent(Context, Timestamp);

public sealed record ToolExecutionCompleted(
    ToolExecutionContext Context,
    DateTimeOffset Timestamp,
    TimeSpan Duration) : ToolExecutionLifecycleEvent(Context, Timestamp);

public sealed record ToolExecutionFailed(
    ToolExecutionContext Context,
    DateTimeOffset Timestamp,
    TimeSpan Duration,
    string ErrorType,
    string ErrorMessage,
    bool Cancelled) : ToolExecutionLifecycleEvent(Context, Timestamp);

public interface IToolExecutionEventSink
{
    ValueTask PublishAsync(ToolExecutionLifecycleEvent executionEvent, CancellationToken cancellationToken = default);
}

public sealed class UnavailableToolExecutionPipeline : IToolExecutionPipeline
{
    public static UnavailableToolExecutionPipeline Instance { get; } = new();
    private UnavailableToolExecutionPipeline() { }

    public ValueTask<JsonElement?> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<JsonElement?>(new InvalidOperationException("No Agentstration Tool Execution Pipeline is configured."));
}

public interface IToolInvoker
{
    ValueTask<JsonElement?> InvokeAsync(ToolExecutionContext context, CancellationToken cancellationToken = default);
}

public interface IRuntimeRegistry
{
    void Set(string deploymentId, IAgentRuntime runtime);
    bool TryGet(string deploymentId, out IAgentRuntime? runtime);
    bool Remove(string deploymentId);
    Task<AgentExecutionResult> ExecuteAsync(string deploymentId, AgentExecutionRequest request, CancellationToken cancellationToken);
    async IAsyncEnumerable<AgentExecutionEvent> ExecuteEventsAsync(
        string deploymentId,
        AgentExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ExecutionStarted(request.SessionId ?? Guid.NewGuid().ToString("N"));
        var result = await ExecuteAsync(deploymentId, request, cancellationToken);
        if (!string.IsNullOrEmpty(result.Output)) yield return new ContentDelta(result.Output);
        yield return new ExecutionCompleted(result);
    }
}

public interface IAgentRouter
{
    Task<AgentRouteResult> SelectAsync(AgentRouteRequest request, IReadOnlyCollection<RoutableAgent> candidates, CancellationToken cancellationToken);
}
