using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Aep.Abstractions;

public static class AepProtocol
{
    public const string Version = "2026-08-01";
    public const string DiscoveryPath = "/.well-known/aep";
    public const string LegacyDiscoveryPath = "/.well-known/agentstration";
    public const string HealthPath = "/aep/health";
    public const string ModelProvidersPath = "/aep/model-providers";

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public static class AepCapabilityNames
{
    public const string Health = "aep.health";
    public const string ModelProvider = "aep.model-provider";
    public const string Tools = "aep.tools";
    public const string Configuration = "aep.configuration";
}

public sealed record AepExtensionIdentity(string Id, string Name, string Version, string? Description = null);

public sealed record AepCapabilityDescriptor(
    string Version,
    string? Endpoint = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public sealed record AepManifest(
    string ProtocolVersion,
    AepExtensionIdentity Extension,
    IReadOnlyDictionary<string, AepCapabilityDescriptor> Capabilities,
    AepContributions Contributions,
    AepMcpDescriptor? Mcp = null);

public sealed record AepHealth(string Status, string? Details = null);

public sealed record AepContributions(
    IReadOnlyList<AepModelProviderDescriptor> ModelProviders,
    IReadOnlyList<AepToolContribution>? Tools = null);

public sealed record AepMcpDescriptor(IReadOnlyList<AepMcpServerDescriptor> Servers);

public sealed record AepMcpServerDescriptor(string Id, string Endpoint);

public sealed record AepMcpToolMapping(string Server, string Tool);

public sealed record AepToolContribution(
    string Id,
    string DisplayName,
    AepMcpToolMapping Mcp,
    string? Description = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public static class AepDescriptorValidator
{
    public static IReadOnlyList<string> Validate(AepManifest descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var errors = new List<string>();
        var servers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in descriptor.Mcp?.Servers ?? [])
        {
            if (string.IsNullOrWhiteSpace(server.Id)) errors.Add("MCP server id is required.");
            else if (!servers.Add(server.Id)) errors.Add($"MCP server '{server.Id}' is duplicated.");
            if (!IsValidEndpoint(server.Endpoint)) errors.Add($"MCP server '{server.Id}' endpoint must be a relative URI or an absolute HTTP(S) URI.");
        }
        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in descriptor.Contributions.Tools ?? [])
        {
            if (string.IsNullOrWhiteSpace(tool.Id)) errors.Add("Tool contribution id is required.");
            else if (!tools.Add(tool.Id)) errors.Add($"Tool contribution '{tool.Id}' is duplicated.");
            if (string.IsNullOrWhiteSpace(tool.DisplayName)) errors.Add($"Tool contribution '{tool.Id}' displayName is required.");
            if (string.IsNullOrWhiteSpace(tool.Mcp.Tool)) errors.Add($"Tool contribution '{tool.Id}' MCP tool name is required.");
            if (string.IsNullOrWhiteSpace(tool.Mcp.Server) || !servers.Contains(tool.Mcp.Server))
                errors.Add($"Tool contribution '{tool.Id}' references unknown MCP server '{tool.Mcp.Server}'.");
        }
        return errors;
    }

    public static Uri ResolveMcpEndpoint(Uri extensionEndpoint, AepMcpServerDescriptor server)
    {
        ArgumentNullException.ThrowIfNull(extensionEndpoint);
        ArgumentNullException.ThrowIfNull(server);
        if (!Uri.TryCreate(server.Endpoint, UriKind.RelativeOrAbsolute, out var endpoint))
            throw new ArgumentException("The MCP endpoint is invalid.", nameof(server));
        if (endpoint.IsAbsoluteUri)
        {
            if (endpoint.Scheme is not ("http" or "https")) throw new ArgumentException("The MCP endpoint must use HTTP or HTTPS.", nameof(server));
            return endpoint;
        }
        var normalizedBase = new Uri(extensionEndpoint.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
        return new Uri(normalizedBase, endpoint);
    }

    private static bool IsValidEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.RelativeOrAbsolute, out var uri)) return false;
        return !uri.IsAbsoluteUri || uri.Scheme is "http" or "https";
    }
}

public sealed record AepModelProviderDescriptor(
    string Id,
    string DisplayName,
    AepModelProviderCapabilities Capabilities,
    IReadOnlyList<AepModelDescriptor>? Models = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public sealed record AepModelProviderCapabilities(
    bool Chat = true,
    bool Streaming = true,
    bool Tools = false,
    bool Thinking = false,
    bool StructuredOutput = false,
    bool Vision = false,
    bool ModelDiscovery = false);

public sealed record AepModelDescriptor(
    string Id,
    string DisplayName,
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record AepProviderHealth(string Status, string? Details = null);

public enum AepRole { System, User, Assistant, Tool }
public enum AepContentKind { Text, Image, File, Structured, ToolCall, ToolResult }
public enum AepFinishReason { Stop, Length, ToolCalls, ContentFilter, Error, Other }

public sealed record AepContent
{
    public required AepContentKind Kind { get; init; }
    public string? Text { get; init; }
    public string? MediaType { get; init; }
    public Uri? Uri { get; init; }
    public JsonElement? Data { get; init; }
    public AepToolCall? ToolCall { get; init; }
    public AepToolResult? ToolResult { get; init; }

    public static AepContent FromText(string text) => new() { Kind = AepContentKind.Text, Text = text };
}

public sealed record AepMessage(AepRole Role, IReadOnlyList<AepContent> Contents, string? AuthorName = null);

public sealed record AepModelOptions
{
    public float? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }
    public float? TopP { get; init; }
    public int? TopK { get; init; }
    public long? Seed { get; init; }
    public IReadOnlyList<string>? StopSequences { get; init; }
    public JsonElement? ResponseFormat { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? AdditionalOptions { get; init; }
}

public sealed record AepToolDefinition(string Name, string? Description, JsonElement Parameters);
public sealed record AepToolCall(string Id, string Name, JsonElement Arguments);
public sealed record AepToolResult(string CallId, JsonElement Result, bool IsError = false);

public sealed record AepChatRequest(
    string Model,
    IReadOnlyList<AepMessage> Messages,
    AepModelOptions? Options = null,
    IReadOnlyList<AepToolDefinition>? Tools = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public sealed record AepUsage(long? InputTokens = null, long? OutputTokens = null, long? TotalTokens = null);

public sealed record AepChatResponse(
    IReadOnlyList<AepMessage> Messages,
    string? Model = null,
    AepFinishReason? FinishReason = null,
    AepUsage? Usage = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public sealed record AepChatUpdate(
    IReadOnlyList<AepContent> Contents,
    AepRole? Role = null,
    string? Model = null,
    AepFinishReason? FinishReason = null,
    AepUsage? Usage = null);

public sealed record AepError(string Code, string Message, string? Target = null, IReadOnlyDictionary<string, string>? Details = null);
public sealed record AepErrorResponse(AepError Error);
