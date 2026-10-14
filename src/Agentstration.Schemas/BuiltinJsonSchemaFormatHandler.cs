using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentstration.Schemas;

public sealed partial class BuiltinJsonSchemaFormatHandler : ISchemaFormatHandler
{
    public const string ProviderId = "builtin/json-schema";
    public const string ProviderVersion = "1.0";
    public const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";
    private const int MaximumDepth = 16;
    private const int MaximumIssues = 100;

    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "title", "description", "type", "properties", "required", "additionalProperties",
        "items", "enum", "default", "examples", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "minLength", "maxLength", "minItems", "maxItems", "pattern", "format"
    };

    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "object", "array", "string", "integer", "number", "boolean", "null"
    };

    public SchemaFormatDescriptor Descriptor { get; } = new(
        SchemaFormatIds.JsonSchema,
        "JSON Schema",
        new(ProviderId, ProviderVersion),
        [Draft202012],
        SchemaFormatCapabilities.ValidateDefinition | SchemaFormatCapabilities.ValidateInstance
            | SchemaFormatCapabilities.VisualAuthoring | SchemaFormatCapabilities.InstancePreview,
        IsDefault: true);

    public ValueTask<SchemaFormatValidationResult> ValidateDefinitionAsync(JsonElement definition, string? dialect,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<SchemaFormatValidationIssue>();
        ValidateDialect(definition, dialect, issues);
        ValidateSchema(definition, "$", 0, issues);
        return ValueTask.FromResult(new SchemaFormatValidationResult(issues));
    }

    public async ValueTask<SchemaFormatValidationResult> ValidateInstanceAsync(JsonElement definition,
        JsonElement instance, string? dialect, CancellationToken cancellationToken)
    {
        var definitionResult = await ValidateDefinitionAsync(definition, dialect, cancellationToken);
        if (!definitionResult.IsValid) return definitionResult;
        var issues = new List<SchemaFormatValidationIssue>();
        ValidateValue(definition, instance, "$", 0, issues);
        return new(issues);
    }

    private static void ValidateDialect(JsonElement definition, string? declaredDialect,
        ICollection<SchemaFormatValidationIssue> issues)
    {
        var documentDialect = definition.ValueKind == JsonValueKind.Object
            && definition.TryGetProperty("$schema", out var schemaValue)
            && schemaValue.ValueKind == JsonValueKind.String
                ? schemaValue.GetString()
                : null;
        var dialect = declaredDialect ?? documentDialect ?? Draft202012;
        if (!string.Equals(dialect, Draft202012, StringComparison.Ordinal))
            Add(issues, "$.$schema", "json_schema_dialect_unsupported",
                $"JSON Schema dialect '{dialect}' is not supported; use '{Draft202012}'.");
        if (declaredDialect is not null && documentDialect is not null
            && !string.Equals(declaredDialect, documentDialect, StringComparison.Ordinal))
            Add(issues, "$.$schema", "json_schema_dialect_mismatch",
                "The resource dialect and the document $schema value must match.");
    }

    private static void ValidateSchema(JsonElement schema, string path, int depth,
        ICollection<SchemaFormatValidationIssue> issues)
    {
        if (issues.Count >= MaximumIssues) return;
        if (depth > MaximumDepth)
        {
            Add(issues, path, "json_schema_too_deep", $"Schemas cannot exceed {MaximumDepth} nested levels.");
            return;
        }
        if (schema.ValueKind != JsonValueKind.Object)
        {
            Add(issues, path, "json_schema_object_required", "A supported JSON Schema definition must be an object.");
            return;
        }

        foreach (var property in schema.EnumerateObject())
            if (!SupportedKeywords.Contains(property.Name)
                && !property.Name.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
                Add(issues, PropertyPath(path, property.Name), "json_schema_keyword_unsupported",
                    $"Keyword '{property.Name}' is not supported by the built-in JSON Schema handler.");

        _ = ReadType(schema, path, issues);
        ValidateText(schema, "$schema", path, issues);
        ValidateText(schema, "$id", path, issues);
        ValidateText(schema, "title", path, issues);
        ValidateText(schema, "description", path, issues);
        ValidateText(schema, "pattern", path, issues);
        ValidateText(schema, "format", path, issues);
        ValidateNonNegativeInteger(schema, "minLength", path, issues);
        ValidateNonNegativeInteger(schema, "maxLength", path, issues);
        ValidateNonNegativeInteger(schema, "minItems", path, issues);
        ValidateNonNegativeInteger(schema, "maxItems", path, issues);
        ValidateNumber(schema, "minimum", path, issues);
        ValidateNumber(schema, "maximum", path, issues);
        ValidateNumber(schema, "exclusiveMinimum", path, issues);
        ValidateNumber(schema, "exclusiveMaximum", path, issues);

        if (schema.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String
            && !string.Equals(format.GetString(), "uri", StringComparison.Ordinal))
            Add(issues, PropertyPath(path, "format"), "json_schema_format_unsupported",
                $"Format '{format.GetString()}' is not supported by the built-in handler.");

        if (schema.TryGetProperty("enum", out var enumeration)
            && (enumeration.ValueKind != JsonValueKind.Array || enumeration.GetArrayLength() == 0))
            Add(issues, PropertyPath(path, "enum"), "json_schema_enum_invalid", "The enum keyword must be a non-empty array.");
        if (schema.TryGetProperty("examples", out var examples) && examples.ValueKind != JsonValueKind.Array)
            Add(issues, PropertyPath(path, "examples"), "json_schema_examples_invalid", "The examples keyword must be an array.");

        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array
                || required.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                Add(issues, PropertyPath(path, "required"), "json_schema_required_invalid",
                    "The required keyword must be an array of property names.");
            else if (required.EnumerateArray().Select(value => value.GetString()).Distinct(StringComparer.Ordinal).Count()
                != required.GetArrayLength())
                Add(issues, PropertyPath(path, "required"), "json_schema_required_duplicate",
                    "The required keyword cannot contain duplicate property names.");
        }

        if (schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            Add(issues, PropertyPath(path, "additionalProperties"), "json_schema_additional_properties_unsupported",
                "Only boolean additionalProperties values are supported.");

        if (schema.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
                Add(issues, PropertyPath(path, "properties"), "json_schema_properties_invalid",
                    "The properties keyword must be an object.");
            else
                foreach (var property in properties.EnumerateObject())
                    ValidateSchema(property.Value, PropertyPath(PropertyPath(path, "properties"), property.Name),
                        depth + 1, issues);
        }

        if (schema.TryGetProperty("items", out var items))
            ValidateSchema(items, PropertyPath(path, "items"), depth + 1, issues);

        ValidateRange(schema, "minLength", "maxLength", path, issues);
        ValidateRange(schema, "minItems", "maxItems", path, issues);
        ValidateRange(schema, "minimum", "maximum", path, issues);
    }

    private static void ValidateValue(JsonElement schema, JsonElement value, string path, int depth,
        ICollection<SchemaFormatValidationIssue> issues)
    {
        if (issues.Count >= MaximumIssues || depth > MaximumDepth) return;
        var type = schema.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
            ? typeValue.GetString()
            : schema.TryGetProperty("properties", out _) ? "object" : null;
        if (type is not null && !Matches(value, type))
        {
            Add(issues, path, "json_schema_instance_type", $"The value must be of type '{type}'.");
            return;
        }
        if (schema.TryGetProperty("enum", out var enumeration) && enumeration.ValueKind == JsonValueKind.Array
            && !enumeration.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
            Add(issues, path, "json_schema_instance_enum", "The value is not one of the allowed enum values.");

        if (value.ValueKind == JsonValueKind.Object)
            ValidateObject(schema, value, path, depth, issues);
        else if (value.ValueKind == JsonValueKind.Array)
            ValidateArray(schema, value, path, depth, issues);
        else if (value.ValueKind == JsonValueKind.String)
            ValidateString(schema, value.GetString()!, path, issues);
        else if (value.ValueKind == JsonValueKind.Number)
            ValidateNumericValue(schema, value, path, issues);
    }

    private static void ValidateObject(JsonElement schema, JsonElement value, string path, int depth,
        ICollection<SchemaFormatValidationIssue> issues)
    {
        var required = schema.TryGetProperty("required", out var requiredValue)
            ? requiredValue.EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        var properties = schema.TryGetProperty("properties", out var propertiesValue)
            ? propertiesValue.EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in required)
            if (!value.TryGetProperty(name, out _))
                Add(issues, PropertyPath(path, name), "json_schema_instance_required", $"Property '{name}' is required.");
        var rejectAdditional = schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind == JsonValueKind.False;
        foreach (var property in value.EnumerateObject())
        {
            if (properties.TryGetValue(property.Name, out var propertySchema))
                ValidateValue(propertySchema, property.Value, PropertyPath(path, property.Name), depth + 1, issues);
            else if (rejectAdditional)
                Add(issues, PropertyPath(path, property.Name), "json_schema_instance_additional_property",
                    $"Property '{property.Name}' is not allowed.");
        }
    }

    private static void ValidateArray(JsonElement schema, JsonElement value, string path, int depth,
        ICollection<SchemaFormatValidationIssue> issues)
    {
        ValidateCount(value.GetArrayLength(), Integer(schema, "minItems"), Integer(schema, "maxItems"), path,
            "json_schema_instance_items", "items", issues);
        if (!schema.TryGetProperty("items", out var items)) return;
        var index = 0;
        foreach (var item in value.EnumerateArray())
            ValidateValue(items, item, $"{path}[{index++}]", depth + 1, issues);
    }

    private static void ValidateString(JsonElement schema, string value, string path,
        ICollection<SchemaFormatValidationIssue> issues)
    {
        ValidateCount(value.Length, Integer(schema, "minLength"), Integer(schema, "maxLength"), path,
            "json_schema_instance_length", "characters", issues);
        if (Text(schema, "pattern") is { } pattern)
        {
            try
            {
                if (!Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    Add(issues, path, "json_schema_instance_pattern", "The value does not match the required pattern.");
            }
            catch (ArgumentException)
            {
                Add(issues, path, "json_schema_pattern_invalid", "The schema pattern is invalid.");
            }
            catch (RegexMatchTimeoutException)
            {
                Add(issues, path, "json_schema_instance_pattern_timeout", "Pattern validation timed out.");
            }
        }
        if (string.Equals(Text(schema, "format"), "uri", StringComparison.Ordinal)
            && !Uri.TryCreate(value, UriKind.Absolute, out _))
            Add(issues, path, "json_schema_instance_format", "The value must be an absolute URI.");
    }

    private static void ValidateNumericValue(JsonElement schema, JsonElement value, string path,
        ICollection<SchemaFormatValidationIssue> issues)
    {
        if (!value.TryGetDecimal(out var number)) return;
        if (Decimal(schema, "minimum") is { } minimum && number < minimum)
            Add(issues, path, "json_schema_instance_minimum", $"The value must be at least {Invariant(minimum)}.");
        if (Decimal(schema, "maximum") is { } maximum && number > maximum)
            Add(issues, path, "json_schema_instance_maximum", $"The value must be at most {Invariant(maximum)}.");
        if (Decimal(schema, "exclusiveMinimum") is { } exclusiveMinimum && number <= exclusiveMinimum)
            Add(issues, path, "json_schema_instance_exclusive_minimum",
                $"The value must be greater than {Invariant(exclusiveMinimum)}.");
        if (Decimal(schema, "exclusiveMaximum") is { } exclusiveMaximum && number >= exclusiveMaximum)
            Add(issues, path, "json_schema_instance_exclusive_maximum",
                $"The value must be less than {Invariant(exclusiveMaximum)}.");
    }

    private static string? ReadType(JsonElement schema, string path, ICollection<SchemaFormatValidationIssue> issues)
    {
        if (!schema.TryGetProperty("type", out var type)) return null;
        if (type.ValueKind != JsonValueKind.String || !SupportedTypes.Contains(type.GetString()!))
        {
            Add(issues, PropertyPath(path, "type"), "json_schema_type_unsupported",
                "The type keyword must contain one supported primitive type.");
            return null;
        }
        return type.GetString();
    }

    private static void ValidateText(JsonElement schema, string name, string path,
        ICollection<SchemaFormatValidationIssue> issues)
    {
        if (schema.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.String)
            Add(issues, PropertyPath(path, name), "json_schema_keyword_type", $"Keyword '{name}' must be a string.");
    }

    private static void ValidateNonNegativeInteger(JsonElement schema, string name, string path,
        ICollection<SchemaFormatValidationIssue> issues)
    {
        if (schema.TryGetProperty(name, out var value)
            && (!value.TryGetInt32(out var number) || number < 0))
            Add(issues, PropertyPath(path, name), "json_schema_keyword_type",
                $"Keyword '{name}' must be a non-negative integer.");
    }

    private static void ValidateNumber(JsonElement schema, string name, string path,
        ICollection<SchemaFormatValidationIssue> issues)
    {
        if (schema.TryGetProperty(name, out var value) && !value.TryGetDecimal(out _))
            Add(issues, PropertyPath(path, name), "json_schema_keyword_type", $"Keyword '{name}' must be a number.");
    }

    private static void ValidateRange(JsonElement schema, string minimumName, string maximumName, string path,
        ICollection<SchemaFormatValidationIssue> issues)
    {
        var minimum = Decimal(schema, minimumName);
        var maximum = Decimal(schema, maximumName);
        if (minimum is not null && maximum is not null && minimum > maximum)
            Add(issues, path, "json_schema_range_invalid",
                $"Keyword '{minimumName}' cannot exceed '{maximumName}'.");
    }

    private static void ValidateCount(int value, int? minimum, int? maximum, string path, string code, string unit,
        ICollection<SchemaFormatValidationIssue> issues)
    {
        if (minimum is { } min && value < min)
            Add(issues, path, code, $"The value must contain at least {min} {unit}.");
        if (maximum is { } max && value > max)
            Add(issues, path, code, $"The value must contain at most {max} {unit}.");
    }

    private static bool Matches(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static int? Integer(JsonElement schema, string name) => schema.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var result) ? result : null;
    private static decimal? Decimal(JsonElement schema, string name) => schema.TryGetProperty(name, out var value)
        && value.TryGetDecimal(out var result) ? result : null;
    private static string? Text(JsonElement schema, string name) => schema.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string PropertyPath(string path, string property) => $"{path}.{property}";
    private static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static void Add(ICollection<SchemaFormatValidationIssue> issues, string path, string code, string message)
    {
        if (issues.Count < MaximumIssues) issues.Add(new(path, code, message));
    }
}
