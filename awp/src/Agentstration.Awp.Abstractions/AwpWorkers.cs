namespace Agentstration.Awp.Abstractions;

public static class AwpRuntimeKinds
{
    public const string MicrosoftAgentFramework = "microsoft-agent-framework";
}

public sealed record AwpRuntimeCapability(
    string RuntimeKind,
    string CapabilityVersion,
    IReadOnlyList<string> ExecutionMaterialVersions,
    string? RuntimeImplementationVersion = null);

public sealed record AwpWorkerDescriptor(
    AwpWorkerId WorkerId,
    AwpWorkerSessionId SessionId,
    string SoftwareVersion,
    int MaximumConcurrentAssignments,
    IReadOnlyList<AwpRuntimeCapability> Capabilities);

public sealed record AwpWorkerRegistrationRequest(
    IReadOnlyList<string> SupportedProtocolVersions,
    AwpWorkerDescriptor Worker);

public sealed record AwpWorkerRegistrationResponse(
    string SelectedProtocolVersion,
    DateTimeOffset RegisteredAt,
    DateTimeOffset ServerTime);
