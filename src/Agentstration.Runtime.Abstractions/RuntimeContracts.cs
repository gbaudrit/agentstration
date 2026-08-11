using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Management.Abstractions;

namespace Agentstration.Runtime.Abstractions;

public sealed record ModelExecutionOptions(
    float? Temperature = null,
    int? MaxOutputTokens = null,
    double? TopP = null,
    int? TopK = null,
    int? Seed = null,
    IReadOnlyList<string>? StopSequences = null,
    StreamingMode Streaming = StreamingMode.Automatic);
public sealed record AgentExecutionOptions
{
    public StreamingMode Streaming { get; init; } = StreamingMode.Automatic;
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
    public IReadOnlySet<ReasoningEffort> SupportedEfforts { get; init; } = new HashSet<ReasoningEffort>();
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
            .Aggregate((IEnumerable<ReasoningEffort>?)null, (current, next) => current is null ? next : current.Intersect(next))
            ?.ToHashSet() ?? [];
        var support = values.Any(value => value.SupportedEfforts.Count > 0) && efforts.Count == 0
            ? CapabilitySupport.Partial
            : feature.Support;
        return new ReasoningCapability { Support = support, SupportedEfforts = efforts };
    }
}
public sealed record AgentRuntimeContext(IToolCatalog Tools);
public sealed record ProvisioningResult(bool Succeeded, string? Endpoint, string? Error);
public sealed record RuntimeObservation(OperationalState State, string? RevisionId, string? Error);
public sealed record ReconciliationResult(AgentDeployment Deployment, bool Changed, string Reason);
public sealed record AgentRouteRequest(string Input);
public sealed record RoutableAgent(string AgentId, string Description, IReadOnlyCollection<string> Capabilities);
public sealed record AgentRouteResult(string AgentId, double Confidence, string Reason);
public sealed record AgentRuntimeReadiness(
    string AgentResourceId,
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
    Task<IAgentRuntime> CreateAsync(ResolvedAgentDefinition definition, string revisionId, AgentRuntimeContext context, CancellationToken cancellationToken);
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

public interface IAgentDeploymentProvisioner
{
    AgentHostingMode HostingMode { get; }
    Task<ProvisioningResult> ProvisionAsync(AgentRevision revision, AgentDeployment deployment, CancellationToken cancellationToken);
    Task<ProvisioningResult> DeprovisionAsync(AgentDeployment deployment, CancellationToken cancellationToken);
    Task<RuntimeObservation> ObserveAsync(AgentDeployment deployment, CancellationToken cancellationToken);
}

public interface IAgentDeploymentReconciler
{
    Task<ReconciliationResult> ReconcileAsync(AgentDeployment deployment, CancellationToken cancellationToken);
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
