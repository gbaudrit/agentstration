using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Agentstration.Schemas;

[JsonConverter(typeof(JsonStringEnumConverter<DataContractMode>))]
public enum DataContractMode { Absent, Fixed, Dynamic }

/// <summary>A declaration, distinct from a concrete contract inherited by one invocation.</summary>
public sealed record DataContractDefinition
{
    public DataContractMode Mode { get; init; } = DataContractMode.Fixed;
    public JsonElement? Schema { get; init; }
    public string? MediaType { get; init; }
    public string Source { get; init; } = "input";
}

public sealed record ResolvedDataContract(JsonElement? Schema, string? MediaType, string Origin,
    DataContractMode DeclaredMode, string Digest, bool Validated = false);

public sealed record InvocationDataContracts(ResolvedDataContract? Input = null, ResolvedDataContract? ExpectedOutput = null, bool ArtifactDraft = false, bool FinalResultMapped = false);

public sealed record DataContractFailure(string Boundary, string Origin, string Path, string Rule,
    string Expected, string Actual, string? FlowRunId = null, string? StepName = null, string? Code = null);

public sealed class DataContractException(string code, DataContractFailure failure) : ArgumentException(
    $"Contract {failure.Boundary} failed at '{failure.Path}': expected {failure.Expected}; received {failure.Actual}. " +
    $"Rule: {failure.Rule}. Origin: {failure.Origin}.")
{
    public string Code { get; } = code;
    public DataContractFailure Failure { get; } = failure;
}

/// <summary>Bounded validation using the existing built-in schema handler. Diagnostics never copy instance values.</summary>
public static partial class DataContracts
{
    public const int MaximumSchemaCharacters = 65_536;
    public const int MaximumValueCharacters = 16 * 1024 * 1024;
    private static readonly BuiltinJsonSchemaFormatHandler Handler = new();
    private static readonly JsonSerializerOptions FailureJson = new(JsonSerializerDefaults.Web);
    public static DataContractFailure? ParseFailure(string? details)
    {
        if (details is not { Length: <= 8192 }) return null;
        try
        {
            using var document = JsonDocument.Parse(details);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var failure = (root.TryGetProperty("contractFailure", out var nested) ? nested : root).Deserialize<DataContractFailure>(FailureJson);
            return failure is not null && Valid(failure.Boundary, 128) && Valid(failure.Origin, 512)
                && Valid(failure.Path, 2048) && Valid(failure.Rule, 128) && Valid(failure.Expected, 256) && Valid(failure.Actual, 256) ? failure : null;
        }
        catch (JsonException) { return null; }
        static bool Valid(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum;
    }

    public static void ValidateDefinition(DataContractDefinition definition, string origin)
    {
        if (!Enum.IsDefined(definition.Mode)
            || definition.Mode == DataContractMode.Dynamic && (definition.Source is not ("input" or "parent") || definition.Schema is not null || definition.MediaType is not null)
            || definition.Mode == DataContractMode.Absent && (definition.Schema is not null || definition.MediaType is not null))
            throw Failure("data_contract_definition_invalid", "definition", origin, "$", "contract_mode", "consistent explicit contract mode", "invalid declaration");
        if (definition.Mode == DataContractMode.Fixed) _ = Resolve(definition, null, null, origin);
    }

    public static ResolvedDataContract? Resolve(DataContractDefinition? definition, ResolvedDataContract? input,
        ResolvedDataContract? parent, string origin)
    {
        if (definition is null) return null;
        if (!Enum.IsDefined(definition.Mode)) throw Failure("data_contract_definition_invalid", "definition", origin, "$", "contract_mode", "explicit contract mode", "unknown mode");
        if (definition.Mode is DataContractMode.Absent or DataContractMode.Dynamic) ValidateDefinition(definition, origin);
        if (definition.Mode == DataContractMode.Absent) return null;
        if (definition.Mode == DataContractMode.Dynamic)
        {
            var inherited = definition.Source == "input" ? input : definition.Source == "parent" ? parent : null;
            if (inherited is null) throw Failure("data_contract_unresolved", "resolution", origin, "$", "contract_missing", "resolved contract", "absent");
            return inherited with { Origin = (origin + " <- " + inherited.Origin)[..Math.Min(512, origin.Length + 4 + inherited.Origin.Length)], DeclaredMode = DataContractMode.Dynamic, Validated = false };
        }
        if (definition.Schema is null && string.IsNullOrWhiteSpace(definition.MediaType))
            throw Failure("data_contract_definition_invalid", "definition", origin, "$", "contract_empty", "schema or MIME type", "absent");
        if (definition.Schema is { } schema)
        {
            if (schema.GetRawText().Length > MaximumSchemaCharacters)
                throw Failure("data_contract_definition_invalid", "definition", origin, "$", "schema_size", "bounded schema", "oversized");
            var checkedSchema = Handler.ValidateDefinitionAsync(schema, null, default).AsTask().GetAwaiter().GetResult();
            if (!checkedSchema.IsValid)
                throw Failure("data_contract_definition_invalid", "definition", origin, "$", checkedSchema.Issues[0].Code, "supported JSON Schema", "unsupported definition");
        }
        if (definition.MediaType is { } media && (media.Length > 160 || !MediaTypeHeaderValue.TryParse(media, out _)))
            throw Failure("data_contract_definition_invalid", "definition", origin, "$", "media_type", "MIME type", "invalid declaration");
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { definition.Schema, definition.MediaType })));
        return new(definition.Schema?.Clone(), definition.MediaType, origin, DataContractMode.Fixed, digest);
    }

    public static ResolvedDataContract? Declared(JsonElement? schema, string origin, string? mediaType = null) =>
        schema is null && mediaType is null ? null : Resolve(new() { Schema = schema, MediaType = mediaType }, null, null, origin);

    public static void Validate(ResolvedDataContract? contract, JsonElement value, string boundary,
        string? runId = null, string? stepName = null, Func<JsonElement, bool>? isProtectedString = null)
    {
        if (contract?.Schema is not { } schema) return;
        if (value.ValueKind == JsonValueKind.Undefined)
            throw Failure("data_contract_violation", boundary, contract.Origin, "$", "required", "defined value", "absent", runId, stepName);
        if (value.GetRawText().Length > MaximumValueCharacters)
            throw Failure("data_contract_value_too_large", boundary, contract.Origin, "$", "value_size", "bounded value", "oversized", runId, stepName);
        var validation = isProtectedString is null ? Handler.ValidateInstanceAsync(schema, value, null, default)
            : Handler.ValidateSymbolicStringsAsync(schema, value, isProtectedString, default);
        var result = validation.AsTask().GetAwaiter().GetResult();
        if (result.IsValid) return;
        var issue = result.Issues[0];
        var (path, expected, actual) = DescribePath(schema, value, issue.Path);
        if (issue.Code == "json_schema_instance_additional_property")
            (expected, actual) = ("only declared properties", "undeclared property (name withheld)");
        else if (issue.Code == "json_schema_instance_required")
            (expected, actual) = ("required property", "absent");
        throw Failure("data_contract_violation", boundary, contract.Origin, path, issue.Code, expected, actual, runId, stepName);
    }

    public static bool IsJsonMediaType(string media) => media.Split(';', 2)[0].Trim() is { } type
        && (type.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.Equals("text/json", StringComparison.OrdinalIgnoreCase) || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    public static JsonElement ReadUtf8(ResolvedDataContract contract, ReadOnlySpan<byte> content, string boundary,
        string? runId = null, string? stepName = null)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException)
        { throw Failure("data_contract_encoding_invalid", boundary, contract.Origin, "$", "utf8_encoding", "valid UTF-8 content", "invalid UTF-8 bytes", runId, stepName); }
        return ReadText(contract, text, boundary, runId, stepName);
    }

    public static JsonElement ReadText(ResolvedDataContract contract, string text, string boundary,
        string? runId = null, string? stepName = null)
    {
        var plainText = contract.Schema is null && contract.MediaType is not null && !IsJsonMediaType(contract.MediaType)
            || contract.MediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true && !IsJsonMediaType(contract.MediaType)
            || contract.MediaType is null && contract.Schema is { ValueKind: JsonValueKind.Object } schema
                && schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "string";
        JsonElement value;
        if (plainText) value = JsonSerializer.SerializeToElement(text);
        else
        {
            try { value = JsonSerializer.Deserialize<JsonElement>(text); }
            catch (JsonException) { throw Failure("data_contract_json_invalid", boundary, contract.Origin, "$", "json_syntax", "valid JSON without Markdown fences; use text/plain for plain text", "invalid JSON text", runId, stepName); }
        }
        Validate(contract, value, boundary, runId, stepName);
        return value;
    }

    public static JsonElement BindOutputParameter(JsonElement input, JsonElement? inputSchema, ResolvedDataContract? contract,
        string boundary, string? runId = null, string? stepName = null)
    {
        if (contract is null || inputSchema is not { ValueKind: JsonValueKind.Object } schema
            || !schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty("outputContract", out _)) return input;
        if (input.ValueKind != JsonValueKind.Object)
            throw Failure("data_contract_violation", boundary, contract.Origin, "$", "contract_parameter", "object input", input.ValueKind.ToString(), runId, stepName);
        var parameter = new Dictionary<string, object?>();
        if (contract.Schema is { } dataSchema) parameter["schema"] = dataSchema;
        if (contract.MediaType is { } media) parameter["mediaType"] = media;
        var value = JsonSerializer.SerializeToElement(parameter);
        if (input.TryGetProperty("outputContract", out var existing) && !JsonElement.DeepEquals(existing, value))
            throw Failure("data_contract_conflict", boundary, contract.Origin, "$.outputContract", "contract_parameter", "effective producer contract", "different explicit parameter", runId, stepName);
        var arguments = input.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        arguments["outputContract"] = value;
        return JsonSerializer.SerializeToElement(arguments);
    }

    public static string Instruction(ResolvedDataContract contract, bool artifactDraft = false) =>
        "\n\nOutput data contract for this invocation (the contract describes the final processing result):\n" +
        (artifactDraft ? "Write the conforming content to the output Artifact and complete its latest revision. Your final chat message is separate.\n"
            : "Return only the conforming result. JSON output must have no Markdown fences or surrounding explanation.\n") +
        JsonSerializer.Serialize(new { contract.MediaType, contract.Schema });

    public static DataContractException Failure(string code, string boundary, string origin, string path, string rule,
        string expected, string actual, string? runId = null, string? stepName = null) =>
        new(code, new(boundary, origin.Length > 512 ? origin[..512] : origin, path, rule, expected, actual, runId, stepName, code));

    private static (string Path, string Expected, string Actual) DescribePath(JsonElement schema, JsonElement value, string requested)
    {
        var path = "$";
        foreach (Match part in PathPartPattern().Matches(requested.TrimStart('$')))
        {
            if (part.Groups[1].Success)
            {
                var name = part.Groups[1].Value;
                if (!schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(name, out var child)) break;
                schema = child;
                path += "." + name;
                value = value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
            }
            else
            {
                if (!schema.TryGetProperty("items", out var items)) break;
                schema = items;
                if (!int.TryParse(part.Groups[2].Value, out var index)) break;
                path += "[" + index + "]";
                value = value.ValueKind == JsonValueKind.Array && index < value.GetArrayLength() ? value[index] : default;
            }
        }
        var expected = schema.TryGetProperty("type", out var type) ? type.ValueKind == JsonValueKind.String ? type.GetString()!
            : type.ValueKind == JsonValueKind.Array ? string.Join("|", type.EnumerateArray().Select(item => item.GetString())) : "schema-conforming value" : "schema-conforming value";
        return (path, expected, value.ValueKind.ToString().ToLowerInvariant());
    }

    [GeneratedRegex(@"\G(?:\.([A-Za-z_][A-Za-z0-9_-]{0,127})|\[(\d{1,8})\])", RegexOptions.CultureInvariant)]
    private static partial Regex PathPartPattern();

}
