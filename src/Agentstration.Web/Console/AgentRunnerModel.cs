using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;

namespace Agentstration.Web.Console;

public enum ToolArgumentRetentionMode { Inherit, Retain, DoNotRetain }

public sealed class AgentRunnerModel
{
    [Required, StringLength(100_000, MinimumLength = 1)]
    public string Prompt { get; set; } = string.Empty;

    public string? Context { get; set; }
    public string? RuntimeParameters { get; set; }
    public RuntimeStreamingMode Streaming { get; set; } = RuntimeStreamingMode.Automatic;
    public ToolArgumentRetentionMode ToolArgumentRetention { get; set; }
    [Range(1, 600)] public int TimeoutSeconds { get; set; } = 120;

    public CreateRuntimeRunRequest ToRequest(AgentResource agent)
    {
        IReadOnlyDictionary<string, JsonElement> parameters = new Dictionary<string, JsonElement>();
        if (!string.IsNullOrWhiteSpace(RuntimeParameters))
        {
            using var document = JsonDocument.Parse(RuntimeParameters);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Runtime parameters must be a JSON object.");
            parameters = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            foreach (var parameter in parameters)
            {
                if (string.Equals(parameter.Key, "temperature", StringComparison.OrdinalIgnoreCase))
                {
                    if (!parameter.Value.TryGetSingle(out var temperature) || temperature is < 0 or > 2)
                        throw new ArgumentException("Temperature must be a number between 0 and 2.");
                }
                else if (string.Equals(parameter.Key, "maxOutputTokens", StringComparison.OrdinalIgnoreCase))
                {
                    if (!parameter.Value.TryGetInt32(out var maxOutputTokens) || maxOutputTokens <= 0)
                        throw new ArgumentException("MaxOutputTokens must be a positive integer.");
                }
                else
                {
                    throw new ArgumentException($"Runtime parameter '{parameter.Key}' is not supported. Use temperature or maxOutputTokens.");
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(Context))
        {
            var trimmed = Context.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) _ = JsonDocument.Parse(Context);
        }
        return new CreateRuntimeRunRequest
        {
            Agent = new RuntimeAgentReference(agent.Metadata.Name, agent.Generation) { Namespace = agent.Namespace },
            Input = new RuntimeRunInput
            {
                Messages = [new RuntimeRunMessage(RuntimeMessageRole.User, Prompt)],
                Context = string.IsNullOrWhiteSpace(Context) ? null : Context
            },
            Execution = new RuntimeExecutionOptions
            {
                Mode = RuntimeExecutionMode.Interactive,
                Streaming = Streaming,
                PersistToolArguments = ToolArgumentRetention switch
                {
                    ToolArgumentRetentionMode.Retain => true,
                    ToolArgumentRetentionMode.DoNotRetain => false,
                    _ => null
                },
                TimeoutSeconds = TimeoutSeconds,
                Parameters = parameters
            },
            Origin = RuntimeRunOrigin.Console
        };
    }
}

public sealed class AgentRunnerState
{
    private readonly List<RuntimeRunEvent> events = [];
    public RuntimeRun? Run { get; private set; }
    public string Response { get; private set; } = string.Empty;
    public RuntimeRunState State { get; private set; } = RuntimeRunState.Pending;
    public IReadOnlyList<RuntimeRunEvent> Events => events;
    public IReadOnlyList<RuntimeToolCall> ToolCalls => (Run?.Status.ToolCalls ?? [])
        .Concat(events.Where(runEvent => runEvent.ToolCall is not null).Select(runEvent => runEvent.ToolCall!))
        .GroupBy(toolCall => toolCall.Id, StringComparer.Ordinal)
        .Select(group => group.Last())
        .ToArray();
    public long LastSequence => events.Count == 0 ? 0 : events[^1].Sequence;

    public void Reset(RuntimeRun run)
    {
        Run = run;
        Response = run.Status.Response ?? string.Empty;
        State = run.Status.State;
        events.Clear();
    }

    public void Apply(RuntimeRunEvent runEvent)
    {
        if (!AddEvent(runEvent)) return;
        if (runEvent.Kind == RuntimeRunEventKind.ResponseDelta && runEvent.Content is not null) Response += runEvent.Content;
        if (runEvent.State is { } state) State = state;
    }

    public void Restore(RuntimeRunEvent runEvent) => AddEvent(runEvent);

    public void Refresh(RuntimeRun run)
    {
        Run = run;
        State = run.Status.State;
        if (string.IsNullOrEmpty(Response)) Response = run.Status.Response ?? string.Empty;
    }

    private bool AddEvent(RuntimeRunEvent runEvent)
    {
        if (events.Any(item => item.EventId == runEvent.EventId)) return false;
        events.Add(runEvent);
        return true;
    }
}
