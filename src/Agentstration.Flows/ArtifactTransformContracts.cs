using System.Text.Json;

namespace Agentstration.Flows;

/// <summary>Reference envelopes accepted and returned by Artifact processing; data contracts describe their content separately.</summary>
public static class ArtifactTransformContracts
{
    public static JsonElement Manifest { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"artifacts":{"type":"array","items":{"type":"object","properties":{"artifactId":{"type":"string"}},"required":["artifactId"]}}},"required":["artifacts"],"additionalProperties":false}
        """);
    public static JsonElement ProcessingInput { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"artifact":{"type":"object"},"content":{},"inputAccess":{"type":"object"},"outputAccess":{"type":"object"},"parameters":{"type":"object"}},"required":["artifact","content","inputAccess","outputAccess","parameters"],"additionalProperties":false}
        """);
    public static JsonElement Input { get; } = JsonSerializer.SerializeToElement(new
    {
        anyOf = new[]
        {
            JsonSerializer.Deserialize<JsonElement>("""{"type":"array","items":{"type":"object","properties":{"artifactId":{"type":"string"}},"required":["artifactId"]}}"""),
            Manifest,
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { anyOf = new[] { Manifest, JsonSerializer.Deserialize<JsonElement>("""{"type":"array","items":{"type":"object"}}""") } }, parameters = new { type = "object" } }, required = new[] { "value", "parameters" }, additionalProperties = false })
        }
    });
}
