using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Agentstration.Runtime.AgentFramework;

public sealed class AgentFrameworkRuntimeFactory(
    IChatClientResolver chatClients,
    ILoggerFactory loggerFactory,
    GenAiObservabilityOptions observability) : IAgentRuntimeFactory
{
    public const string TelemetrySourceName = "Agentstration.MAF";

    public string Handler => "prompt-agent";

    internal async Task<AIAgent> CreateAgentAsync(
        ExecutableAgentDefinition definition,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var tools = (await context.Tools.ResolveAsync(definition.EffectiveToolNames, cancellationToken))
            .Select(MapTool)
            .ToList();
        var chatClient = await chatClients.ResolveAsync(definition.ModelProfileName, cancellationToken);
        AIAgent agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Id = definition.AgentId.ToString("N"),
                Name = definition.AgentKey,
                Description = definition.Description,
                ChatOptions = new Microsoft.Extensions.AI.ChatOptions
                {
                    Instructions = definition.EffectiveInstructions,
                    Tools = tools
                }
            });
        return Observe(agent, observability.Enabled);
    }

    public async Task<IAgentRuntime> CreateAsync(ExecutableAgentDefinition definition, string revisionId, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tools = (await context.Tools.ResolveAsync(definition.EffectiveToolNames, cancellationToken))
            .Select(MapTool)
            .ToList();
        return new AgentFrameworkRuntime(
            definition.AgentKey,
            revisionId,
            definition.ModelProfileName,
            definition.EffectiveInstructions,
            definition.Description,
            tools,
            chatClients,
            observability.Enabled,
            loggerFactory.CreateLogger<AgentFrameworkRuntime>());
    }

    private static Microsoft.Extensions.AI.AITool MapTool(IAgentTool tool) =>
        tool.GetService(typeof(Microsoft.Extensions.AI.AITool)) as Microsoft.Extensions.AI.AITool
        ?? Microsoft.Extensions.AI.AIFunctionFactory.Create(
            (JsonElement? arguments, CancellationToken cancellationToken) => tool.InvokeAsync(arguments, cancellationToken),
            tool.Name,
            tool.Description);

    private static AIAgent Observe(AIAgent agent, bool enabled) => enabled
        ? agent.AsBuilder()
            .UseOpenTelemetry(TelemetrySourceName, telemetry => telemetry.EnableSensitiveData = false)
            .Build()
        : agent;

    private sealed class AgentFrameworkRuntime(
        string agentId,
        string revisionId,
        string modelProfileId,
        string instructions,
        string? description,
        IList<Microsoft.Extensions.AI.AITool> tools,
        IChatClientResolver chatClients,
        bool observabilityEnabled,
        ILogger<AgentFrameworkRuntime> logger) : IAgentRuntime
    {
        public string AgentId { get; } = agentId;
        public string RevisionId { get; } = revisionId;
        public string RuntimeType => "microsoft-agent-framework";
        public AgentRuntimeCapabilities Capabilities { get; } = new()
        {
            Streaming = new(CapabilitySupport.Native),
            Sessions = new(CapabilitySupport.Partial),
            Tools = new(CapabilitySupport.Native),
            StructuredOutput = new(CapabilitySupport.Native),
            Reasoning = new ReasoningCapability
            {
                Support = CapabilitySupport.Partial,
                SupportedEfforts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "low", "medium", "high" }
            }
        };

        public async Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Input);
            var chatClient = await chatClients.ResolveAsync(modelProfileId, cancellationToken);
            var model = chatClient.GetService(typeof(ModelChatClientMetadata)) as ModelChatClientMetadata;
            AIAgent agent = AgentFrameworkRuntimeFactory.Observe(new ChatClientAgent(
                chatClient,
                instructions: instructions,
                name: AgentId,
                description: description,
                tools: tools), observabilityEnabled);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Executing agent {AgentName} revision {RevisionId} with model profile {ModelProfile} for run {RunId}",
                    AgentId,
                    RevisionId,
                    modelProfileId,
                    request.SessionId);
                if (model is not null)
                {
                    logger.LogInformation(
                        "Run {RunId} uses deployment {Deployment}, provider {ProviderType}/{ProviderName}, and model {ModelName}",
                        request.SessionId,
                        model.Deployment,
                        model.ProviderType,
                        model.ProviderName,
                        model.ModelName);
                }
            }
            var generation = model?.Generation;
            var effective = new ModelExecutionOptions(
                request.Options?.Temperature ?? (generation?.Temperature is double temperature ? checked((float)temperature) : null),
                request.Options?.MaxOutputTokens ?? generation?.MaxOutputTokens);
            var chatOptions = AgentFrameworkChatOptionsMapper.Map(model, request.Options);
            var runOptions = new ChatClientAgentRunOptions(chatOptions);
            var response = await agent.RunAsync(request.Input, options: runOptions, cancellationToken: cancellationToken);
            return new AgentExecutionResult(response.Text, request.SessionId, model?.ProviderType, model?.ModelName, effective);
        }

        public async IAsyncEnumerable<AgentExecutionEvent> ExecuteEventsAsync(
            AgentExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Input);
            var executionId = request.SessionId ?? Guid.NewGuid().ToString("N");
            yield return new ExecutionStarted(executionId);
            var chatClient = await chatClients.ResolveAsync(modelProfileId, cancellationToken);
            var model = chatClient.GetService(typeof(ModelChatClientMetadata)) as ModelChatClientMetadata;
            var agent = AgentFrameworkRuntimeFactory.Observe(new ChatClientAgent(chatClient, instructions: instructions, name: AgentId, description: description, tools: tools), observabilityEnabled);
            var chatOptions = AgentFrameworkChatOptionsMapper.Map(model, request.Options);
            var effective = new ModelExecutionOptions(
                chatOptions.Temperature,
                chatOptions.MaxOutputTokens,
                chatOptions.TopP,
                chatOptions.TopK,
                checked((int?)chatOptions.Seed),
                chatOptions.StopSequences?.ToArray(),
                request.Execution?.Streaming ?? request.Options?.Streaming ?? RuntimeStreamingMode.Automatic);
            var output = new StringBuilder();
            if (effective.Streaming == RuntimeStreamingMode.Disabled)
            {
                var response = await agent.RunAsync(
                    request.Input,
                    options: new ChatClientAgentRunOptions(chatOptions),
                    cancellationToken: cancellationToken);
                if (!string.IsNullOrEmpty(response.Text)) yield return new ContentDelta(response.Text);
                yield return new ExecutionCompleted(new AgentExecutionResult(response.Text, request.SessionId, model?.ProviderType, model?.ModelName, effective));
                yield break;
            }
            await using var updates = agent.RunStreamingAsync(
                request.Input,
                options: new ChatClientAgentRunOptions(chatOptions),
                cancellationToken: cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                Exception? failure = null;
                var hasUpdate = false;
                try
                {
                    hasUpdate = await updates.MoveNextAsync();
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failure = exception;
                }
                if (failure is not null)
                {
                    yield return new ExecutionFailed(new AgentExecutionError("maf_execution_failed", failure.Message));
                    yield break;
                }
                if (!hasUpdate) break;
                var update = updates.Current;
                if (string.IsNullOrEmpty(update.Text)) continue;
                output.Append(update.Text);
                yield return new ContentDelta(update.Text);
            }
            yield return new ExecutionCompleted(new AgentExecutionResult(output.ToString(), request.SessionId, model?.ProviderType, model?.ModelName, effective));
        }

    }
}

public sealed class AgentFrameworkAgentRouter(IChatClientResolver chatClients, GenAiObservabilityOptions observability) : IAgentRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string RouterModelProfileName = "reasoning-default";

    public async Task<AgentRouteResult> SelectAsync(AgentRouteRequest request, IReadOnlyCollection<RoutableAgent> candidates, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Input);
        if (candidates.Count == 0) throw new ArgumentException("At least one routing candidate is required.", nameof(candidates));
        var client = await chatClients.ResolveAsync(RouterModelProfileName, cancellationToken);
        AIAgent router = new ChatClientAgent(
            client,
            instructions: "AGENTSTRATION_ROUTER: Select exactly one candidate for the request. Return only JSON with agentId, confidence, and reason. Never execute the selected agent.",
            name: "agentstration-router",
            description: "Selects one deployed specialized agent.");
        if (observability.Enabled)
        {
            router = router.AsBuilder()
                .UseOpenTelemetry(AgentFrameworkRuntimeFactory.TelemetrySourceName, telemetry => telemetry.EnableSensitiveData = false)
                .Build();
        }
        var payload = JsonSerializer.Serialize(new { request = request.Input, candidates }, JsonOptions);
        var response = await router.RunAsync(payload, cancellationToken: cancellationToken);
        var result = JsonSerializer.Deserialize<AgentRouteResult>(response.Text.Trim().Trim('`'), JsonOptions)
            ?? throw new InvalidOperationException("The routing agent returned an invalid result.");
        if (!candidates.Any(candidate => string.Equals(candidate.AgentId, result.AgentId, StringComparison.Ordinal)))
            throw new InvalidOperationException("The routing agent selected an unknown candidate.");
        return result;
    }
}
