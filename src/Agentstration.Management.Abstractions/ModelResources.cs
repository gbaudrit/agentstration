using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Management.Abstractions;

public sealed record ModelSelection
{
    public required string Name { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ExtensionRegistrationSource>))]
public enum ExtensionRegistrationSource
{
    [JsonStringEnumMemberName("manual")] Manual,
    [JsonStringEnumMemberName("configuration")] Configuration,
    [JsonStringEnumMemberName("aspire")] Aspire
}

public sealed record ModelProviderProperties
{
    public required string DisplayName { get; init; }
    public required ResourceReference Extension { get; init; }
    public required string ContributionId { get; init; }
}

public sealed record ModelProviderResource : Resource
{
    public ModelProviderProperties Definition { get; init; } = null!;
}

public sealed record ExtensionRegistrationProperties
{
    public required string DisplayName { get; init; }
    public required Uri Endpoint { get; init; }
    public bool Enabled { get; init; } = true;
    public string? ExpectedExtensionId { get; init; }
    public ExtensionRegistrationSource Source { get; init; } = ExtensionRegistrationSource.Manual;
    public ResourceReference? Credential { get; init; }
}

public sealed record ExtensionRegistrationResource : Resource
{
    public ExtensionRegistrationProperties Definition { get; init; } = null!;
}

[JsonConverter(typeof(JsonStringEnumConverter<SecretType>))]
public enum SecretType { [JsonStringEnumMemberName("opaque")] Opaque }

public sealed record VaultProperties
{
    public required string DisplayName { get; init; }
    public required string ProviderType { get; init; }
    public IReadOnlyDictionary<string, JsonElement> ProviderOptions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record VaultResource : Resource
{
    public VaultProperties Definition { get; init; } = null!;
}

public sealed record SecretProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required ResourceReference Vault { get; init; }
    public required string Key { get; init; }
    public SecretType SecretType { get; init; } = SecretType.Opaque;
}

public sealed record SecretResource : Resource
{
    public SecretProperties Definition { get; init; } = null!;
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

public sealed record VersionedExtensionOptions
{
    public string OptionSet { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string SchemaDigest { get; init; } = string.Empty;
    public JsonElement Values { get; init; }
    [System.Text.Json.Serialization.JsonExtensionData]
    public IDictionary<string, JsonElement>? LegacyValues { get; init; }
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
    public IReadOnlyDictionary<string, VersionedExtensionOptions> ProviderOptions { get; init; } = new Dictionary<string, VersionedExtensionOptions>();
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

