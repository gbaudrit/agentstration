using Agentstration.Triggers;

namespace Agentstration.Triggers.Contracts;

public sealed record TriggerSchedulePreviewRequest(TriggerSchedule Schedule, int Count = 5);
public sealed record TriggerSchedulePreviewResponse(IReadOnlyList<DateTimeOffset> Occurrences);
