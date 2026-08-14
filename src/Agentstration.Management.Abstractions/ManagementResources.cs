using System.Text.Json;
using System.Text.Json.Serialization;

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
    public const string ModelProvider = "ModelProvider";
    public const string ModelProfile = "ModelProfile";
    public const string RuntimeProfile = "RuntimeProfile";
    public const string Tool = "Tool";
    public const string ToolProvider = "ToolProvider";
}

public static class PackKinds
{
    public const string Pack = "Pack";
}

public sealed record PackManifest
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public PackMetadata Metadata { get; init; } = new();
    public PackSpec Spec { get; init; } = new();
}

public sealed record PackMetadata
{
    public string Name { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record PackSpec
{
    public IReadOnlyList<string> Resources { get; init; } = [];
    public IReadOnlyList<PackRequirement> Requirements { get; init; } = [];
}

public sealed record PackRequirement
{
    public string? Capability { get; init; }
    public string? Pack { get; init; }
    public string? Version { get; init; }
}

public readonly record struct PackIdentity(string Publisher, string Name)
{
    public string ResourceName => $"{Publisher.Length}-{Publisher}-{Name}";
    public override string ToString() => $"{Publisher}/{Name}";
}

[JsonConverter(typeof(JsonStringEnumConverter<InstalledPackState>))]
public enum InstalledPackState
{
    [JsonStringEnumMemberName("installing")] Installing,
    [JsonStringEnumMemberName("installed")] Installed,
    [JsonStringEnumMemberName("uninstalling")] Uninstalling,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("degraded")] Degraded
}

public sealed record ManagedPackResource
{
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string VersionToken { get; init; }
}

public sealed record InstalledPackProperties
{
    public required string Publisher { get; init; }
    public required string PackName { get; init; }
    public required string Version { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public required string Source { get; init; }
    public required DateTimeOffset InstalledAt { get; init; }
    public InstalledPackState State { get; init; } = InstalledPackState.Installing;
    public IReadOnlyList<ManagedPackResource> ManagedResources { get; init; } = [];
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record InstalledPackResource : Resource
{
    public InstalledPackProperties Definition { get; init; } = null!;
}

public sealed record PackResourceDocument(
    string Path,
    string ApiVersion,
    string Kind,
    string Name,
    JsonElement Manifest);

public sealed record PackArchive(PackManifest Manifest, IReadOnlyList<PackResourceDocument> Resources, string Source);

public sealed record PackResourcePreview(string Path, string Kind, string Name, bool AlreadyExists);

public sealed record PackInstallationPreview(
    PackMetadata Metadata,
    IReadOnlyList<PackResourcePreview> Resources,
    bool AlreadyInstalled)
{
    public bool CanInstall => !AlreadyInstalled && Resources.All(resource => !resource.AlreadyExists);
}

public interface IPackArchiveReader
{
    Task<PackArchive> ReadAsync(Stream archive, string source, CancellationToken cancellationToken);
}

public interface IPackResourceHandler
{
    string Kind { get; }
    int InstallOrder { get; }
    Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken);
    Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, string packVersion, CancellationToken cancellationToken);
    Task<string?> GetVersionTokenAsync(string name, CancellationToken cancellationToken);
    Task DeleteAsync(ManagedPackResource resource, CancellationToken cancellationToken);
}

public sealed record ToolTypeReference(string Extension, string Id);

public enum ToolProviderType { Aep, Mcp }
public enum McpToolProviderTransport { Stdio, StreamableHttp }

public sealed record AepToolProviderConfiguration
{
    public required string ExtensionId { get; init; }
}

public sealed record McpToolProviderConfiguration
{
    public McpToolProviderTransport Transport { get; init; } = McpToolProviderTransport.Stdio;
    public Uri? Endpoint { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> EnvironmentReferences { get; init; } = new Dictionary<string, string>();
}

public sealed record ToolProviderDiscoveryState
{
    public DateTimeOffset? LastDiscoveryAt { get; init; }
    public string Status { get; init; } = "notDiscovered";
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int ToolCount { get; init; }
    public IReadOnlyDictionary<string, bool> Capabilities { get; init; } = new Dictionary<string, bool>();
    public IReadOnlyDictionary<string, string> ServerMetadata { get; init; } = new Dictionary<string, string>();
}

public sealed record ToolProviderProperties
{
    public required string DisplayName { get; init; }
    public required ToolProviderType ProviderType { get; init; }
    public bool Enabled { get; init; } = true;
    public AepToolProviderConfiguration? Aep { get; init; }
    public McpToolProviderConfiguration? Mcp { get; init; }
    public ToolProviderDiscoveryState Discovery { get; init; } = new();
}

public sealed record ToolProviderResource : Resource
{
    public ToolProviderProperties Definition { get; init; } = null!;
}

public sealed record ToolDiscoveryState
{
    public bool Discovered { get; init; } = true;
    public bool Available { get; init; }
    public required DateTimeOffset FirstSeenAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
}

public sealed record ToolSchema
{
    public required JsonElement Input { get; init; }
    public JsonElement? Output { get; init; }
}

public sealed record ToolResourceProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public ToolTypeReference? ToolType { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyDictionary<string, JsonElement> Metadata { get; init; } = new Dictionary<string, JsonElement>();
    public ResourceReference? Provider { get; init; }
    public string? ExternalId { get; init; }
    public ToolDiscoveryState? Discovery { get; init; }
    public ToolSchema? Schema { get; init; }
}

public sealed record ToolResource : Resource
{
    public ToolResourceProperties Definition { get; init; } = null!;
}

public sealed record DiscoveredToolDescriptor(
    string ExternalId,
    string DisplayName,
    string? Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    IReadOnlyDictionary<string, JsonElement> Metadata);

public sealed record ToolProviderDiscoveryResult(
    IReadOnlyCollection<DiscoveredToolDescriptor> Tools,
    IReadOnlyDictionary<string, bool> Capabilities,
    IReadOnlyDictionary<string, string> ServerMetadata);

public interface IToolProviderDiscovery
{
    bool Supports(ToolProviderType providerType);
    Task<ToolProviderDiscoveryResult> DiscoverAsync(ToolProviderResource provider, CancellationToken cancellationToken);
}

public sealed record ResourceMetadata
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Annotations { get; init; } = new Dictionary<string, string>();
}

public readonly record struct ResourceKey(string Kind, string Name)
{
    public static ResourceKey Create(string kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(kind, name);
    }

    public override string ToString() => $"{Kind}/{Name}";
}

public abstract record Resource
{
    public Guid Uid { get; init; }
    public required string ApiVersion { get; init; }
    public string Kind { get; init; } = string.Empty;
    public ResourceMetadata Metadata { get; init; } = new();
    [JsonIgnore] public string Name => Metadata.Name;
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
    public ResourceReference(string name, string? workspaceRef = null) { Name = name; WorkspaceRef = workspaceRef; }
    public string Name { get; init; }
    public string? WorkspaceRef { get; init; }
    [JsonIgnore] public string ResourceId => Name;
}

public record AgentProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string Handler { get; init; } = "prompt-agent";
    public required string Instructions { get; init; }
    public required ResourceReference ModelProfile { get; init; }
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
    public required string RuntimeProfileName { get; init; }
    public required IReadOnlyCollection<string> EffectiveToolNames { get; init; }
    public required IReadOnlyCollection<string> MiddlewareIds { get; init; }
    public required IReadOnlyCollection<string> ContextProviderIds { get; init; }
    public required IReadOnlyCollection<string> Capabilities { get; init; }
    public required string Handler { get; init; }
    public required string DefinitionHash { get; init; }
}

public sealed record AgentDeployment : Resource
{
    public required string RevisionName { get; init; }
    public string? AgentName { get; init; }
    public string? ModelProfileName { get; init; }
    public required string Environment { get; init; }
    public required string RuntimeProfileName { get; init; }
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

public sealed record ModelSelection
{
    public required string Name { get; init; }
}

public enum ModelProviderManagementMode { External, Aspire }

public sealed record ModelProviderProperties
{
    public required string DisplayName { get; init; }
    public required string ProviderType { get; init; }
    public required Uri Endpoint { get; init; }
    public ModelProviderManagementMode ManagementMode { get; init; } = ModelProviderManagementMode.External;
    public IReadOnlyDictionary<string, JsonElement> ProviderOptions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record ModelProviderResource : Resource
{
    public ModelProviderProperties Definition { get; init; } = null!;
}

public sealed record ModelGenerationOptions
{
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? TopK { get; init; }
    public int? MaxOutputTokens { get; init; }
    public int? Seed { get; init; }
    public IReadOnlyList<string>? StopSequences { get; init; }
}

public enum ReasoningMode { Automatic, Enabled, Disabled }
public enum ReasoningEffort { Minimal, Low, Medium, High }

public sealed record ModelReasoningOptions
{
    public ReasoningMode Mode { get; init; } = ReasoningMode.Automatic;
    public ReasoningEffort? Effort { get; init; }
}

public enum ModelOutputFormat { Text, JsonObject, JsonSchema }

public sealed record ModelOutputOptions
{
    public ModelOutputFormat Format { get; init; } = ModelOutputFormat.Text;
    public JsonElement? JsonSchema { get; init; }
    public bool Strict { get; init; }
}

public sealed record ModelProfileProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required ResourceReference Provider { get; init; }
    public required ModelSelection Model { get; init; }
    public ModelGenerationOptions Generation { get; init; } = new();
    public ModelReasoningOptions Reasoning { get; init; } = new();
    public ModelOutputOptions Output { get; init; } = new();
    public IReadOnlyDictionary<string, JsonElement> ProviderOptions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record ModelProfileResource : Resource
{
    public ModelProfileProperties Definition { get; init; } = null!;
}

public enum RuntimeSessionMode { Transient, Persistent }
public enum RuntimeToolInvocationMode { Automatic, Required, Disabled }
public enum StreamingMode { Automatic, Enabled, Disabled }

public sealed record RuntimeExecutionDefaults
{
    public RuntimeSessionMode SessionMode { get; init; } = RuntimeSessionMode.Transient;
    public RuntimeToolInvocationMode ToolInvocation { get; init; } = RuntimeToolInvocationMode.Automatic;
    public StreamingMode Streaming { get; init; } = StreamingMode.Automatic;
}

public sealed record RuntimeProfileProperties
{
    public required string DisplayName { get; init; }
    public required string RuntimeType { get; init; }
    public RuntimeExecutionDefaults Execution { get; init; } = new();
    public IReadOnlyDictionary<string, JsonElement> RuntimeOptions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record RuntimeProfileResource : Resource
{
    public RuntimeProfileProperties Definition { get; init; } = null!;
}

public sealed record ExternalBinding
{
    public required Guid DeploymentId { get; init; }
    public required string Provider { get; init; }
    public required string ExternalResourceId { get; init; }
    public string? ExternalVersionId { get; init; }
    public Uri? Endpoint { get; init; }
}
