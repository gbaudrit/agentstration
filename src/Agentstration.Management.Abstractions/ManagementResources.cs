using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Management.Abstractions;

public static class ManagementApiVersions
{
    public const string CoreV1 = "agentstration.io/v1";
    public const string V20260801 = CoreV1;
}

public static class ResourceKinds
{
    public const string Agent = "Agent";
    public const string AgentRevision = "AgentRevision";
    public const string AgentDeployment = "AgentDeployment";
    public const string Flow = "Flow";
    public const string Entry = "Entry";
    public const string ManagementOperation = "ManagementOperation";
    public const string InstalledPack = "InstalledPack";
    public const string PackConfiguration = "PackConfiguration";
    public const string ModelProvider = "ModelProvider";
    public const string ExtensionRegistration = "ExtensionRegistration";
    public const string ModelProfile = "ModelProfile";
    public const string RuntimeProfile = "RuntimeProfile";
    public const string Secret = "Secret";
    public const string Vault = "Vault";
    public const string Tool = "Tool";
    public const string ToolProvider = "ToolProvider";
    public const string ToolExecutionHook = "ToolExecutionHook";
    public const string Trigger = "Trigger";
}

public sealed record ResourceMetadata
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Annotations { get; init; } = new Dictionary<string, string>();
}

public readonly record struct ResourceKey(string Kind, string Name, ResourceNamespace Namespace = default)
{
    public static ResourceKey Create(string kind, string name, ResourceNamespace @namespace = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(kind, name, @namespace);
    }

    public ResourceAddress Address => ResourceAddress.Create(Namespace, Kind, Name);
    public override string ToString() => Address.ToString();
}

public abstract record Resource
{
    public Guid Uid { get; init; }
    public required string ApiVersion { get; init; }
    public string Kind { get; init; } = string.Empty;
    public ResourceMetadata Metadata { get; init; } = new();
    [JsonIgnore] public string Name => Metadata.Name;
    [JsonIgnore]
    public ResourceNamespace Namespace => Metadata.Namespace;
    [JsonIgnore]
    public ResourceAddress Address => ResourceAddress.Create(Namespace, Kind, Name);
    public Guid TenantId { get; init; }
    public Guid WorkspaceId { get; init; }
    public long Generation { get; init; }
    public ResourceStatus Status { get; init; } = new() { ProvisioningState = ProvisioningState.Accepted };
    public string? ETag { get; init; }

    public Resource WithSystemState(Guid uid, Guid tenantId, Guid workspaceId, string etag) => this with
    {
        Uid = uid,
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        ETag = etag,
        Status = Status with { ResourceVersion = etag }
    };
}

public sealed record ResourceCondition
{
    public required string Type { get; init; }
    public required string Status { get; init; }
    public string? Reason { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? LastTransitionTime { get; init; }
}

public sealed record ResourceStatus
{
    public required ProvisioningState ProvisioningState { get; init; }
    public string? ResourceVersion { get; init; }
    public IReadOnlyList<ResourceCondition> Conditions { get; init; } = [];
}

public sealed record ResourceReference
{
    public ResourceReference(string name, string? workspaceRef = null, ResourceNamespace? @namespace = null) { Name = name; WorkspaceRef = workspaceRef; Namespace = @namespace; }
    public string Name { get; init; }
    public string? WorkspaceRef { get; init; }
    [JsonIgnore] public string ResourceId => Name;
    public ResourceNamespace? Namespace { get; init; }
    public ResourceAddress Resolve(ResourceNamespace ownerNamespace, string kind) =>
        ResourceAddress.Create(Namespace ?? ownerNamespace, kind, Name);
}

public record AgentProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string Handler { get; init; } = "prompt-agent";
    public required string Instructions { get; init; }
    public required ResourceReference ModelProfile { get; init; }
    public ResourceReference RuntimeProfile { get; init; } = new("maf-builtin", @namespace: ResourceNamespace.Default);
    public IReadOnlyList<ResourceReference> Tools { get; init; } = [];
    public IReadOnlyList<string> Behaviors { get; init; } = [];
    public IReadOnlyList<string> Middleware { get; init; } = [];
    public IReadOnlyList<string> ContextProviders { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> Settings { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record AgentResource : Resource
{
    public AgentProperties Definition { get; init; } = null!;
}

public sealed record AgentDeploymentSpec
{
    public required string Environment { get; init; }
    public required string RuntimeProfileName { get; init; }
    public ResourceNamespace RuntimeProfileNamespace { get; init; } = ResourceNamespace.Default;
    public required AgentHostingMode HostingMode { get; init; }
}

public sealed record ResolvedAgentSpec
{
    public required Guid AgentUid { get; init; }
    public required string AgentName { get; init; }
    public required long Generation { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required string Instructions { get; init; }
    public required ResourceReference ModelProfileRef { get; init; }
    public required IReadOnlyList<ResourceReference> ToolRefs { get; init; }
}

public enum AgentHostingMode { InProcess, SharedHost, DedicatedProcess, DedicatedContainer, RemoteEndpoint, FoundryHosted }
public enum ProvisioningState { Accepted, Validating, Creating, Updating, Succeeded, Failed, Deleting, Canceled }
public enum OperationalState { Starting, Ready, Degraded, Suspended, Stopped, Unavailable }
public enum DesiredAgentState { Running, Stopped }
public enum OperationStatus { Accepted, Running, Succeeded, Failed, Canceled }
public enum AgentIdentityType { None, SystemAssigned, UserAssigned, External }

public sealed record AgentRevision : Resource
{
    public ResourceNamespace AgentNamespace { get; init; } = ResourceNamespace.Default;
    public required Guid AgentUid { get; init; }
    public required string AgentName { get; init; }
    public required long AgentVersion { get; init; }
    public required ResolvedAgentDefinition Definition { get; init; }
    public required string DefinitionHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required ProvisioningState ProvisioningState { get; init; }
}

public sealed record ResolvedAgentDefinition
{
    public required Guid AgentId { get; init; }
    public required string AgentKey { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required long AgentVersion { get; init; }
    public required string EffectiveInstructions { get; init; }
    public required string ModelProfileName { get; init; }
    public ResourceNamespace? ModelProfileNamespace { get; init; }
    public required string RuntimeProfileName { get; init; }
    public ResourceNamespace RuntimeProfileNamespace { get; init; } = ResourceNamespace.Default;
    public required IReadOnlyCollection<string> EffectiveToolNames { get; init; }
    public required IReadOnlyCollection<string> MiddlewareIds { get; init; }
    public required IReadOnlyCollection<string> ContextProviderIds { get; init; }
    public required IReadOnlyCollection<string> Capabilities { get; init; }
    public required string Handler { get; init; }
    public required string DefinitionHash { get; init; }
}

public sealed record AgentDeployment : Resource
{
    public ResourceNamespace AgentNamespace { get; init; } = ResourceNamespace.Default;
    public required string RevisionName { get; init; }
    public string? AgentName { get; init; }
    public string? ModelProfileName { get; init; }
    public ResourceNamespace? ModelProfileNamespace { get; init; }
    public required string Environment { get; init; }
    public required string RuntimeProfileName { get; init; }
    public ResourceNamespace RuntimeProfileNamespace { get; init; } = ResourceNamespace.Default;
    public required AgentHostingMode HostingMode { get; init; }
    public required DesiredAgentState DesiredState { get; init; }
    public required ProvisioningState ProvisioningState { get; init; }
    public required OperationalState OperationalState { get; init; }
    public string? ObservedRevisionName { get; init; }
    public IReadOnlyDictionary<string, int> TrafficWeights { get; init; } = new Dictionary<string, int>();
    public string? LastError { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ManagementOperation : Resource
{
    public required ResourceKey Target { get; init; }
    public required string OperationType { get; init; }
    public required OperationStatus OperationStatus { get; init; }
    public int? PercentComplete { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
