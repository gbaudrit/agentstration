using System.Text.Json;
using Agentstration.Coding.Contracts;
using Agentstration.Work;

namespace Agentstration.Runtime.Worker.Coding;

internal sealed class CodingUsageReader(CodingInvocationCoordinates coordinates, TimeProvider clock)
{
    private int completedTurns;
    internal CodingConsumptionObservation? Read(string line)
    {
        if (line.Length > 64 * 1024) throw new CodingProcessException("coding_cli_line_limit");
        JsonDocument document;
        try { document = JsonDocument.Parse(line, new() { MaxDepth = 32 }); }
        catch (JsonException) { throw new CodingProcessException("coding_cli_event_invalid"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new CodingProcessException("coding_cli_event_invalid");
            if (type.GetString() != "turn.completed") return null;
            if (++completedTurns > 32) throw new CodingProcessException("coding_cli_turn_limit");
            var id = WorkPlanDefinition.Digest(new { coordinates.InvocationId, Turn = completedTurns });
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return new(id, coordinates, CodingConsumptionAvailability.Unavailable, null, null, null, null, clock.GetUtcNow(), "usage_unreported");
            var input = Count(usage, "input_tokens"); var cached = Count(usage, "cached_input_tokens");
            var output = Count(usage, "output_tokens"); var reasoning = Count(usage, "reasoning_output_tokens");
            if (input is { } i && cached > i || output is { } o && reasoning > o)
                return new(id, coordinates, CodingConsumptionAvailability.Unavailable, null, null, null, null, clock.GetUtcNow(), "usage_inconsistent");
            var available = input is not null || cached is not null || output is not null || reasoning is not null;
            var complete = input is not null && cached is not null && output is not null;
            return new(id, coordinates, complete ? CodingConsumptionAvailability.Known : available ? CodingConsumptionAvailability.Partial : CodingConsumptionAvailability.Unavailable,
                input, cached, output, reasoning, clock.GetUtcNow(), complete ? reasoning is null ? "reasoning_unreported" : null : "usage_partial_or_unreported");
        }
    }
    private static long? Count(JsonElement usage, string name) => usage.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count is >= 0 and <= 1_000_000_000_000 ? count : null;
}
