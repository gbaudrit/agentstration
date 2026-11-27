using System.Text.Json.Serialization;

namespace Agentstration.Work;

[JsonConverter(typeof(JsonStringEnumConverter<WorkForgeRecovery>))]
public enum WorkForgeRecovery { None, Process, Durable }

public sealed record WorkForgeDeliveryRoute(WorkProjectResolvedBinding Connection, WorkForgeRecovery Recovery,
    int ReceiptWindowSeconds, int MaximumResultBytes);
public sealed record WorkForgeSubmission(Guid Id, DateTimeOffset StartedAt);
public sealed record WorkForgeDeliveryResponse(string OperationKey, string WireIntentDigest, WorkTaskDeliveryEffectStatus Status,
    WorkPullRequestReceipt? Review = null, string? ErrorCode = null);
