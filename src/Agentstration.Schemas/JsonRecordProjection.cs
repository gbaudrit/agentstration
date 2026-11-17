using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Schemas;

/// <summary>An explicit transformation result, separate from an ordinary business JSON array.</summary>
public sealed record JsonRecordProjection
{
    public const string Format = "json.records/v1";
    public const int MaximumRecords = 100;
    public const int MaximumBytes = 65536;
    [JsonPropertyName("$agentstration")] public string Contract { get; init; } = Format;
    public required JsonElement Value { get; init; }
    public required ResolvedDataContract RecordContract { get; init; }
    public required string SelectionPath { get; init; }

    [JsonIgnore] public ResolvedDataContract CollectionContract => Collection(RecordContract);

    public static ResolvedDataContract Collection(ResolvedDataContract record) => DataContracts.Declared(
        JsonSerializer.SerializeToElement(new { type = "array", minItems = 1, maxItems = MaximumRecords, items = record.Schema }),
        "json.explode/collection", "application/json")! with
    { Validated = true };

    public void Validate(CancellationToken ct = default)
    {
        if (Contract != Format || SelectionPath is null || RecordContract is null || SelectionPath.Length > 1024 || Value.ValueKind != JsonValueKind.Array
            || Value.GetArrayLength() is < 1 or > MaximumRecords || RecordContract.Schema is null
            || JsonSerializer.SerializeToUtf8Bytes(Value).Length > MaximumBytes)
            throw new ArgumentException("A JSON record projection requires a bounded non-empty object collection and its record contract.");
        var declared = DataContracts.Declared(RecordContract.Schema, RecordContract.Origin, RecordContract.MediaType)!;
        if (declared.Digest != RecordContract.Digest)
            throw new ArgumentException("The JSON record contract evidence is inconsistent.");
        foreach (var item in Value.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("JSON records must be objects.");
            DataContracts.Validate(declared, item, "json-record");
        }
    }

    public static JsonRecordProjection? Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("$agentstration", out var marker)
            || marker.ValueKind != JsonValueKind.String || marker.GetString() != Format) return null;
        var projection = value.Deserialize<JsonRecordProjection>(JsonSerializerOptions.Web)
            ?? throw new ArgumentException("Invalid JSON record projection.");
        projection.Validate();
        return projection;
    }

    public static JsonRecordProjection Normalize(JsonElement value, ResolvedDataContract? rootContract)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A stored JSON result must be an object, or an explicit json.explode record collection.");
        var result = new JsonRecordProjection
        {
            Value = JsonSerializer.SerializeToElement(new[] { value }),
            SelectionPath = "",
            RecordContract = (rootContract ?? DataContracts.Declared(JsonSerializer.SerializeToElement(new { type = "object" }),
                "json-root/record", "application/json")!) with
            { Validated = true }
        };
        result.Validate();
        return result;
    }
}
