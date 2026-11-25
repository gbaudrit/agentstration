using System.Text.Json.Serialization;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Coding.Contracts;

public static class CodingExecutionProtocol
{
    public const string RuntimeCapability = "coding-cli";
    public const string CapabilityVersion = "1.0";
    public const string MaterialVersion = "1.0";
    public const string AdmissionKind = "coding.execution";
    public const int MaximumPatchBytes = 64 * 1024;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CodingRuntimeProfileReference(string Name, ResourceNamespace Namespace)
{
    public Guid? Uid { get; init; }
    public long? Generation { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CodingHarnessReference(string Name, ResourceNamespace Namespace, string Version, string Digest)
{
    public Guid? Uid { get; init; }
    public long? Generation { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CodingExecutionConfiguration(CodingRuntimeProfileReference RuntimeProfile, CodingHarnessReference Harness,
    string Repository, int TimeoutSeconds = 120)
{
    public IReadOnlyList<CodingRepositoryCheck> Checks { get; init; } = [];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CodingRepositoryCheck(string Name, string Command, IReadOnlyList<string> Arguments);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CodingTaskInput(string BaseRevision, IReadOnlyDictionary<string, string> Bindings);

public sealed record CodingInvocationCoordinates(WorkspaceId WorkspaceId, Guid PrincipalId, WorkProjectId ProjectId,
    WorkPlanId PlanId, long PlanRevision, string PlanDigest, WorkTaskId TaskId, long TaskVersion, string TaskDigest,
    Guid TaskAttemptId, WorkItemId WorkItemId, string FlowRunId, Guid AssignmentId, Guid AssignmentAttemptId, Guid InvocationId);

public sealed record CodingExecutionMaterial(string Version, CodingInvocationCoordinates Coordinates, CodingExecutionConfiguration Configuration,
    HarnessProfileVersionResource Harness, CodingRepositoryIdentity Repository, CodingTaskInput Input, string Instruction);

public enum CodingConsumptionAvailability { Known, Partial, Unavailable }
public sealed record CodingConsumptionObservation(string ObservationId, CodingInvocationCoordinates Coordinates,
    CodingConsumptionAvailability Availability, long? InputTokens, long? CachedInputTokens, long? OutputTokens,
    long? ReasoningOutputTokens, DateTimeOffset ObservedAt, string? Reason = null);
public sealed record CodingCheckEvidence(string Name, int? ExitCode, bool Completed, string? Diagnostic = null);
public sealed record CodingNativeResult(CodingInvocationCoordinates Coordinates, string BaseRevision, string? Patch,
    string? PatchDigest, string Summary, int? CliExitCode, IReadOnlyList<CodingCheckEvidence> Checks,
    IReadOnlyList<CodingConsumptionObservation> Consumption, string? Diagnostic = null);
