using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Aep.Abstractions;

public static class AepStorageProtocol
{
    public const string Capability = "aep.storage-types";
    public const string ContributionKind = "storage-type";
    public const string DocumentCapability = "aep.storage-documents";
    public const string Version = "1.0";
    public const string BindingPath = "/aep/storage/bindings";
    public const string BindingHeader = "X-Agentstration-Asp-Binding";
    public static string Digest(AepStorageTypeVersion version) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(version, AepProtocol.JsonOptions)));
}

public sealed record AepStorageRequirement(string Id, bool Required, AepValueType Type, AepValueProtection Protection, string? Format = null);
public sealed record AepStorageCapabilities(bool List, bool Stat, bool Read, bool Write,
    bool Delete = false, bool StableVersions = false, bool DurableRetention = false);
public sealed record AepStorageLimits(int MaximumPageSize, int MaximumReadBytes, int MaximumWriteBytes);
public sealed record AepStorageAspMapping(string Profile, string McpServer, string ToolPrefix,
    string ProviderId, string ProviderVersion, string BindingPath = AepStorageProtocol.BindingPath);
public sealed record AepStorageTypeVersion(string Version, string SchemaDigest, JsonElement ConfigurationSchema,
    string EnvelopeSchemaDigest, JsonElement EnvelopeSchema, bool RequiresConnectedService,
    IReadOnlyList<AepStorageRequirement> Requirements, AepStorageCapabilities Capabilities,
    AepStorageLimits Limits, AepStorageAspMapping Asp, bool Deprecated = false)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AepStorageDocumentCapabilities? Documents { get; init; }
}
public sealed record AepStorageQueryProfile(string Id, string Version, string Encoding);
public sealed record AepStorageDocumentCapabilities(bool Get, bool Put, bool Delete, bool Query,
    bool ImmutablePublications, IReadOnlyList<AepStorageQueryProfile> QueryProfiles);
public sealed record AepStorageTypeContribution(string Id, string DisplayName, string PreferredVersion,
    IReadOnlyList<AepStorageTypeVersion> Versions, string? Description = null);

/// <summary>Protected authenticated configuration exchange; never a content Tool's business arguments.</summary>
public sealed record AepStorageBindingRequest(string ContributionId, string Version, string Digest,
    JsonElement Store, IReadOnlyList<AepBoundValue> Connection, JsonElement Configuration, JsonElement Envelope,
    JsonElement DatasetRule, string AuthorizationRevision, DateTimeOffset ExpiresAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? DocumentCollections { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? DocumentSelection { get; init; }
    public override string ToString() => "[REDACTED]";
}
public sealed record AepStorageBindingLease(string Token, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "[REDACTED]";
}

public static class AepStorageDescriptorValidator
{
    public static IReadOnlyList<string> Validate(AepManifest manifest)
    {
        var errors = new List<string>();
        var types = manifest.Contributions.StorageTypes ?? [];
        var advertised = manifest.Capabilities.TryGetValue(AepStorageProtocol.Capability, out var capability);
        if (advertised != (types.Count > 0) || advertised && capability!.Version != AepStorageProtocol.Version)
            errors.Add("Storage type capability and contributions must agree on version 1.0.");
        if (types.Count > 32 || types.Any(t => t is null))
            return ["Storage contributions must contain at most 32 non-null types."];
        if (types.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != types.Count)
            errors.Add("At most 32 unique Storage types are supported.");
        var requirements = types.SelectMany(t => t.Versions?.Take(32).OfType<AepStorageTypeVersion>() ?? [])
            .SelectMany(v => v.Requirements?.Take(64).OfType<AepStorageRequirement>() ?? []).ToArray();
        if (requirements.Length > 0 && (!manifest.Capabilities.TryGetValue(AepCapabilityNames.BoundValues, out var boundValues)
            || boundValues.Version != AepProtocol.BoundValuesCapabilityVersion))
            errors.Add("Storage requirements need the supported bound-values capability.");
        if (requirements.Any(r => r.Protection == AepValueProtection.Secured)
            && (!manifest.Capabilities.TryGetValue(AepCapabilityNames.SecretAccess, out var secretAccess)
                || secretAccess.Version != AepProtocol.SecretAccessVersion))
            errors.Add("Secured Storage requirements need the supported Secret access capability.");
        foreach (var type in types.Take(32))
        {
            if (!TypeId(type.Id) || string.IsNullOrWhiteSpace(type.DisplayName) || type.DisplayName.Length > 200
                || type.Versions is not { Count: > 0 and <= 32 }
                || type.Versions.Any(v => v is null)
                || type.Versions.Select(v => v.Version).Distinct(StringComparer.Ordinal).Count() != type.Versions.Count
                || !type.Versions.Any(v => v.Version == type.PreferredVersion))
            { errors.Add("Invalid Storage type identity or versions."); continue; }
            foreach (var version in type.Versions)
            {
                if (!Member(version.Version) || version.Version.Length > 64 || version.ConfigurationSchema.ValueKind != JsonValueKind.Object
                    || version.EnvelopeSchema.ValueKind != JsonValueKind.Object
                    || version.ConfigurationSchema.GetRawText().Length > 16384 || version.EnvelopeSchema.GetRawText().Length > 16384
                    || version.SchemaDigest != AepSchemaDigest.Compute(version.ConfigurationSchema)
                    || version.EnvelopeSchemaDigest != AepSchemaDigest.Compute(version.EnvelopeSchema))
                    errors.Add("Invalid Storage type schema or digest.");
                if (version.Requirements is null || version.Requirements.Count > 64
                    || version.Requirements.Any(r => r is null)
                    || version.Requirements.Any(r => !RequirementId(r.Id) || !Enum.IsDefined(r.Type) || !Enum.IsDefined(r.Protection) || r.Format?.Length > 128)
                    || version.Requirements.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != version.Requirements.Count
                    || !version.RequiresConnectedService && version.Requirements.Count != 0)
                    errors.Add("Invalid Storage requirements.");
                if (version.Asp is not { Profile: "asp.content/1" or "asp.documents/1" } mapping || mapping.ToolPrefix is not { Length: > 0 and <= 64 }
                    || !mapping.ToolPrefix.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
                    || !Member(mapping.ProviderId) || !Member(mapping.ProviderVersion)
                    || !Member(mapping.McpServer)
                    || mapping.BindingPath != AepStorageProtocol.BindingPath
                    || !(manifest.Mcp?.Servers.Any(s => s.Id == mapping.McpServer) ?? false))
                    errors.Add("Storage operations must map to a declared MCP server and the known ASP content or document profile.");
                if (version.Documents is { } documents)
                {
                    if (version.Asp.Profile != "asp.documents/1"
                        || !manifest.Capabilities.TryGetValue(AepStorageProtocol.DocumentCapability, out var documentCapability) || documentCapability.Version != "1.0"
                        || documents.QueryProfiles is not { Count: <= 8 } || documents.Query && documents.QueryProfiles.Count == 0
                        || documents.QueryProfiles.Any(p => string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 128 || !Member(p.Version) || string.IsNullOrWhiteSpace(p.Encoding) || p.Encoding.Length > 128)
                        || documents.QueryProfiles.Select(p => (p.Id, p.Version, p.Encoding)).Distinct().Count() != documents.QueryProfiles.Count)
                        errors.Add("Document storage requires an explicit versioned document capability and bounded query profiles.");
                }
                else if (version.Asp.Profile == "asp.documents/1") errors.Add("Document mappings require document capability declarations.");
                if (version.Capabilities is null || version.Capabilities.Delete || version.Capabilities.StableVersions || version.Capabilities.DurableRetention
                    || version.Limits is null || version.Limits.MaximumPageSize is < 1 or > 100
                    || version.Limits.MaximumReadBytes is < 1 or > 65536 || version.Limits.MaximumWriteBytes is < 1 or > 65536)
                    errors.Add("Storage capabilities or limits exceed the implemented ASP content profile.");
            }
        }
        return errors;
    }
    private static bool Member(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    private static bool TypeId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 128
        && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    private static bool RequirementId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 64
        && value[0] is >= 'a' and <= 'z' && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
