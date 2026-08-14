using System.Runtime.CompilerServices;
using System.Text.Json;

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
    AgentExecutionOptions? Execution = null);
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
public sealed record AgentRuntimeContext(IToolCatalog Tools);
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
    ValueTask<JsonElement?> InvokeAsync(JsonElement? arguments, CancellationToken cancellationToken = default);
    object? GetService(Type serviceType) => null;
}

public interface IToolCatalog
{
    ValueTask<IReadOnlyCollection<IAgentTool>> ResolveAsync(IEnumerable<string> toolIds, CancellationToken cancellationToken = default);
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
