using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Parameters;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Models;

public static class ModelResourceKinds
{
    public const string Model = "Model";
    public const string ModelProvider = "ModelProvider";
    public const string ModelProfile = "ModelProfile";
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelObservationState>))]
public enum ModelObservationState
{
    [JsonStringEnumMemberName("available")] Available,
    [JsonStringEnumMemberName("missing")] Missing,
    [JsonStringEnumMemberName("failed")] Failed
}

public sealed record ModelObservation
{
    public ModelObservationState State { get; init; } = ModelObservationState.Available;
    public required DateTimeOffset FirstObservedAt { get; init; }
    public required DateTimeOffset LastObservedAt { get; init; }
    public required DateTimeOffset LastAttemptedAt { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record ModelProperties
{
    public required string DisplayName { get; init; }
    public required ResourceReference Provider { get; init; }
    public required Guid ProviderUid { get; init; }
    public required string ExternalId { get; init; }
    public required string ProviderStatus { get; init; }
    public ModelIdentity? Identity { get; init; }
    public required ModelSpecification Specification { get; init; }
    public required ModelObservation Observation { get; init; }
}

public sealed record ModelResource : Resource
{
    public ModelProperties Definition { get; init; } = null!;
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
    public IReadOnlyList<ModelProviderValueBinding> ValueBindings { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelProviderValueBindingKind>))]
public enum ModelProviderValueBindingKind
{
    [JsonStringEnumMemberName("parameter")] Parameter,
    [JsonStringEnumMemberName("secret")] Secret
}

public sealed record ModelProviderValueBinding
{
    public required string RequirementId { get; init; }
    public required ModelProviderValueBindingKind Kind { get; init; }
    public ParameterReference? Parameter { get; init; }
    public SecretReference? Secret { get; init; }

    public static ModelProviderValueBinding FromParameter(string requirementId, ParameterReference parameter) =>
        new() { RequirementId = requirementId, Kind = ModelProviderValueBindingKind.Parameter, Parameter = parameter };

    public static ModelProviderValueBinding FromSecret(string requirementId, SecretReference secret) =>
        new() { RequirementId = requirementId, Kind = ModelProviderValueBindingKind.Secret, Secret = secret };

    public override string ToString() => $"{RequirementId}={Kind}:[REDACTED]";
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
    public IReadOnlyList<SecretBinding> SecretBindings { get; init; } = [];
}

public sealed record ModelProfileResource : Resource
{
    public ModelProfileProperties Definition { get; init; } = null!;
}
