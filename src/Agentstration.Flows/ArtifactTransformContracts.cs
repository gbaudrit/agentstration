using System.Text.Json;

namespace Agentstration.Flows;

/// <summary>Reference envelopes accepted and returned by Artifact processing; data contracts describe their content separately.</summary>
public static class ArtifactTransformContracts
{
    private static JsonElement References { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"array","items":{"type":"object","properties":{"artifactId":{"type":"string"}},"required":["artifactId"]}}
        """);
    public static JsonElement Manifest { get; } = ManifestSchema(false);
    private static JsonElement IncomingManifest { get; } = ManifestSchema(true);
    public static JsonElement ProcessingInput { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"artifact":{"type":"object"},"content":{},"inputAccess":{"type":"object"},"outputAccess":{"type":"object"},"parameters":{"type":"object"}},"required":["artifact","content","inputAccess","outputAccess","parameters"],"additionalProperties":false}
        """);
    public static JsonElement Input { get; } = JsonSerializer.SerializeToElement(new
    {
        anyOf = new[]
        {
            References, IncomingManifest,
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { anyOf = new[] { IncomingManifest, References } }, parameters = new { type = "object" } }, required = new[] { "value", "parameters" }, additionalProperties = false })
        }
    });
    private static JsonElement ManifestSchema(bool additionalProperties) => JsonSerializer.SerializeToElement(new
    { type = "object", properties = new { artifacts = References }, required = new[] { "artifacts" }, additionalProperties });
}
