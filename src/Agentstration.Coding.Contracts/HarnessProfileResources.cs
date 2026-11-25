using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Coding.Contracts;

public static class HarnessResourceKinds
{
    public const string NativeConfiguration = "HarnessNativeConfiguration";
    public const string Profile = "HarnessProfile";
    public const string ProfileVersion = "HarnessProfileVersion";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CodexHarnessSettings(string Model, string SandboxMode = "read-only", string ApprovalPolicy = "on-request");

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HarnessNativeConfigurationDefinition(string Materializer, string MaterializerVersion, CodexHarnessSettings Settings);

public sealed record HarnessNativeConfigurationResource : Resource, IImmutableResource
{
    public required string ConfigurationName { get; init; }
    public required string Version { get; init; }
    public required HarnessNativeConfigurationDefinition Definition { get; init; }
    public required string ContentDigest { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HarnessNativeConfigurationReference(string Name, string Version, ResourceNamespace Namespace)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResourceScopeRef? ScopeRef { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HarnessProfileDefinition(string DisplayName, string Instructions, JsonElement BindingsSchema,
    HarnessNativeConfigurationReference NativeConfiguration)
{
    public string InstructionsPath { get; init; } = "AGENTS.md";
}
public sealed record HarnessProfileResource : Resource
{
    public required HarnessProfileDefinition Definition { get; init; }
}
public sealed record HarnessNativeConfigurationPin(ResourceScopeRef ScopeRef, ResourceNamespace Namespace, string Name,
    string Version, Guid Uid, long Generation, string ContentDigest, HarnessNativeConfigurationDefinition Definition);
public sealed record HarnessProfileVersionResource : Resource, IImmutableResource
{
    public required string ProfileName { get; init; }
    public required Guid ProfileUid { get; init; }
    public required long ProfileGeneration { get; init; }
    public required string Version { get; init; }
    public required HarnessProfileDefinition Definition { get; init; }
    public required HarnessNativeConfigurationPin NativeConfiguration { get; init; }
    public required string ContentDigest { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateHarnessNativeConfigurationRequest(string Name, ResourceNamespace Namespace, string Version, HarnessNativeConfigurationDefinition Definition);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PublishHarnessProfileRequest(string Version, string ExpectedETag);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HarnessExistingFile(string Path, string? Digest, string? OwnerProfile, string Kind = "file");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HarnessExportRequest(ResourceNamespace Namespace, string Name, string Version, string ExpectedDigest,
    IReadOnlyDictionary<string, string> Bindings, IReadOnlyList<HarnessExistingFile> ExistingFiles);
public sealed record HarnessExportFile(string Path, string Content, string Digest, string OwnerProfile);
public sealed record HarnessProvenance(string FormatVersion, ResourceNamespace Namespace, string Profile, string ProfileVersion,
    string ProfileDigest, string Materializer, string MaterializerVersion, string NativeConfiguration, string NativeConfigurationVersion,
    string NativeConfigurationDigest, IReadOnlyDictionary<string, string> FileDigests);
public sealed record HarnessExportPreview(HarnessProvenance Provenance, IReadOnlyList<HarnessExportFile> Files, string Digest,
    IReadOnlyList<string> UsageFields, IReadOnlyList<string> UnavailableUsageFields);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HarnessProfileUpdateRequest(HarnessProfileDefinition Definition, string ExpectedETag);
public sealed record HarnessExportArchive(string FileName, byte[] Content, string Digest, string FilesDigest);
