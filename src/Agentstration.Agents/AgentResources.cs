using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Agents;

public static class AgentResourceKinds
{
    public const string Agent = "Agent";
    public const string AgentRevision = "AgentRevision";
    public const string AgentDeployment = "AgentDeployment";
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
public enum OperationalState { Starting, Ready, Degraded, Suspended, Stopped, Unavailable }
public enum DesiredAgentState { Running, Stopped }
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
