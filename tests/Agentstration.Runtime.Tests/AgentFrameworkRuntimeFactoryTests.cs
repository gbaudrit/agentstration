using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.AgentFramework;
using Agentstration.Runtime.Local;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class AgentFrameworkRuntimeFactoryTests
{
    [TestMethod]
    public async Task FactoryResolvesDeclaredProfileAndPassesAgentInstructionsToMaf()
    {
        using var chatClient = new RecordingChatClient
        {
            Metadata = new ModelChatClientMetadata("reasoning-default", "local-reasoning", "ollama", "ollama-local", "qwen3:4b", new ModelGenerationOptions { Temperature = 0.2, MaxOutputTokens = 1000 })
        };
        var resolver = new RecordingResolver(chatClient);
        var factory = new AgentFrameworkRuntimeFactory(resolver, NullLoggerFactory.Instance, new GenAiObservabilityOptions { Enabled = false });
        var definition = Definition();

        var runtime = await factory.CreateAsync(definition, "revision-1", new AgentRuntimeContext(new EmptyToolCatalog()), default);
        var result = await runtime.ExecuteAsync(new AgentExecutionRequest("What is HAVING?", "run-1", new ModelExecutionOptions(0.7f, 1500)), default);

        Assert.AreEqual(definition.ModelProfileName, resolver.RequestedProfile);
        Assert.AreEqual("sql-expert", runtime.AgentId);
        Assert.AreEqual("OK", result.Output);
        Assert.IsTrue(chatClient.Options?.Instructions?.Contains(definition.EffectiveInstructions, StringComparison.Ordinal) == true);
        Assert.IsTrue(chatClient.Messages.Any(message => message.Role == ChatRole.User && message.Text.Contains("HAVING", StringComparison.Ordinal)));
        Assert.AreEqual("qwen3:4b", chatClient.Options?.ModelId);
        Assert.AreEqual(0.7f, chatClient.Options?.Temperature);
        Assert.AreEqual(1500, chatClient.Options?.MaxOutputTokens);
        Assert.AreEqual(0.7f, result.EffectiveOptions?.Temperature);
    }

    [TestMethod]
    public async Task RuntimeResolvesCurrentProfileClientForEveryExecution()
    {
        using var first = new RecordingChatClient { Metadata = new ModelChatClientMetadata("profile", "deployment", "ollama", "local", "qwen3:1.7b") };
        using var second = new RecordingChatClient { Metadata = new ModelChatClientMetadata("profile", "deployment", "ollama", "local", "qwen3:4b") };
        var resolver = new RecordingResolver(first);
        var runtime = await new AgentFrameworkRuntimeFactory(resolver, NullLoggerFactory.Instance, new GenAiObservabilityOptions { Enabled = false })
            .CreateAsync(Definition(), "revision-1", new AgentRuntimeContext(new EmptyToolCatalog()), default);

        _ = await runtime.ExecuteAsync(new AgentExecutionRequest("first"), default);
        resolver.Client = second;
        var result = await runtime.ExecuteAsync(new AgentExecutionRequest("second"), default);

        Assert.AreEqual(2, resolver.ResolutionCount);
        Assert.AreEqual("qwen3:4b", result.ModelName);
        Assert.IsTrue(second.Messages.Any(message => message.Text.Contains("second", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RuntimeMapsCanonicalOptionsAndNormalizesStreamingEvents()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        using var chatClient = new RecordingChatClient
        {
            Metadata = new ModelChatClientMetadata(
                "profile",
                "deployment",
                "ollama",
                "local",
                "qwen3:8b",
                Generation: new ModelGenerationOptions
                {
                    Temperature = 0.2,
                    TopP = 0.8,
                    TopK = 20,
                    Seed = 42,
                    StopSequences = ["STOP"]
                },
                Reasoning: new ModelReasoningOptions { Mode = ReasoningMode.Enabled, Effort = Agentstration.Management.Abstractions.ReasoningEffort.Medium },
                Output: new ModelOutputOptions { Format = ModelOutputFormat.JsonSchema, JsonSchema = schema.RootElement.Clone(), Strict = true })
        };
        var runtime = await new AgentFrameworkRuntimeFactory(new RecordingResolver(chatClient), NullLoggerFactory.Instance, new GenAiObservabilityOptions { Enabled = false })
            .CreateAsync(Definition(), "revision-1", new AgentRuntimeContext(new EmptyToolCatalog()), default);

        var events = new List<AgentExecutionEvent>();
        await foreach (var item in runtime.ExecuteEventsAsync(
            new AgentExecutionRequest("stream", Execution: new AgentExecutionOptions { Streaming = RuntimeStreamingMode.Enabled })))
            events.Add(item);

        Assert.AreEqual("microsoft-agent-framework", runtime.RuntimeType);
        Assert.AreEqual(CapabilitySupport.Native, runtime.Capabilities.Streaming.Support);
        Assert.AreEqual(1, chatClient.StreamingCalls);
        Assert.IsTrue(events.OfType<ExecutionStarted>().Any());
        Assert.AreEqual("OK", string.Concat(events.OfType<ContentDelta>().Select(item => item.Content)));
        Assert.IsTrue(events.OfType<ExecutionCompleted>().Any());
        Assert.AreEqual(0.2f, chatClient.Options?.Temperature);
        Assert.AreEqual(0.8f, chatClient.Options?.TopP);
        Assert.AreEqual(20, chatClient.Options?.TopK);
        Assert.IsNotNull(chatClient.Options?.ResponseFormat);
        Assert.AreEqual("medium", chatClient.Options?.AdditionalProperties?["reasoning_effort"]);
    }

    [TestMethod]
    public async Task MafTelemetryIsEmittedWithoutPromptContent()
    {
        const string secretPrompt = "secret-customer-prompt";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentFrameworkRuntimeFactory.TelemetrySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        using var chatClient = new RecordingChatClient
        {
            Metadata = new ModelChatClientMetadata("profile", "deployment", "ollama", "local", "qwen3:1.7b")
        };
        var runtime = await new AgentFrameworkRuntimeFactory(
                new RecordingResolver(chatClient),
                NullLoggerFactory.Instance,
                new GenAiObservabilityOptions())
            .CreateAsync(Definition(), "revision-1", new AgentRuntimeContext(new EmptyToolCatalog()), default);

        _ = await runtime.ExecuteAsync(new AgentExecutionRequest(secretPrompt, "run-telemetry"), default);

        Assert.IsNotEmpty(stopped);
        var emittedData = string.Join(' ', stopped.SelectMany(ActivityData));
        Assert.IsFalse(emittedData.Contains(secretPrompt, StringComparison.Ordinal));
    }

    private static IEnumerable<string> ActivityData(Activity activity) =>
        activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")
            .Concat(activity.Events.SelectMany(activityEvent => activityEvent.Tags.Select(tag => $"{tag.Key}={tag.Value}")));

    private static ExecutableAgentDefinition Definition() => new()
    {
        AgentId = Guid.NewGuid(),
        AgentKey = "sql-expert",
        DisplayName = "SQL Expert",
        Description = "SQL specialist",
        AgentVersion = 1,
        EffectiveInstructions = "Focus on SQL Server.",
        ModelProfileName = "reasoning-default",
        RuntimeProfileName = "maf-default",
        EffectiveToolNames = [],
        MiddlewareIds = [],
        ContextProviderIds = [],
        Capabilities = [],
        Handler = "prompt-agent",
        DefinitionHash = "hash"
    };

    private sealed class RecordingResolver(IChatClient client) : IChatClientResolver
    {
        public string? RequestedProfile { get; private set; }
        public IChatClient Client { get; set; } = client;
        public int ResolutionCount { get; private set; }

        public ValueTask<IChatClient> ResolveAsync(string modelProfileResourceId, CancellationToken cancellationToken = default)
        {
            RequestedProfile = modelProfileResourceId;
            ResolutionCount++;
            return ValueTask.FromResult(Client);
        }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public IReadOnlyList<ChatMessage> Messages { get; private set; } = [];
        public ChatOptions? Options { get; private set; }
        public ModelChatClientMetadata? Metadata { get; init; }
        public int StreamingCalls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToArray();
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingCalls++;
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(ModelChatClientMetadata) ? Metadata : serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
