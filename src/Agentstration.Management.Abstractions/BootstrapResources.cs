using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Management.Abstractions;

public static class BootstrapResourceKinds
{
    public const string PlatformAdministrator = "PlatformAdministrator";
    public const string Tenant = "Tenant";
    public const string Workspace = "Workspace";
    public const string PrincipalDefaultContext = "PrincipalDefaultContext";
    public const string PackInstallation = "PackInstallation";
    public const string BootstrapProfile = "BootstrapProfile";
}

public sealed record BootstrapResourceDocument
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public ResourceMetadata Metadata { get; init; } = new();
    public JsonElement Definition { get; init; }
}

public enum BootstrapResourceApplyResult
{
    Created,
    Skipped,
    Conflict
}

[JsonConverter(typeof(JsonStringEnumConverter<BootstrapProfileScope>))]
public enum BootstrapProfileScope
{
    [JsonStringEnumMemberName("instance")] Instance,
    [JsonStringEnumMemberName("tenant")] Tenant,
    [JsonStringEnumMemberName("workspace")] Workspace
}

[JsonConverter(typeof(JsonStringEnumConverter<BootstrapResourceDisposition>))]
public enum BootstrapResourceDisposition
{
    [JsonStringEnumMemberName("create")] Create,
    [JsonStringEnumMemberName("skip")] Skip,
    [JsonStringEnumMemberName("conflict")] Conflict,
    [JsonStringEnumMemberName("invalid")] Invalid,
    [JsonStringEnumMemberName("failed")] Failed
}

public sealed record BootstrapApplicationTarget(Guid? TenantId = null, Guid? WorkspaceId = null);

public sealed record BootstrapBindingSelection(
    string Profile,
    string Name,
    ResourceReference Target);

[JsonConverter(typeof(JsonStringEnumConverter<BootstrapBindingTargetKind>))]
public enum BootstrapBindingTargetKind
{
    [JsonStringEnumMemberName("modelProfile")] ModelProfile,
    [JsonStringEnumMemberName("modelProvider")] ModelProvider,
    [JsonStringEnumMemberName("runtimeProfile")] RuntimeProfile,
    [JsonStringEnumMemberName("extensionRegistration")] ExtensionRegistration,
    [JsonStringEnumMemberName("secret")] Secret
}

public sealed record BootstrapResourceOperationContext(
    string ProfileName,
    string ProfilePath,
    BootstrapProfileScope ProfileScope,
    BootstrapApplicationTarget? Target = null);

public sealed record BootstrapResourcePlanDetail(
    string Kind,
    string Name,
    BootstrapResourceDisposition Disposition,
    string? Description = null);

public sealed record BootstrapResourcePlanResult(
    BootstrapResourceDisposition Disposition,
    IReadOnlyList<BootstrapResourcePlanDetail>? Details = null);

public sealed class BootstrapPlanningContext
{
    private readonly HashSet<(string Kind, string Name, string? Parent)> planned = [];

    public void Register(string kind, string name, string? parent = null) =>
        planned.Add((kind, name, parent));

    public bool Contains(string kind, string name, string? parent = null) =>
        planned.Contains((kind, name, parent));
}

public interface IBootstrapResourceHandler
{
    string Kind { get; }
    BootstrapProfileScope Scope { get; }
    Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken);
    Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken);
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

public sealed record BootstrapAppliedResource(
    string Profile,
    string Location,
    string Kind,
    string Name,
    BootstrapResourceDisposition Disposition,
    string? Message = null);

public sealed record BootstrapApplicationProperties
{
    public BootstrapApplicationSource Source { get; init; }
    public Guid? ActorPrincipalId { get; init; }
    public IReadOnlyList<string> Profiles { get; init; } = [];
    public BootstrapProfileScope Scope { get; init; }
    public BootstrapApplicationTarget? Target { get; init; }
    public IReadOnlyList<BootstrapBindingSelection> Bindings { get; init; } = [];
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
