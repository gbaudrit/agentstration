using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Console;

public enum ExtensionOptionFieldKind { Text, WholeNumber, DecimalNumber, Toggle, JsonObject, JsonValue }

public sealed class ExtensionOptionFieldEditor
{
    public required string Name { get; init; }
    public required ExtensionOptionFieldKind Kind { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
    public IReadOnlyList<string> AllowedValues { get; init; } = [];
    public string? Minimum { get; init; }
    public string? Maximum { get; init; }
    public string? Value { get; set; }
}

public sealed class ExtensionOptionsEditorModel
{
    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly JsonObject root;

    private ExtensionOptionsEditorModel(
        string providerType,
        ExtensionOptionSetResponse contract,
        ExtensionOptionSetVersionResponse version,
        JsonObject root,
        bool enabled,
        IReadOnlyList<ExtensionOptionFieldEditor> fields,
        string? unavailableReason)
    {
        ProviderType = providerType;
        Contract = contract;
        Version = version;
        this.root = root;
        Enabled = enabled;
        Fields = fields;
        UnavailableReason = unavailableReason;
    }

    public string ProviderType { get; }
    public ExtensionOptionSetResponse Contract { get; }
    public ExtensionOptionSetVersionResponse Version { get; }
    public bool Enabled { get; set; }
    public IReadOnlyList<ExtensionOptionFieldEditor> Fields { get; }
    public string? UnavailableReason { get; }
    public bool CanGuide => UnavailableReason is null;

    public static ExtensionOptionsEditorModel Create(
        string providerType,
        ExtensionOptionSetResponse contract,
        string? providerOptionsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerType);
        ArgumentNullException.ThrowIfNull(contract);
        JsonObject root;
        try { root = string.IsNullOrWhiteSpace(providerOptionsJson) ? [] : JsonNode.Parse(providerOptionsJson) as JsonObject ?? throw new JsonException(); }
        catch (JsonException)
        {
            var fallbackVersion = contract.Versions.Single(value => string.Equals(value.Version, contract.PreferredVersion, StringComparison.Ordinal));
            return new(providerType, contract, fallbackVersion, [], false, [], "The existing provider options are not a JSON object. Use raw JSON mode to repair them.");
        }

        var preferred = contract.Versions.Single(value => string.Equals(value.Version, contract.PreferredVersion, StringComparison.Ordinal));
        var hasExisting = root.TryGetPropertyValue(providerType, out var existingNode);
        if (hasExisting && existingNode is not JsonObject)
            return Unavailable(providerType, contract, preferred, root, "The persisted provider options are not a versioned option envelope. Migrate or edit the raw JSON explicitly.");

        var existing = existingNode as JsonObject;
        var enabled = existing is not null;
        string? versionName;
        string? optionSet;
        string? schemaDigest;
        try
        {
            versionName = existing?["version"]?.GetValue<string>() ?? contract.PreferredVersion;
            optionSet = existing?["optionSet"]?.GetValue<string>();
            schemaDigest = existing?["schemaDigest"]?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return Unavailable(providerType, contract, preferred, root, "The persisted provider option envelope has invalid metadata. Migrate or edit the raw JSON explicitly.");
        }

        var version = contract.Versions.SingleOrDefault(value => string.Equals(value.Version, versionName, StringComparison.Ordinal));
        if (existing is not null && (!string.Equals(optionSet, contract.Id, StringComparison.Ordinal)
            || version is null
            || !string.Equals(schemaDigest, version.SchemaDigest, StringComparison.Ordinal)))
        {
            return Unavailable(providerType, contract, version ?? preferred, root, "The persisted options do not match a currently published contract and schema digest. Migrate or edit the raw JSON explicitly.");
        }
        version ??= preferred;
        var values = existing?["values"] as JsonObject;
        if (existing is not null && values is null)
            return Unavailable(providerType, contract, version, root, "The persisted provider option values are not a JSON object. Migrate or edit the raw JSON explicitly.");
        if (!TryCreateFields(version.Schema, values, out var fields, out var reason))
            return Unavailable(providerType, contract, version, root, reason!);
        return new(providerType, contract, version, root, enabled, fields, null);
    }

    public string ToProviderOptionsJson()
    {
        if (!CanGuide) throw new ArgumentException(UnavailableReason);
        if (!Enabled)
        {
            root.Remove(ProviderType);
            return root.Count == 0 ? string.Empty : root.ToJsonString(IndentedJson);
        }
        var values = new JsonObject();
        foreach (var field in Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Value))
            {
                if (field.Required) throw new ArgumentException($"Provider option '{field.Name}' is required.");
                continue;
            }
            values[field.Name] = ParseValue(field);
        }
        root[ProviderType] = new JsonObject
        {
            ["optionSet"] = Contract.Id,
            ["version"] = Version.Version,
            ["schemaDigest"] = Version.SchemaDigest,
            ["values"] = values
        };
        return root.ToJsonString(IndentedJson);
    }

    private static bool TryCreateFields(
        JsonElement schema,
        JsonObject? values,
        out IReadOnlyList<ExtensionOptionFieldEditor> fields,
        out string? unavailableReason)
    {
        fields = [];
        unavailableReason = null;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            unavailableReason = "This option contract does not expose top-level object properties. Use raw JSON mode for this schema.";
            return false;
        }

        var propertyNames = properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (values is not null && values.Any(value => !propertyNames.Contains(value.Key)))
        {
            unavailableReason = "The persisted options contain values that this schema-driven editor cannot represent. Use raw JSON mode to preserve them.";
            return false;
        }

        var required = schema.TryGetProperty("required", out var requiredValue) && requiredValue.ValueKind == JsonValueKind.Array
            ? requiredValue.EnumerateArray().Select(value => value.GetString()).Where(value => value is not null).ToHashSet(StringComparer.Ordinal)
            : [];
        fields = properties.EnumerateObject().Select(property =>
        {
            var propertySchema = property.Value;
            var type = propertySchema.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            var kind = type switch
            {
                "string" => ExtensionOptionFieldKind.Text,
                "integer" => ExtensionOptionFieldKind.WholeNumber,
                "number" => ExtensionOptionFieldKind.DecimalNumber,
                "boolean" => ExtensionOptionFieldKind.Toggle,
                "object" => ExtensionOptionFieldKind.JsonObject,
                _ => ExtensionOptionFieldKind.JsonValue
            };
            var current = values?[property.Name];
            return new ExtensionOptionFieldEditor
            {
                Name = property.Name,
                Kind = kind,
                Description = propertySchema.TryGetProperty("description", out var description) ? description.GetString() : null,
                Required = required.Contains(property.Name),
                AllowedValues = propertySchema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array
                    ? allowed.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray()
                    : [],
                Minimum = NumberConstraint(propertySchema, "minimum") ?? NumberConstraint(propertySchema, "exclusiveMinimum"),
                Maximum = NumberConstraint(propertySchema, "maximum"),
                Value = DisplayValue(current, kind)
            };
        }).ToArray();
        return true;
    }

    private static ExtensionOptionsEditorModel Unavailable(
        string providerType,
        ExtensionOptionSetResponse contract,
        ExtensionOptionSetVersionResponse version,
        JsonObject root,
        string reason) => new(providerType, contract, version, root, root.ContainsKey(providerType), [], reason);

    private static JsonNode ParseValue(ExtensionOptionFieldEditor field)
    {
        try
        {
            return field.Kind switch
            {
                ExtensionOptionFieldKind.Text => JsonValue.Create(field.Value!)!,
                ExtensionOptionFieldKind.Toggle when bool.TryParse(field.Value, out var value) => JsonValue.Create(value),
                ExtensionOptionFieldKind.WholeNumber when long.TryParse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => JsonValue.Create(value),
                ExtensionOptionFieldKind.DecimalNumber => ParseNumber(field),
                ExtensionOptionFieldKind.JsonObject => ParseObject(field),
                ExtensionOptionFieldKind.JsonValue => JsonNode.Parse(field.Value!) ?? throw new ArgumentException($"Provider option '{field.Name}' cannot be null."),
                _ => throw new ArgumentException($"Provider option '{field.Name}' has an invalid value.")
            };
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Provider option '{field.Name}' must contain valid JSON.", exception);
        }
    }

    private static JsonNode ParseNumber(ExtensionOptionFieldEditor field)
    {
        var node = JsonNode.Parse(field.Value!);
        if (node is not JsonValue || !double.TryParse(field.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException($"Provider option '{field.Name}' must be a number.");
        return node;
    }

    private static JsonObject ParseObject(ExtensionOptionFieldEditor field) =>
        JsonNode.Parse(field.Value!) as JsonObject
        ?? throw new ArgumentException($"Provider option '{field.Name}' must be a JSON object.");

    private static string? DisplayValue(JsonNode? value, ExtensionOptionFieldKind kind) => value switch
    {
        null => null,
        JsonValue when kind == ExtensionOptionFieldKind.Text => value.GetValue<string>(),
        _ => value.ToJsonString(IndentedJson)
    };

    private static string? NumberConstraint(JsonElement schema, string name) =>
        schema.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
}
