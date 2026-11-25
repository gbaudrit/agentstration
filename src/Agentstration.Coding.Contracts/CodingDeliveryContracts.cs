namespace Agentstration.Coding.Contracts;

public sealed record BeginCodingDeliveryRequest(Guid Id, Guid PlanId, Guid AttemptId, long ExpectedProjectVersion,
    long ExpectedPlanVersion, long ExpectedTaskVersion, long ExpectedSharedPlanVersion, string ExpectedSharedPlanDigest,
    string Title, string Body);
