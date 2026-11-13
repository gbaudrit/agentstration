using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Flows;

/// <summary>Bounded, process-local handoff; durable stores receive metadata and opaque receipts only.</summary>
public sealed class DirectExecutionPayloadPersistence(TimeProvider clock)
    : IFlowPayloadPersistence, IRuntimeAssignmentPayloadPersistence
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public const long MaximumBytes = 256L * 1024 * 1024;
    public const long MaximumWorkspaceBytes = 64L * 1024 * 1024;
    private const int MaximumEntries = 4096;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> values = new(StringComparer.Ordinal);
    private readonly Dictionary<(ResourceScopeRef Scope, string Run), DateTimeOffset> transientRuns = [];
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement<object?>(null);
    private sealed record Entry(ResourceScopeRef Scope, object Value, long Bytes, DateTimeOffset Expires);

    public FlowRun Store(FlowRun run)
    {
        if (!run.TransientPayloads) return run;
        lock (gate) transientRuns[(run.ExecutionScopeRef, run.Id)] = run.CreatedAt + Lifetime;
        var ticket = Save(run.ExecutionScopeRef, "flow", run.Id, run, run.CreatedAt + Lifetime);
        return run with
        {
            TransientPayloadTicket = ticket,
            Input = Empty,
            Output = null,
            Error = Redact(run.Error),
            Steps = run.Steps.Select(step => step with
            { DeclaredInput = null, ResolvedInput = null, Output = null, Logs = [], Error = Redact(step.Error) }).ToArray()
        };
    }
    public FlowRun Restore(FlowRun run) => !run.TransientPayloads ? run
        : Find<FlowRun>(run.ExecutionScopeRef, "flow", run.Id, run.TransientPayloadTicket) is { } hot
            ? hot with { TransientPayloadTicket = run.TransientPayloadTicket, TransientPayloadsAvailable = true }
            : run with { TransientPayloadsAvailable = false };

    public FlowRunEvent Store(FlowRunEvent value) => !value.TransientPayloads ? value : value with
    {
        TransientPayloadTicket = Save(value.ScopeRef, "event", value.RunId + ":" + value.Sequence, value, clock.GetUtcNow() + Lifetime),
        Payload = null
    };
    public FlowRunEvent Restore(FlowRunEvent value) => !value.TransientPayloads ? value
        : Find<FlowRunEvent>(value.ScopeRef, "event", value.RunId + ":" + value.Sequence, value.TransientPayloadTicket) is { } hot
            ? hot with { TransientPayloadTicket = value.TransientPayloadTicket } : value;
    public InputRequest Store(InputRequest value) => !value.TransientPayloads ? value : value with
    {
        TransientPayloadTicket = Save(value.ScopeRef, "input", value.RunId + ":" + value.Id, value, value.CreatedAt + Lifetime),
        Prompt = string.Empty,
        Options = [],
        Schema = null,
        Response = null
    };
    public InputRequest Restore(InputRequest value) => !value.TransientPayloads ? value
        : Find<InputRequest>(value.ScopeRef, "input", value.RunId + ":" + value.Id, value.TransientPayloadTicket) ?? value;

    public RuntimeWorkerAssignment Store(RuntimeWorkerAssignment assignment)
    {
        var direct = assignment.TransientPayloads;
        lock (gate) direct |= transientRuns.ContainsKey((assignment.ExecutionScopeRef, assignment.TargetRunId));
        if (!direct) return assignment;
        assignment = assignment with { TransientPayloads = true };
        var ticket = Save(assignment.ExecutionScopeRef, "assignment", assignment.Id.ToString(), assignment, assignment.CreatedAt + Lifetime);
        return assignment with
        {
            TransientPayloadTicket = ticket,
            ExecutionEvents = assignment.ExecutionEvents.Select(value => value with { Payload = null }).ToArray(),
            Checkpoints = assignment.Checkpoints.Select(value => value with { Payload = Empty }).ToArray(),
            Attempts = assignment.Attempts.Select(value => value with { ErrorMessage = value.ErrorMessage is null ? null : "Ephemeral execution failed." }).ToArray()
        };
    }
    public RuntimeWorkerAssignment Restore(RuntimeWorkerAssignment assignment) => !assignment.TransientPayloads ? assignment
        : Find<RuntimeWorkerAssignment>(assignment.ExecutionScopeRef, "assignment", assignment.Id.ToString(), assignment.TransientPayloadTicket) is { } hot
            ? hot with { TransientPayloadTicket = assignment.TransientPayloadTicket, TransientPayloadsAvailable = true }
            : assignment with { TransientPayloadsAvailable = false };

    // Mutable processing drafts share the direct handoff's admission budget. A failed
    // replacement preserves the accepted revision; live entries are never evicted.
    internal void StoreProcessing<T>(ResourceScopeRef scope, string id, T value, DateTimeOffset expires) where T : class
    {
        var size = JsonSerializer.SerializeToUtf8Bytes(value, Json).LongLength;
        var key = Key(scope, "artifact-processing", id, "current");
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var expired in values.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) values.Remove(expired);
            if (expires <= now) throw new FlowValidationException("direct_content_unavailable", "The ephemeral execution payload has expired.");
            values.TryGetValue(key, out var previous);
            var oldSize = previous?.Bytes ?? 0;
            if (previous is null && values.Count >= MaximumEntries
                || values.Values.Sum(entry => entry.Bytes) - oldSize + size > MaximumBytes
                || values.Values.Where(entry => entry.Scope == scope).Sum(entry => entry.Bytes) - oldSize + size > MaximumWorkspaceBytes)
                throw new FlowValidationException("direct_content_capacity", "The ephemeral execution payload capacity has been reached.");
            values[key] = new(scope, value, size, expires);
        }
    }

    internal T? GetProcessing<T>(ResourceScopeRef scope, string id) where T : class =>
        Find<T>(scope, "artifact-processing", id, "current");

    private string Save<T>(ResourceScopeRef scope, string family, string id, T value, DateTimeOffset expires) where T : class
    {
        var size = JsonSerializer.SerializeToUtf8Bytes(value, Json).LongLength;
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var key in values.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) values.Remove(key);
            foreach (var key in transientRuns.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray()) transientRuns.Remove(key);
            if (expires <= now) throw new Agentstration.Flows.FlowValidationException("direct_content_unavailable", "The ephemeral execution payload has expired.");
            if (values.Count >= MaximumEntries || values.Values.Sum(entry => entry.Bytes) + size > MaximumBytes
                || values.Values.Where(entry => entry.Scope == scope).Sum(entry => entry.Bytes) + size > MaximumWorkspaceBytes)
                throw new Agentstration.Flows.FlowValidationException("direct_content_capacity", "The ephemeral execution payload capacity has been reached.");
            var ticket = Guid.NewGuid().ToString("N");
            values.Add(Key(scope, family, id, ticket), new(scope, value, size, expires));
            return ticket;
        }
    }
    private T? Find<T>(ResourceScopeRef scope, string family, string id, string? ticket) where T : class
    {
        lock (gate) return ticket is not null && values.TryGetValue(Key(scope, family, id, ticket), out var entry)
            && entry.Expires > clock.GetUtcNow() ? entry.Value as T : null;
    }
    private static string Key(ResourceScopeRef scope, string family, string id, string ticket) => $"{scope.Value}/{family}/{id}/{ticket}";
    private static FlowRunError? Redact(FlowRunError? error) => error is null ? null : new(error.Code, "Ephemeral execution failed.");
}
