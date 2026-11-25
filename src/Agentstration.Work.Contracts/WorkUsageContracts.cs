using Agentstration.Work;

namespace Agentstration.Work.Contracts;

public sealed record CaptureWorkUsageRequest(Guid PlanId, Guid AttemptId);
public sealed record CorrectWorkUsageRequest(Guid CommandId, Guid ExpectedPreviousId, string ExpectedPreviousDigest,
    WorkUsageMeasurement Measurement, WorkUsageTotals Totals, string Reason, string EvidenceReference);
