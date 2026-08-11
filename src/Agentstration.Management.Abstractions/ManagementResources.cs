using System.Text.Json;

namespace Agentstration.Management.Abstractions;

public static class AgentstrationProviderNamespaces
{
    public const string Agents = "Agentstration.Agents";
    public const string Models = "Agentstration.Models";
    public const string ModelProviders = "Agentstration.ModelProviders";
    public const string Tools = "Agentstration.Tools";
    public const string Runtime = "Agentstration.Runtime";
    public const string Memory = "Agentstration.Memory";
    public const string Identity = "Agentstration.Identity";
    public const string Integrations = "Agentstration.Integrations";
}

public static class AgentstrationResourceTypes
{
    public const string AgentTypes = AgentstrationProviderNamespaces.Agents + "/agentTypes";
    public const string Agents = AgentstrationProviderNamespaces.Agents + "/agents";
    public const string AgentRevisions = AgentstrationProviderNamespaces.Agents + "/agentRevisions";
    public const string Deployments = AgentstrationProviderNamespaces.Agents + "/deployments";
    public const string Operations = AgentstrationProviderNamespaces.Agents + "/operations";
    public const string ModelProviders = AgentstrationProviderNamespaces.ModelProviders + "/modelProviders";
    public const string ModelProfiles = AgentstrationProviderNamespaces.Models + "/modelProfiles";
    public const string RuntimeProfiles = AgentstrationProviderNamespaces.Runtime + "/runtimeProfiles";
    public const string Tools = AgentstrationProviderNamespaces.Tools + "/tools";
    public const string ToolProviders = AgentstrationProviderNamespaces.Tools + "/toolProviders";
    public const string McpServers = AgentstrationProviderNamespaces.Integrations + "/mcpServers";
}

public sealed record ToolTypeReference(string Extension, string Id);
public sealed record DirectMcpToolReference(ResourceReference Server, string Tool);

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
    public required ToolProviderProperties Properties { get; init; }
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
    public DirectMcpToolReference? Mcp { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyDictionary<string, JsonElement> Metadata { get; init; } = new Dictionary<string, JsonElement>();
    public ResourceReference? Provider { get; init; }
    public string? ExternalId { get; init; }
    public ToolDiscoveryState? Discovery { get; init; }
    public ToolSchema? Schema { get; init; }
}

public sealed record ToolResource : Resource
{
    public required ToolResourceProperties Properties { get; init; }
}

public sealed record McpServerProperties
{
    public required Uri Endpoint { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record McpServerResource : Resource
{
    public required McpServerProperties Properties { get; init; }
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

public static class ManagementApiVersions
{
    public const string V20260801 = "2026-08-01";
}

public readonly record struct ResourceIdentifier(string Value)
{
    public static ResourceIdentifier Create(Guid workspaceId, string resourceGroup, string providerNamespace, string resourceType, string name)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace ID cannot be empty.", nameof(workspaceId));
        var legacy = Create(resourceGroup, providerNamespace, resourceType, name);
        return new($"/workspaces/{workspaceId:D}{legacy.Value}");
    }

    public static ResourceIdentifier Create(string resourceGroup, string providerNamespace, string resourceType, string name)
    {
        ValidateSegment(resourceGroup, nameof(resourceGroup));
        ValidateSegment(providerNamespace, nameof(providerNamespace));
        ValidateSegment(resourceType, nameof(resourceType));
        ValidateSegment(name, nameof(name));
        return new($"/resourceGroups/{resourceGroup}/providers/{providerNamespace}/{resourceType}/{name}");
    }

    public static ResourceIdentifier Parse(string value)
    {
        if (!TryParse(value, out var identifier))
            throw new ArgumentException("The resource identifier must use '/resourceGroups/{group}/providers/{provider}/{type}/{name}'.", nameof(value));
        return identifier;
    }

    public static bool TryParse(string? value, out ResourceIdentifier identifier)
    {
        identifier = default;
        if (string.IsNullOrWhiteSpace(value) || value[0] != '/') return false;
        var canonicalSegments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var offset = 0;
        if (canonicalSegments.Length == 8
            && string.Equals(canonicalSegments[0], "workspaces", StringComparison.Ordinal)
            && Guid.TryParse(canonicalSegments[1], out _)) offset = 2;
        if (canonicalSegments.Length != offset + 6
            || !string.Equals(canonicalSegments[offset], "resourceGroups", StringComparison.Ordinal)
            || !string.Equals(canonicalSegments[offset + 2], "providers", StringComparison.Ordinal)) return false;
        try
        {
            identifier = offset == 0
                ? Create(canonicalSegments[1], canonicalSegments[3], canonicalSegments[4], canonicalSegments[5])
                : Create(Guid.Parse(canonicalSegments[1]), canonicalSegments[3], canonicalSegments[5], canonicalSegments[6], canonicalSegments[7]);
            return string.Equals(identifier.Value, value, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            identifier = default;
            return false;
        }
    }

    public Guid? WorkspaceId => Segments().Length == 8 ? Guid.Parse(Segments()[1]) : null;
    public string ResourceGroup => Segments()[Segments().Length == 8 ? 3 : 1];
    public string ProviderNamespace => Segments()[Segments().Length == 8 ? 5 : 3];
    public string ResourceType => Segments()[Segments().Length == 8 ? 6 : 4];
    public string Name => Segments()[Segments().Length == 8 ? 7 : 5];

    public override string ToString() => Value;

    private static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('/', StringComparison.Ordinal)) throw new ArgumentException("Resource identifier segments cannot contain '/'.", parameterName);
    }

    private string[] Segments() => Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
}

public abstract record Resource
{
    public string Id { get; init; } = string.Empty;
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string ApiVersion { get; init; }
    public string? ResourceGroup { get; init; }
    public Guid TenantId { get; init; }
    public Guid WorkspaceId { get; init; }
    public Guid ResourceGroupId { get; init; }
    public string? Location { get; init; }
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    public long Generation { get; init; }
    public ResourceStatus Status { get; init; } = new() { ProvisioningState = ProvisioningState.Accepted };
    public string? ETag { get; init; }
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

public sealed record AgentTypePolicy
{
    public bool AllowAdditionalInstructions { get; init; }
    public bool AllowModelOverride { get; init; }
    public bool AllowAdditionalTools { get; init; }
    public bool AllowToolRemoval { get; init; }
    public bool AllowMemoryOverride { get; init; }
    public bool AllowParameterOverrides { get; init; }
    public int MaximumAdditionalInstructionsLength { get; init; }
}

public sealed record AgentTypeDefinition
{
    public required string Key { get; init; }
    public required int Version { get; init; }
    public required string Handler { get; init; }
    public required string BaseInstructions { get; init; }
    public IReadOnlyCollection<string> RequiredToolIds { get; init; } = [];
    public IReadOnlyCollection<string> AllowedToolIds { get; init; } = [];
    public IReadOnlyCollection<string> BehaviorIds { get; init; } = [];
    public IReadOnlyCollection<string> MiddlewareIds { get; init; } = [];
    public IReadOnlyCollection<string> ContextProviderIds { get; init; } = [];
    public required string DefaultModelProfileId { get; init; }
    public required AgentTypePolicy Policy { get; init; }
}

public sealed record AgentTypeResource : Resource
{
    public required AgentTypeDefinition Properties { get; init; }
}

public sealed record AgentTypeReference(string ResourceId, int? Version = null);

public sealed record ResourceReference(string ResourceId);

public record AgentProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required AgentTypeReference AgentType { get; init; }
    public string? AdditionalInstructions { get; init; }
    public required ResourceReference ModelProfile { get; init; }
    public IReadOnlyList<ResourceReference> Tools { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> Settings { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record AgentResource : Resource
{
    public required AgentProperties Properties { get; init; }
}

public sealed record AgentDeploymentSpec
{
    public required string Environment { get; init; }
    public required string RuntimeProfileId { get; init; }
    public required AgentHostingMode HostingMode { get; init; }
}

public sealed record ResolvedAgentDefinition
{
    public required Guid AgentId { get; init; }
    public required string AgentKey { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required long AgentVersion { get; init; }
    public required string EffectiveInstructions { get; init; }
    public required string ModelProfileId { get; init; }
    public required string RuntimeProfileId { get; init; }
    public required IReadOnlyCollection<string> EffectiveToolIds { get; init; }
    public required IReadOnlyCollection<string> MiddlewareIds { get; init; }
    public required IReadOnlyCollection<string> ContextProviderIds { get; init; }
    public required IReadOnlyCollection<string> Capabilities { get; init; }
    public required string Handler { get; init; }
    public required string DefinitionHash { get; init; }
}

public sealed record ResolvedAgentSpec
{
    public required string AgentResourceId { get; init; }
    public required long Generation { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required string Instructions { get; init; }
    public required string AgentTypeResourceId { get; init; }
    public required string ModelProfileResourceId { get; init; }
    public required IReadOnlyList<string> ToolResourceIds { get; init; }
}

public enum AgentHostingMode { InProcess, SharedHost, DedicatedProcess, DedicatedContainer, RemoteEndpoint, FoundryHosted }
public enum ProvisioningState { Accepted, Validating, Creating, Updating, Succeeded, Failed, Deleting, Canceled }
public enum OperationalState { Starting, Ready, Degraded, Suspended, Stopped, Unavailable }
public enum DesiredAgentState { Running, Stopped }
public enum OperationStatus { Accepted, Running, Succeeded, Failed, Canceled }
public enum AgentIdentityType { None, SystemAssigned, UserAssigned, External }

public sealed record AgentRevision : Resource
{
    public required string AgentResourceId { get; init; }
    public required long AgentVersion { get; init; }
    public required int AgentTypeVersion { get; init; }
    public required ResolvedAgentDefinition Definition { get; init; }
    public required string DefinitionHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required ProvisioningState ProvisioningState { get; init; }
}

public sealed record AgentDeployment : Resource
{
    public required string RevisionId { get; init; }
    public string? AgentResourceId { get; init; }
    public string? ModelProfileId { get; init; }
    public required string Environment { get; init; }
    public required string RuntimeProfileId { get; init; }
    public required AgentHostingMode HostingMode { get; init; }
    public required DesiredAgentState DesiredState { get; init; }
    public required ProvisioningState ProvisioningState { get; init; }
    public required OperationalState OperationalState { get; init; }
    public string? ObservedRevisionId { get; init; }
    public IReadOnlyDictionary<string, int> TrafficWeights { get; init; } = new Dictionary<string, int>();
    public string? LastError { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ManagementOperation : Resource
{
    public required string ResourceId { get; init; }
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
    public required ModelProviderProperties Properties { get; init; }
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
    public required ModelProfileProperties Properties { get; init; }
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
    public required RuntimeProfileProperties Properties { get; init; }
}

public sealed record ExternalBinding
{
    public required Guid DeploymentId { get; init; }
    public required string Provider { get; init; }
    public required string ExternalResourceId { get; init; }
    public string? ExternalVersionId { get; init; }
    public Uri? Endpoint { get; init; }
}
