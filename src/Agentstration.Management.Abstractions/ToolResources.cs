using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Management.Abstractions;

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
    public bool RequiresApproval { get; init; }
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

public static class ToolExecutionHookHandlers
{
    public const string Deny = "deny";
}

public sealed record ToolExecutionHookSelector
{
    public IReadOnlyList<string> Tools { get; init; } = [];
    public IReadOnlyList<string> Providers { get; init; } = [];
    public IReadOnlyList<string> Agents { get; init; } = [];
}

public sealed record ToolExecutionHookProperties
{
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; } = true;
    public int Order { get; init; }
    public required string Handler { get; init; }
    public ToolExecutionHookSelector Selector { get; init; } = new();
    public IReadOnlyDictionary<string, JsonElement> Configuration { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record ToolExecutionHookResource : Resource
{
    public ToolExecutionHookProperties Definition { get; init; } = null!;
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

