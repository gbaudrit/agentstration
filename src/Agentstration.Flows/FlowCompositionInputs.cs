using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Flows;

[JsonConverter(typeof(JsonStringEnumConverter<FlowCompositionFunctionInputSource>))]
public enum FlowCompositionFunctionInputSource
{
    [JsonStringEnumMemberName("previousResult")] PreviousResult,
    [JsonStringEnumMemberName("acquisitionContent")] AcquisitionContent
}

/// <summary>The narrow text-response contract eligible for acquisition input adaptation.</summary>
public static class FlowCompositionInputs
{
    public static bool IsTextResponse(JsonElement schema) => HasType(schema, "object")
        && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
        && properties.EnumerateObject().Count() == 2
        && properties.TryGetProperty("mediaType", out var mediaType) && HasType(mediaType, "string")
        && properties.TryGetProperty("content", out var content) && HasType(content, "string")
        && schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
        && required.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "mediaType")
        && required.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "content")
        && schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;

    public static bool ExpectsText(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return false;
        var value = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty("value", out var wrapped) ? wrapped : schema;
        return HasType(value, "string");
    }

    private static bool HasType(JsonElement schema, string expected) => schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == expected;
}
