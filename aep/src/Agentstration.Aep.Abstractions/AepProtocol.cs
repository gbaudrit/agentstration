using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Aep.Abstractions;

public static class AepProtocol
{
    public const string Version = "2026-09-22";
    public const string DiscoveryPath = "/.well-known/aep";
    public const string LegacyDiscoveryPath = "/.well-known/agentstration";
    public const string HealthPath = "/aep/health";
    public const string ModelProvidersPath = "/aep/model-providers";
    public const string SourceProvidersPath = "/aep/source-providers";
    public const string ConfigurationPath = "/aep/configuration";
    public const string ConfigurationMigrationPath = "/aep/configuration/migrate";
    public const string ValueRequirementsCapabilityVersion = "1.0";
    public const string BoundValuesCapabilityVersion = "1.0";
    public const string SecretAccessVersion = "1.0";
    public const string ModelProviderCapabilityVersion = "1.0";
    public const string SecretAccessPath = "/api/aep/secrets/redeem";

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
    public const string SourceProvider = "aep.source-provider";
    public const string Tools = "aep.tools";
    public const string Configuration = "aep.configuration";
    public const string ValueRequirements = "aep.value-requirements";
    public const string BoundValues = "aep.bound-values";
    public const string SecretAccess = "aep.secret-access";
}

public sealed record AepExtensionIdentity(string Id, string Name, string Version, string? Description = null);

public sealed record AepCapabilityDescriptor(
    string Version,
    string? Endpoint = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public static class AepContributionKinds
{
    public const string ModelProvider = "model-provider";
    public const string SourceProvider = "source-provider";
    public const string Tool = "tool";
}

public static class AepOptionScopes
{
    public const string ModelProfile = "model-profile";
    public const string SourceChannel = "source-channel";
}

public sealed record AepOptionSetVersionDescriptor(
    string Version,
    string SchemaDigest,
    JsonElement Schema,
    bool Deprecated = false)
{
    public static AepOptionSetVersionDescriptor Create(string version, JsonElement schema, bool deprecated = false) =>
        new(version, AepSchemaDigest.Compute(schema), schema.Clone(), deprecated);
}

public sealed record AepOptionSetDescriptor(
    string Id,
    string ContributionKind,
    string ContributionId,
    string Scope,
    string PreferredVersion,
    IReadOnlyList<AepOptionSetVersionDescriptor> Versions,
    IReadOnlyList<AepOptionMigrationDescriptor>? Migrations = null);

public sealed record AepOptionMigrationDescriptor(string FromVersion, string ToVersion);

public sealed record AepConfigurationCatalog(IReadOnlyList<AepOptionSetDescriptor> OptionSets);

public sealed record AepVersionedOptions(
    string OptionSet,
    string Version,
    string SchemaDigest,
    JsonElement Values);

public sealed record AepOptionMigrationRequest(
    string OptionSet,
    string FromVersion,
    string FromSchemaDigest,
    string ToVersion,
    JsonElement Values);

public sealed record AepOptionMigrationResponse(AepVersionedOptions Options);

public interface IAepOptionMigrator
{
    string OptionSet { get; }
    string FromVersion { get; }
    string ToVersion { get; }
    ValueTask<JsonElement> MigrateAsync(JsonElement values, CancellationToken cancellationToken = default);
}

public static class AepSchemaDigest
{
    public static string Compute(JsonElement schema)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(schema, AepProtocol.JsonOptions);
        return $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    }
}

public sealed record AepOptionValidationIssue(string Path, string Code, string Message);

public static class AepOptionSchemaValidator
{
    public static IReadOnlyList<AepOptionValidationIssue> Validate(
        JsonElement value,
        JsonElement schema,
        string path = "values")
    {
        var issues = new List<AepOptionValidationIssue>();
        ValidateValue(value, schema, path, issues);
        return issues;
    }

    private static void ValidateValue(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<AepOptionValidationIssue> issues)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("enum", out var enumValues)
            && enumValues.ValueKind == JsonValueKind.Array
            && !enumValues.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            issues.Add(new(path, "enum", $"Value at '{path}' is not one of the supported values."));
            return;
        }

        var type = schema.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
            ? typeValue.GetString()
            : null;
        if (type is not null && !MatchesType(value, type))
        {
            issues.Add(new(path, "type", $"Value at '{path}' must be of type '{type}'."));
            return;
        }

        if (value.ValueKind == JsonValueKind.Object) ValidateObject(value, schema, path, issues);
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                ValidateValue(item, itemSchema, $"{path}[{index++}]", issues);
        }
        if (value.ValueKind == JsonValueKind.Number) ValidateNumber(value, schema, path, issues);
    }

    private static void ValidateObject(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<AepOptionValidationIssue> issues)
    {
        var properties = schema.TryGetProperty("properties", out var propertySchemas)
            && propertySchemas.ValueKind == JsonValueKind.Object
            ? propertySchemas
            : default;
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString();
                if (!string.IsNullOrWhiteSpace(name) && !value.TryGetProperty(name, out _))
                    issues.Add(new($"{path}.{name}", "required", $"Required option '{name}' is missing."));
            }
        }
        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateValue(property.Value, propertySchema, $"{path}.{property.Name}", issues);
                continue;
            }
            if (schema.TryGetProperty("additionalProperties", out var additional)
                && additional.ValueKind == JsonValueKind.False)
                issues.Add(new($"{path}.{property.Name}", "unknown", $"Option '{property.Name}' is not supported."));
        }
    }

    private static void ValidateNumber(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<AepOptionValidationIssue> issues)
    {
        var number = value.GetDouble();
        if (schema.TryGetProperty("minimum", out var minimum)
            && minimum.ValueKind == JsonValueKind.Number
            && number < minimum.GetDouble())
            issues.Add(new(path, "minimum", $"Value at '{path}' must be at least {minimum.GetRawText()}."));
        if (schema.TryGetProperty("maximum", out var maximum)
            && maximum.ValueKind == JsonValueKind.Number
            && number > maximum.GetDouble())
            issues.Add(new(path, "maximum", $"Value at '{path}' must be at most {maximum.GetRawText()}."));
        if (schema.TryGetProperty("exclusiveMinimum", out var exclusiveMinimum)
            && exclusiveMinimum.ValueKind == JsonValueKind.Number
            && number <= exclusiveMinimum.GetDouble())
            issues.Add(new(path, "exclusiveMinimum", $"Value at '{path}' must be greater than {exclusiveMinimum.GetRawText()}."));
    }

    private static bool MatchesType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };
}

public sealed record AepManifest(
    string ProtocolVersion,
    AepExtensionIdentity Extension,
    IReadOnlyDictionary<string, AepCapabilityDescriptor> Capabilities,
    AepContributions Contributions,
    AepMcpDescriptor? Mcp = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AepValueRequirement>? ValueRequirements = null);

public enum AepValueProtection { Standard, Secured }
public enum AepValueType
{
    [JsonStringEnumMemberName("string")] Text,
    [JsonStringEnumMemberName("integer")] WholeNumber,
    [JsonStringEnumMemberName("number")] DecimalNumber,
    [JsonStringEnumMemberName("boolean")] Logical
}

public sealed record AepValueRequirement(
    string ContributionKind,
    string ContributionId,
    string Id,
    [property: JsonRequired] bool Required,
    AepValueType Type = AepValueType.Text,
    AepValueProtection Protection = AepValueProtection.Standard,
    string? Format = null,
    string? Description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<JsonElement>? AllowedValues = null);

public enum AepBoundValueKind { Inline, SecretGrant }

public sealed record AepBoundValue(
    string RequirementId,
    AepBoundValueKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? InlineValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AepSecretAccessGrant? SecretGrant = null)
{
    public static AepBoundValue Inline(string requirementId, JsonElement value) =>
        new(requirementId, AepBoundValueKind.Inline, value.Clone());

    public static AepBoundValue Secured(string requirementId, AepSecretAccessGrant grant) =>
        new(requirementId, AepBoundValueKind.SecretGrant, SecretGrant: grant);

    public override string ToString() => "[REDACTED]";
}

public sealed record AepBoundValuesRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AepBoundValue>? BoundValues = null);

public sealed record AepBoundValueValidationIssue(string Code, string RequirementId);

public static class AepBoundValueValidator
{
    public const int MaximumBoundValues = 64;
    public const int MaximumInlineValueBytes = 65_536;
    public const int MaximumAllowedValues = 64;
    public const int MaximumAllowedValuesBytes = 65_536;

    public static IReadOnlyList<AepBoundValueValidationIssue> Validate(
        IReadOnlyList<AepBoundValue>? values,
        IReadOnlyList<AepValueRequirement>? requirements,
        string contributionKind,
        string contributionId,
        bool requireAll)
    {
        var issues = new List<AepBoundValueValidationIssue>();
        var applicable = (requirements ?? [])
            .Where(value => string.Equals(value.ContributionKind, contributionKind, StringComparison.Ordinal)
                && string.Equals(value.ContributionId, contributionId, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(value => value.Id, StringComparer.Ordinal);
        var supplied = values ?? [];
        if (supplied.Count > MaximumBoundValues)
            issues.Add(new("bound_values_too_many", string.Empty));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in supplied.Take(MaximumBoundValues))
        {
            if (value is null || string.IsNullOrWhiteSpace(value.RequirementId))
            {
                issues.Add(new("bound_value_invalid", string.Empty));
                continue;
            }
            if (!seen.Add(value.RequirementId))
                issues.Add(new("bound_value_duplicate", value.RequirementId));
            if (!applicable.TryGetValue(value.RequirementId, out var requirement))
            {
                issues.Add(new("bound_value_unknown", value.RequirementId));
                continue;
            }
            var validVariant = value.Kind switch
            {
                AepBoundValueKind.Inline => value.InlineValue is not null && value.SecretGrant is null,
                AepBoundValueKind.SecretGrant => value.InlineValue is null && value.SecretGrant is not null,
                _ => false
            };
            if (!validVariant)
            {
                issues.Add(new("bound_value_variant_invalid", value.RequirementId));
                continue;
            }
            if (value.Kind == AepBoundValueKind.Inline)
            {
                if (requirement.Protection == AepValueProtection.Secured)
                    issues.Add(new("bound_value_protection_invalid", value.RequirementId));
                else if (JsonSerializer.SerializeToUtf8Bytes(value.InlineValue!.Value, AepProtocol.JsonOptions).Length > MaximumInlineValueBytes)
                    issues.Add(new("bound_value_too_large", value.RequirementId));
                else if (!MatchesType(value.InlineValue.Value, requirement.Type))
                    issues.Add(new("bound_value_type_invalid", value.RequirementId));
                else if (requirement.AllowedValues is { } allowedValues && !Contains(allowedValues, value.InlineValue.Value))
                    issues.Add(new("bound_value_not_allowed", value.RequirementId));
            }
            else
            {
                if (!string.Equals(value.SecretGrant!.Version, AepProtocol.SecretAccessVersion, StringComparison.Ordinal)
                    || !string.Equals(value.SecretGrant.RequirementId, value.RequirementId, StringComparison.Ordinal))
                    issues.Add(new("bound_value_grant_invalid", value.RequirementId));
                else if (requirement.AllowedValues is not null)
                    issues.Add(new("bound_value_allowed_values_require_inline", value.RequirementId));
            }
        }
        if (requireAll)
        {
            foreach (var requirement in applicable.Values.Where(value => value.Required && !seen.Contains(value.Id)))
                issues.Add(new("bound_value_required", requirement.Id));
        }
        return issues;
    }

    internal static bool MatchesType(JsonElement value, AepValueType type) => type switch
    {
        AepValueType.Text => value.ValueKind == JsonValueKind.String,
        AepValueType.WholeNumber => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        AepValueType.DecimalNumber => value.ValueKind == JsonValueKind.Number,
        AepValueType.Logical => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => false
    };

    internal static bool Contains(IReadOnlyList<JsonElement> values, JsonElement candidate) =>
        values.Any(value => JsonElement.DeepEquals(value, candidate));
}

public sealed record AepSecretAccessGrant(
    string Version,
    Uri Endpoint,
    string ExtensionId,
    string RequirementId,
    string ExecutionId,
    string SecretCapability)
{
    public override string ToString() => "[REDACTED]";
}

public sealed record AepSecretAccessRequest(
    string Version,
    string ExtensionId,
    string RequirementId,
    string ExecutionId,
    string SecretCapability)
{
    public override string ToString() => "[REDACTED]";
}

public sealed record AepSecretAccessResponse(string Version, string SecretValueBase64)
{
    public override string ToString() => "[REDACTED]";
}

public sealed record AepHealth(string Status, string? Details = null);

public sealed record AepContributions(
    IReadOnlyList<AepModelProviderDescriptor> ModelProviders,
    IReadOnlyList<AepToolContribution>? Tools = null,
    IReadOnlyList<AepSourceProviderDescriptor>? SourceProviders = null);

public sealed record AepSourceProviderDescriptor(
    string Id,
    string DisplayName,
    string? Description = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public sealed record AepContentIntegrity(string Algorithm, string Digest)
{
    public static AepContentIntegrity Sha256(ReadOnlySpan<byte> content) =>
        new("sha256", Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
}

public sealed record AepSourceResolveRequest(AepVersionedOptions Configuration);

public sealed record AepSourceResolveResponse(
    string Revision,
    AepContentIntegrity Integrity,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public sealed record AepSourceMaterializationLimits(
    long MaxArchiveBytes,
    int MaxEntries,
    long MaxExpandedBytes,
    int TimeoutSeconds);

public sealed record AepSourceMaterializeRequest(
    AepVersionedOptions Configuration,
    string Revision,
    AepSourceMaterializationLimits Limits);

public sealed record AepSourceArchive(
    string MediaType,
    byte[] Content,
    long ExpandedBytes,
    int EntryCount,
    AepContentIntegrity Integrity);

public sealed record AepSourceMaterializeResponse(string Revision, AepSourceArchive Archive);

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
    private static readonly SearchValues<char> ValueRequirementIdCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-");

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
        var sourceProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in descriptor.Contributions.SourceProviders ?? [])
        {
            if (string.IsNullOrWhiteSpace(provider.Id)) errors.Add("Source provider contribution id is required.");
            else if (!sourceProviders.Add(provider.Id)) errors.Add($"Source provider contribution '{provider.Id}' is duplicated.");
            if (string.IsNullOrWhiteSpace(provider.DisplayName)) errors.Add($"Source provider contribution '{provider.Id}' displayName is required.");
        }
        var requirements = descriptor.ValueRequirements ?? [];
        var hasCapability = descriptor.Capabilities.TryGetValue(AepCapabilityNames.ValueRequirements, out var valueCapability);
        if (requirements.Count > 0 && !hasCapability)
            errors.Add("Value requirements need the aep.value-requirements capability.");
        if (hasCapability && !string.Equals(valueCapability!.Version, AepProtocol.ValueRequirementsCapabilityVersion, StringComparison.Ordinal))
            errors.Add($"Value requirements capability version '{valueCapability.Version}' is not supported.");
        if (hasCapability && requirements.Count == 0)
            errors.Add("The aep.value-requirements capability needs at least one value requirement.");
        if (requirements.Count > 0
            && (!descriptor.Capabilities.TryGetValue(AepCapabilityNames.BoundValues, out var boundValuesCapability)
                || !string.Equals(boundValuesCapability.Version, AepProtocol.BoundValuesCapabilityVersion, StringComparison.Ordinal)))
            errors.Add("Value requirements need the aep.bound-values capability version 1.0.");
        if (requirements.Any(requirement => requirement?.Protection == AepValueProtection.Secured)
            && (!descriptor.Capabilities.TryGetValue(AepCapabilityNames.SecretAccess, out var secretAccessCapability)
                || !string.Equals(secretAccessCapability.Version, AepProtocol.SecretAccessVersion, StringComparison.Ordinal)))
            errors.Add("Secured value requirements need the aep.secret-access capability version 1.0.");
        var requirementIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in requirements)
        {
            if (requirement is null)
            {
                errors.Add("A value requirement cannot be null.");
                continue;
            }
            var contributionKey = $"{requirement.ContributionKind}\n{requirement.ContributionId}\n{requirement.Id}";
            if (!IsKnownContribution(descriptor.Contributions, requirement.ContributionKind, requirement.ContributionId))
                errors.Add($"Value requirement '{requirement.Id}' targets unknown contribution '{requirement.ContributionKind}/{requirement.ContributionId}'.");
            if (!IsValidValueRequirementId(requirement.Id))
                errors.Add($"Value requirement id '{requirement.Id}' must start with a lowercase ASCII letter and contain only ASCII letters, digits, '.', '_' or '-' (maximum 64 characters).");
            else if (!requirementIds.Add(contributionKey))
                errors.Add($"Value requirement '{requirement.Id}' is duplicated for contribution '{requirement.ContributionKind}/{requirement.ContributionId}'.");
            if (requirement.Format is { Length: > 64 } || requirement.Format?.Any(char.IsControl) == true)
                errors.Add($"Value requirement '{requirement.Id}' format must contain at most 64 printable characters.");
            if (requirement.Description is { Length: > 256 } || requirement.Description?.Any(char.IsControl) == true)
                errors.Add($"Value requirement '{requirement.Id}' description must contain at most 256 printable characters.");
            if (requirement.AllowedValues is { } allowedValues)
            {
                if (requirement.Protection == AepValueProtection.Secured)
                    errors.Add($"Secured Value requirement '{requirement.Id}' cannot declare allowed values.");
                if (allowedValues.Count == 0)
                    errors.Add($"Value requirement '{requirement.Id}' allowedValues must contain at least one value when present.");
                if (allowedValues.Count > AepBoundValueValidator.MaximumAllowedValues)
                    errors.Add($"Value requirement '{requirement.Id}' allowedValues may contain at most {AepBoundValueValidator.MaximumAllowedValues} values.");
                if (allowedValues.All(value => value.ValueKind != JsonValueKind.Undefined)
                    && JsonSerializer.SerializeToUtf8Bytes(allowedValues, AepProtocol.JsonOptions).Length > AepBoundValueValidator.MaximumAllowedValuesBytes)
                    errors.Add($"Value requirement '{requirement.Id}' allowedValues exceeds the maximum serialized size.");
                for (var index = 0; index < allowedValues.Count; index++)
                {
                    if (!AepBoundValueValidator.MatchesType(allowedValues[index], requirement.Type))
                        errors.Add($"Value requirement '{requirement.Id}' allowedValues contains a value that does not match its declared type.");
                    if (allowedValues.Take(index).Any(value => JsonElement.DeepEquals(value, allowedValues[index])))
                        errors.Add($"Value requirement '{requirement.Id}' allowedValues contains a duplicate value.");
                }
            }
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

    private static bool IsKnownContribution(AepContributions contributions, string kind, string id) =>
        string.Equals(kind, AepContributionKinds.ModelProvider, StringComparison.Ordinal)
            && contributions.ModelProviders.Any(value => string.Equals(value.Id, id, StringComparison.OrdinalIgnoreCase))
        || string.Equals(kind, AepContributionKinds.SourceProvider, StringComparison.Ordinal)
            && (contributions.SourceProviders ?? []).Any(value => string.Equals(value.Id, id, StringComparison.OrdinalIgnoreCase))
        || string.Equals(kind, AepContributionKinds.Tool, StringComparison.Ordinal)
            && (contributions.Tools ?? []).Any(value => string.Equals(value.Id, id, StringComparison.OrdinalIgnoreCase));

    private static bool IsValidValueRequirementId(string? id) =>
        id is { Length: >= 1 and <= 64 }
        && id[0] is >= 'a' and <= 'z'
        && id.AsSpan(1).IndexOfAnyExcept(ValueRequirementIdCharacters) < 0;
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
    AepModelSpecification? Specification = null,
    AepModelIdentity? Identity = null);

public sealed record AepModelIdentity(
    string? Publisher = null,
    string? Model = null,
    string? Version = null);

public enum AepModelContentType { Text, Image, Audio }
public enum AepModelFeatureSupport { Unknown, Unsupported, Native, Emulated, Partial }
public enum AepModelToolMode { Function, Parallel }
public enum AepModelStructuredOutputFormat { JsonObject, JsonSchema }
public enum AepModelReasoningEffort { None, Minimal, Low, Medium, High }

public record AepModelFeatureSpecification
{
    public AepModelFeatureSupport Support { get; init; } = AepModelFeatureSupport.Unknown;
}

public sealed record AepModelStreamingFeatureSpecification : AepModelFeatureSpecification;
public sealed record AepModelToolModeSpecification;

public sealed record AepModelToolsFeatureSpecification : AepModelFeatureSpecification
{
    public IReadOnlyDictionary<AepModelToolMode, AepModelToolModeSpecification> Modes { get; init; }
        = new Dictionary<AepModelToolMode, AepModelToolModeSpecification>();
}

public sealed record AepModelStructuredOutputFormatSpecification
{
    public bool? SupportsStrict { get; init; }
}

public sealed record AepModelStructuredOutputFeatureSpecification : AepModelFeatureSpecification
{
    public IReadOnlyDictionary<AepModelStructuredOutputFormat, AepModelStructuredOutputFormatSpecification> Formats { get; init; }
        = new Dictionary<AepModelStructuredOutputFormat, AepModelStructuredOutputFormatSpecification>();
}

public sealed record AepModelReasoningEffortSpecification;

public sealed record AepModelReasoningFeatureSpecification : AepModelFeatureSpecification
{
    public IReadOnlyDictionary<AepModelReasoningEffort, AepModelReasoningEffortSpecification> Efforts { get; init; }
        = new Dictionary<AepModelReasoningEffort, AepModelReasoningEffortSpecification>();
}

public sealed record AepModelFeatureSpecifications
{
    public AepModelStreamingFeatureSpecification? Streaming { get; init; }
    public AepModelToolsFeatureSpecification? Tools { get; init; }
    public AepModelStructuredOutputFeatureSpecification? StructuredOutput { get; init; }
    public AepModelReasoningFeatureSpecification? Reasoning { get; init; }
}

public sealed record AepModelLimits
{
    public long? ContextTokens { get; init; }
    public long? MaxOutputTokens { get; init; }
}

public sealed record AepModelSpecification
{
    public IReadOnlyList<AepModelContentType>? Input { get; init; }
    public IReadOnlyList<AepModelContentType>? Output { get; init; }
    public AepModelFeatureSpecifications Features { get; init; } = new();
    public AepModelLimits Limits { get; init; } = new();
}

public static class AepModelObservationValidator
{
    public const int MaximumModels = 1_000;
    public const int MaximumIdentifierLength = 256;
    public const int MaximumDisplayNameLength = 256;
    public const int MaximumIdentityPartLength = 128;
    public const long MaximumTokenLimit = 10_000_000_000;

    public static string? FindIssue(IReadOnlyList<AepModelDescriptor> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count > MaximumModels) return "The model observation exceeds the model count limit.";
        if (models.Any(model => model is null)) return "A model observation entry is required.";
        if (models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() != models.Count)
            return "Model observation identifiers must be unique.";
        foreach (var model in models)
        {
            if (!IsSafeRequired(model.Id, MaximumIdentifierLength)) return "A model observation identifier is invalid.";
            if (!IsSafeRequired(model.DisplayName, MaximumDisplayNameLength)) return "A model observation display name is invalid.";
            if (!IsSafeOptional(model.Identity?.Publisher, MaximumIdentityPartLength)
                || !IsSafeOptional(model.Identity?.Model, MaximumIdentityPartLength)
                || !IsSafeOptional(model.Identity?.Version, MaximumIdentityPartLength))
                return "A model observation identity is invalid.";
            if (FindSpecificationIssue(model.Specification) is { } issue) return issue;
        }
        return null;
    }

    public static string? FindSpecificationIssue(AepModelSpecification? specification)
    {
        if (specification is null) return null;
        if (specification.Features is null || specification.Limits is null)
            return "Model observation features and limits are required objects.";
        if (!IsDistinctAndBounded(specification.Input) || !IsDistinctAndBounded(specification.Output))
            return "Model observation content types must be unique and bounded.";
        if (specification.Limits.ContextTokens is <= 0 or > MaximumTokenLimit
            || specification.Limits.MaxOutputTokens is <= 0 or > MaximumTokenLimit)
            return "Model observation limits must be positive and bounded.";
        if (!IsSupport(specification.Features.Streaming)
            || !IsSupport(specification.Features.Tools)
            || !IsSupport(specification.Features.StructuredOutput)
            || !IsSupport(specification.Features.Reasoning))
            return "A model observation feature support value is invalid.";
        if (!IsBounded(specification.Features.Tools?.Modes)
            || !IsBounded(specification.Features.StructuredOutput?.Formats)
            || !IsBounded(specification.Features.Reasoning?.Efforts))
            return "Model observation feature details exceed their limit.";
        if (HasDetailsWhenUnsupported(specification.Features.Tools?.Support, specification.Features.Tools?.Modes?.Count)
            || HasDetailsWhenUnsupported(specification.Features.StructuredOutput?.Support, specification.Features.StructuredOutput?.Formats?.Count)
            || HasDetailsWhenUnsupported(specification.Features.Reasoning?.Support, specification.Features.Reasoning?.Efforts?.Count))
            return "An unsupported model feature cannot publish supported details.";
        return null;
    }

    private static bool IsSafeRequired(string? value, int maximum) =>
        value is { Length: > 0 } && value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);

    private static bool IsSafeOptional(string? value, int maximum) =>
        value is null || IsSafeRequired(value, maximum);

    private static bool IsDistinctAndBounded<T>(IReadOnlyList<T>? values) where T : struct, Enum =>
        values is null || values.Count <= 16 && values.Count == values.Distinct().Count()
            && values.All(Enum.IsDefined);

    private static bool IsBounded<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? values)
        where TKey : struct, Enum where TValue : class =>
        values is null || values.Count <= 16 && values.All(value => Enum.IsDefined(value.Key) && value.Value is not null);

    private static bool IsSupport(AepModelFeatureSpecification? feature) =>
        feature is null || Enum.IsDefined(feature.Support);

    private static bool HasDetailsWhenUnsupported(AepModelFeatureSupport? support, int? count) =>
        support == AepModelFeatureSupport.Unsupported && count > 0;
}

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
    public AepVersionedOptions? NativeOptions { get; init; }
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
    IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AepBoundValue>? BoundValues = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AepModelSpecification? EffectiveSpecification = null);

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
