using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentstration.Web.Components.JsonSchema;

public sealed record JsonSchemaInputIssue(string Path, string Code, string? Constraint = null);

public sealed record JsonSchemaInputValidationState(
    bool IsValid,
    JsonElement? Value,
    IReadOnlyList<JsonSchemaInputIssue> Issues,
    bool UsesRawFallback,
    string? FallbackReason = null);

public enum JsonSchemaInputKind { Map, List, Text, WholeNumber, DecimalNumber, Toggle, NullValue, Union }

public sealed record JsonSchemaInputNode(
    string Path,
    string Name,
    JsonSchemaInputKind Kind,
    bool Required,
    string? Title,
    string? Description,
    JsonElement? Default,
    IReadOnlyList<JsonElement> Examples,
    IReadOnlyList<JsonElement> AllowedValues,
    IReadOnlyList<JsonSchemaInputNode> Properties,
    JsonSchemaInputNode? Items,
    bool AdditionalProperties,
    decimal? Minimum,
    decimal? Maximum,
    decimal? ExclusiveMinimum,
    decimal? ExclusiveMaximum,
    int? MinimumLength,
    int? MaximumLength,
    int? MinimumItems,
    int? MaximumItems,
    string? Pattern,
    string? Format,
    IReadOnlyList<JsonSchemaInputNode>? Variants = null,
    bool ExclusiveVariants = false);

internal sealed record JsonSchemaInputAnalysis(JsonSchemaInputNode? Root, string? FallbackReason)
{
    public bool CanRender => Root is not null && FallbackReason is null;
}

internal static class JsonSchemaInputModel
{
    public const int MaximumSchemaBytes = 65_536;
    public const int MaximumValueBytes = 65_536;
    public const int MaximumDepth = 8;
    public const int MaximumFields = 128;
    public const int MaximumRenderedItems = 100;

    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "title", "description", "type", "properties", "required", "additionalProperties",
        "items", "enum", "default", "examples", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "minLength", "maxLength", "minItems", "maxItems", "pattern", "format"
    };

    public static JsonSchemaInputAnalysis Analyze(JsonElement? schema, bool allowAnyRoot = false, bool required = true)
    {
        if (schema is null) return new(null, "MissingSchema");
        if (schema.Value.ValueKind == JsonValueKind.Undefined) return new(null, "InvalidSchema");
        if (Encoding.UTF8.GetByteCount(schema.Value.GetRawText()) > MaximumSchemaBytes) return new(null, "SchemaTooLarge");
        var fields = 0;
        try
        {
            if (allowAnyRoot && schema.Value.ValueKind == JsonValueKind.Object && !schema.Value.EnumerateObject().Any())
                return new(null, "FreeFormSchema");
            var root = Parse(schema.Value, "$", "root", required, 0, ref fields, allowAnyRoot);
            if (!allowAnyRoot && root.Kind != JsonSchemaInputKind.Map) return new(null, "ObjectSchemaRequired");
            if (root.Kind == JsonSchemaInputKind.Map && root.Properties.Count == 0 && root.AdditionalProperties)
                return new(allowAnyRoot ? root : null, "FreeFormSchema");
            if (allowAnyRoot && RequiresRaw(root)) return new(root, "FreeFormSchema");
            return new(root, null);
        }
        catch (NotSupportedException exception) { return new(null, exception.Message); }
        catch (InvalidOperationException) { return new(null, "InvalidSchema"); }
        catch (JsonException) { return new(null, "InvalidSchema"); }
    }

    private static bool RequiresRaw(JsonSchemaInputNode node) =>
        node.Kind == JsonSchemaInputKind.Map && node.Properties.Count == 0 && node.AdditionalProperties
        || node.Kind == JsonSchemaInputKind.List && node.Items is null
        || node.Properties.Any(RequiresRaw) || node.Items is not null && RequiresRaw(node.Items)
        || node.Variants is not null && node.Variants.Any(RequiresRaw);

    public static JsonObject ApplyDefaults(JsonObject value, JsonSchemaInputNode schema)
    {
        var result = (JsonObject)value.DeepClone();
        ApplyObjectDefaults(result, schema);
        return result;
    }

    public static JsonObject CreateTemplate(JsonSchemaInputNode schema)
    {
        var result = new JsonObject();
        foreach (var property in schema.Properties.Where(property => property.Required || property.Default is not null))
            result[property.Name] = TemplateValue(property);
        return result;
    }

    public static IReadOnlyList<JsonSchemaInputIssue> Validate(JsonElement value, JsonSchemaInputNode schema)
    {
        var issues = new List<JsonSchemaInputIssue>();
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumValueBytes)
            issues.Add(new("$", "ValueTooLarge", MaximumValueBytes.ToString(CultureInfo.InvariantCulture)));
        else
            ValidateNode(value, schema, issues);
        return issues;
    }

    public static JsonSchemaInputNode AtPath(JsonSchemaInputNode schema, string path) => schema with
    {
        Path = path,
        Properties = schema.Properties.Select(property => AtPath(property, $"{path}.{property.Name}")).ToArray(),
        Items = schema.Items is null ? null : AtPath(schema.Items, $"{path}[]"),
        Variants = schema.Variants?.Select(variant => AtPath(variant, path)).ToArray()
    };

    private static JsonSchemaInputNode Parse(JsonElement schema, string path, string name, bool required, int depth, ref int fields, bool allowUnions)
    {
        if (depth > MaximumDepth) throw new NotSupportedException("SchemaTooDeep");
        if (schema.ValueKind != JsonValueKind.Object) throw new NotSupportedException("InvalidSchema");
        if (allowUnions && schema.TryGetProperty("type", out var unionType) && unionType.ValueKind == JsonValueKind.Array)
        {
            var variants = new List<JsonSchemaInputNode>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var unionMember in unionType.EnumerateArray())
            {
                if (unionMember.ValueKind != JsonValueKind.String || !seen.Add(unionMember.GetString()!))
                    throw new NotSupportedException("InvalidSchema");
                var branch = JsonNode.Parse(schema.GetRawText())!.AsObject();
                branch["type"] = unionMember.GetString();
                if (++fields > MaximumFields) throw new NotSupportedException("TooManyFields");
                variants.Add(Parse(JsonSerializer.SerializeToElement(branch), path, name, required, depth + 1, ref fields, true));
            }
            return Union(schema, variants, false);
        }
        if (allowUnions && (schema.TryGetProperty("anyOf", out _) || schema.TryGetProperty("oneOf", out _)))
        {
            var exclusive = schema.TryGetProperty("oneOf", out var branches);
            if (exclusive && schema.TryGetProperty("anyOf", out _)) throw new NotSupportedException("UnsupportedSchema");
            if (!exclusive) branches = schema.GetProperty("anyOf");
            if (branches.ValueKind != JsonValueKind.Array) throw new NotSupportedException("InvalidSchema");
            foreach (var property in schema.EnumerateObject())
                if (property.Name is not ("anyOf" or "oneOf" or "$schema" or "$id" or "title" or "description" or "default" or "examples")
                    && !property.Name.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("UnsupportedSchema");
            var variants = new List<JsonSchemaInputNode>();
            foreach (var branch in branches.EnumerateArray())
            {
                if (++fields > MaximumFields) throw new NotSupportedException("TooManyFields");
                variants.Add(Parse(branch, path, name, required, depth + 1, ref fields, true));
            }
            return Union(schema, variants, exclusive);
        }
        foreach (var property in schema.EnumerateObject())
            if (!SupportedKeywords.Contains(property.Name) && !property.Name.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("UnsupportedSchema");
        if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            throw new NotSupportedException("UnsupportedSchema");
        var type = Text(schema, "type") ?? InferType(schema);
        var kind = ParseKind(type);
        var format = Text(schema, "format");
        if (format is not null && !string.Equals(format, "uri", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("UnsupportedSchema");
        var requiredNames = schema.TryGetProperty("required", out var requiredValue) && requiredValue.ValueKind == JsonValueKind.Array
            ? requiredValue.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        var properties = new List<JsonSchemaInputNode>();
        if (schema.TryGetProperty("properties", out var propertySchemas))
        {
            if (propertySchemas.ValueKind != JsonValueKind.Object) throw new NotSupportedException("InvalidSchema");
            foreach (var property in propertySchemas.EnumerateObject())
            {
                if (++fields > MaximumFields) throw new NotSupportedException("TooManyFields");
                properties.Add(Parse(property.Value, $"{path}.{property.Name}", property.Name, requiredNames.Contains(property.Name), depth + 1, ref fields, allowUnions));
            }
        }
        JsonSchemaInputNode? items = null;
        if (kind == JsonSchemaInputKind.List)
        {
            if (!schema.TryGetProperty("items", out var itemSchema) || itemSchema.ValueKind == JsonValueKind.True)
            {
                if (!allowUnions) throw new NotSupportedException("UnsupportedSchema");
            }
            else if (itemSchema.ValueKind != JsonValueKind.Object)
                throw new NotSupportedException("UnsupportedSchema");
            else
                items = Parse(itemSchema, $"{path}[]", "item", true, depth + 1, ref fields, allowUnions);
        }
        return new(path, name, kind, required, Text(schema, "title"), Text(schema, "description"), Clone(schema, "default"),
            Array(schema, "examples"), Array(schema, "enum"), properties, items,
            !schema.TryGetProperty("additionalProperties", out additional) || additional.ValueKind != JsonValueKind.False,
            Decimal(schema, "minimum"), Decimal(schema, "maximum"), Decimal(schema, "exclusiveMinimum"), Decimal(schema, "exclusiveMaximum"),
            Integer(schema, "minLength"), Integer(schema, "maxLength"), Integer(schema, "minItems"), Integer(schema, "maxItems"),
            Text(schema, "pattern"), format);
    }

    private static JsonSchemaInputNode Union(JsonElement schema, IReadOnlyList<JsonSchemaInputNode> variants, bool exclusive)
    {
        if (variants.Count == 0) throw new NotSupportedException("InvalidSchema");
        return variants[0] with
        {
            Kind = JsonSchemaInputKind.Union, Variants = variants, ExclusiveVariants = exclusive,
            Title = Text(schema, "title"), Description = Text(schema, "description"),
            Default = Clone(schema, "default"), Examples = Array(schema, "examples"), AllowedValues = []
        };
    }

    private static void ApplyObjectDefaults(JsonObject target, JsonSchemaInputNode schema)
    {
        foreach (var property in schema.Properties)
        {
            if (!target.ContainsKey(property.Name) && property.Default is { } defaultValue)
                target[property.Name] = JsonNode.Parse(defaultValue.GetRawText());
            if (target[property.Name] is JsonObject child && property.Kind == JsonSchemaInputKind.Map)
                ApplyObjectDefaults(child, property);
        }
    }

    public static JsonNode? TemplateValue(JsonSchemaInputNode schema)
    {
        if (schema.Default is { } defaultValue) return JsonNode.Parse(defaultValue.GetRawText());
        return schema.Kind switch
        {
            JsonSchemaInputKind.Union => TemplateValue(schema.Variants![0]),
            JsonSchemaInputKind.Map => CreateTemplate(schema),
            JsonSchemaInputKind.List => new JsonArray(),
            JsonSchemaInputKind.Text => JsonValue.Create(string.Empty),
            JsonSchemaInputKind.WholeNumber => JsonValue.Create(0),
            JsonSchemaInputKind.DecimalNumber => JsonValue.Create(0m),
            JsonSchemaInputKind.Toggle => JsonValue.Create(false),
            _ => null
        };
    }

    private static void ValidateNode(JsonElement value, JsonSchemaInputNode schema, ICollection<JsonSchemaInputIssue> issues)
    {
        if (schema.Kind == JsonSchemaInputKind.Union)
        {
            var variants = schema.Variants!;
            var results = variants.Select(variant =>
            {
                var branchIssues = new List<JsonSchemaInputIssue>();
                ValidateNode(value, variant, branchIssues);
                return branchIssues;
            }).ToArray();
            var valid = results.Count(result => result.Count == 0);
            if (valid > 0)
            {
                if (schema.ExclusiveVariants && valid != 1) issues.Add(new(schema.Path, "ExclusiveUnion"));
                return;
            }
            var relevant = results.Select((result, index) => (result, index))
                .Where(entry => Matches(value, variants[entry.index]))
                .OrderBy(entry => entry.result.Count).Select(entry => entry.result).FirstOrDefault();
            if (relevant is not null) foreach (var issue in relevant) issues.Add(issue);
            else issues.Add(new(schema.Path, "Union"));
            return;
        }
        if (!Matches(value, schema.Kind)) { issues.Add(new(schema.Path, "Type", schema.Kind.ToString().ToLowerInvariant())); return; }
        if (schema.AllowedValues.Count > 0 && !schema.AllowedValues.Any(candidate => JsonElement.DeepEquals(candidate, value)))
            issues.Add(new(schema.Path, "Enum"));
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in schema.Properties)
            {
                if (!value.TryGetProperty(property.Name, out var child))
                {
                    if (property.Required) issues.Add(new(property.Path, "Required"));
                    continue;
                }
                ValidateNode(child, property, issues);
            }
            if (!schema.AdditionalProperties)
            {
                var names = schema.Properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject().Where(property => !names.Contains(property.Name)))
                    issues.Add(new($"{schema.Path}.{property.Name}", "AdditionalProperty"));
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var length = value.GetArrayLength();
            Check(length, schema.MinimumItems, schema.MaximumItems, schema.Path, "Items", issues);
            if (length > MaximumRenderedItems) issues.Add(new(schema.Path, "TooManyItems", MaximumRenderedItems.ToString(CultureInfo.InvariantCulture)));
            else if (schema.Items is not null)
            {
                var index = 0;
                foreach (var item in value.EnumerateArray()) ValidateNode(item, AtPath(schema.Items, $"{schema.Path}[{index++}]"), issues);
            }
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            Check(text.Length, schema.MinimumLength, schema.MaximumLength, schema.Path, "Length", issues);
            if (schema.Pattern is not null)
            {
                try { if (!Regex.IsMatch(text, schema.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100))) issues.Add(new(schema.Path, "Pattern")); }
                catch (ArgumentException) { issues.Add(new(schema.Path, "Pattern")); }
                catch (RegexMatchTimeoutException) { issues.Add(new(schema.Path, "Pattern")); }
            }
            if (string.Equals(schema.Format, "uri", StringComparison.OrdinalIgnoreCase) && !Uri.TryCreate(text, UriKind.Absolute, out _)) issues.Add(new(schema.Path, "FormatUri"));
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            if (schema.Minimum is { } minimum && number < minimum) issues.Add(new(schema.Path, "Minimum", minimum.ToString(CultureInfo.InvariantCulture)));
            if (schema.Maximum is { } maximum && number > maximum) issues.Add(new(schema.Path, "Maximum", maximum.ToString(CultureInfo.InvariantCulture)));
            if (schema.ExclusiveMinimum is { } exclusiveMinimum && number <= exclusiveMinimum) issues.Add(new(schema.Path, "ExclusiveMinimum", exclusiveMinimum.ToString(CultureInfo.InvariantCulture)));
            if (schema.ExclusiveMaximum is { } exclusiveMaximum && number >= exclusiveMaximum) issues.Add(new(schema.Path, "ExclusiveMaximum", exclusiveMaximum.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void Check(int value, int? minimum, int? maximum, string path, string code, ICollection<JsonSchemaInputIssue> issues)
    {
        if (minimum is { } min && value < min) issues.Add(new(path, $"Minimum{code}", min.ToString(CultureInfo.InvariantCulture)));
        if (maximum is { } max && value > max) issues.Add(new(path, $"Maximum{code}", max.ToString(CultureInfo.InvariantCulture)));
    }

    public static bool Matches(JsonElement value, JsonSchemaInputKind kind) => kind switch
    {
        JsonSchemaInputKind.Map => value.ValueKind == JsonValueKind.Object,
        JsonSchemaInputKind.List => value.ValueKind == JsonValueKind.Array,
        JsonSchemaInputKind.Text => value.ValueKind == JsonValueKind.String,
        JsonSchemaInputKind.WholeNumber => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        JsonSchemaInputKind.DecimalNumber => value.ValueKind == JsonValueKind.Number,
        JsonSchemaInputKind.Toggle => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        JsonSchemaInputKind.NullValue => value.ValueKind == JsonValueKind.Null,
        _ => false
    };

    public static bool Matches(JsonElement value, JsonSchemaInputNode schema) => schema.Kind == JsonSchemaInputKind.Union
        ? schema.Variants!.Any(variant => Matches(value, variant)) : Matches(value, schema.Kind);

    private static string InferType(JsonElement schema) => schema.TryGetProperty("properties", out _) ? "object" : throw new NotSupportedException("UnsupportedSchema");
    private static JsonSchemaInputKind ParseKind(string type) => type switch { "object" => JsonSchemaInputKind.Map, "array" => JsonSchemaInputKind.List, "string" => JsonSchemaInputKind.Text, "integer" => JsonSchemaInputKind.WholeNumber, "number" => JsonSchemaInputKind.DecimalNumber, "boolean" => JsonSchemaInputKind.Toggle, "null" => JsonSchemaInputKind.NullValue, _ => throw new NotSupportedException("UnsupportedSchema") };
    private static string? Text(JsonElement schema, string name) => schema.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static JsonElement? Clone(JsonElement schema, string name) => schema.TryGetProperty(name, out var value) ? value.Clone() : null;
    private static IReadOnlyList<JsonElement> Array(JsonElement schema, string name) => schema.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(item => item.Clone()).ToArray() : [];
    private static decimal? Decimal(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty(name, out var value)) return null;
        return value.TryGetDecimal(out var result) ? result : throw new NotSupportedException("InvalidSchema");
    }
    private static int? Integer(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty(name, out var value)) return null;
        return value.TryGetInt32(out var result) && result >= 0 ? result : throw new NotSupportedException("InvalidSchema");
    }
}
