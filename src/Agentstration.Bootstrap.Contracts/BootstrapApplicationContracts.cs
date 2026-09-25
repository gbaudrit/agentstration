using System.Text.Json.Serialization;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;

namespace Agentstration.Bootstrap.Contracts;

public static class BootstrapKinds
{
    public const string BootstrapProfile = "BootstrapProfile";
    public const string BootstrapApplication = "BootstrapApplication";
    public const string InstanceInitialization = "InstanceInitialization";
}

[JsonConverter(typeof(JsonStringEnumConverter<InstanceInitializationStatus>))]
public enum InstanceInitializationStatus
{
    [JsonStringEnumMemberName("initializing")] Initializing,
    [JsonStringEnumMemberName("ready")] Ready,
    [JsonStringEnumMemberName("failed")] Failed
}

public sealed record InstanceInitializationProperties
{
    public int TargetVersion { get; init; }
    public InstanceInitializationStatus Status { get; init; }
    public string? OwnerInstanceId { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public long FencingToken { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? LastError { get; init; }
}

public sealed record InstanceInitializationResource : Resource
{
    public InstanceInitializationProperties Definition { get; init; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<BootstrapApplicationSource>))]
public enum BootstrapApplicationSource
{
    [JsonStringEnumMemberName("startup")] Startup,
    [JsonStringEnumMemberName("manual")] Manual
}

[JsonConverter(typeof(JsonStringEnumConverter<BootstrapApplicationStatus>))]
public enum BootstrapApplicationStatus
{
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("partiallyApplied")] PartiallyApplied,
    [JsonStringEnumMemberName("interrupted")] Interrupted,
    [JsonStringEnumMemberName("failed")] Failed
}

public sealed record BootstrapAppliedResource(string Profile, string Location, string Kind, string Name, BootstrapResourceDisposition Disposition, string? Message = null);

public sealed record BootstrapApplicationProperties
{
    public BootstrapApplicationSource Source { get; init; }
    public Guid? ActorPrincipalId { get; init; }
    public IReadOnlyList<string> Profiles { get; init; } = [];
    public BootstrapProfileScope Scope { get; init; }
    public BootstrapApplicationTarget? Target { get; init; }
    public IReadOnlyList<BootstrapBindingSelection> Bindings { get; init; } = [];
    public BootstrapSourceProvenance? SourceProvenance { get; init; }
    public string Digest { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public BootstrapApplicationStatus Status { get; init; } = BootstrapApplicationStatus.Running;
    public string? Error { get; init; }
    public IReadOnlyList<BootstrapAppliedResource> Resources { get; init; } = [];
}

public sealed record BootstrapApplicationResource : Resource
{
    public BootstrapApplicationProperties Definition { get; init; } = new();
}
