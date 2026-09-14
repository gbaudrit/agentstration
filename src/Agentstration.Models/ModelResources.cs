using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Models;

public static class ModelResourceKinds
{
    public const string ModelProvider = "ModelProvider";
    public const string ModelProfile = "ModelProfile";
}

public sealed record ModelSelection
{
    public required string Name { get; init; }
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
