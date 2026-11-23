using System.Text.Json;
using Agentstration.Aep.Abstractions;

namespace Agentstration.Extensions.LlamaCpp;

public static class LlamaCppOptionContracts
{
    public const string ModelProfileOptionSet = "io.agentstration.llamacpp/model-profile";
    public const string Version = "1.0.0";

    public static AepOptionSetDescriptor ModelProfile { get; } = Create();

    private static AepOptionSetDescriptor Create()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "minP": { "type": "number", "minimum": 0, "maximum": 1 },
                "typicalP": { "type": "number", "minimum": 0, "maximum": 1 },
                "repeatPenalty": { "type": "number", "exclusiveMinimum": 0 },
                "repeatLastN": { "type": "integer", "minimum": 0 },
                "mirostat": { "type": "integer", "minimum": 0, "maximum": 2 },
                "mirostatTau": { "type": "number", "exclusiveMinimum": 0 },
                "mirostatEta": { "type": "number", "exclusiveMinimum": 0 },
                "reasoningFormat": { "type": "string" },
                "reasoningEffort": { "type": "string" },
                "chatTemplateKwargs": { "type": "object" },
                "additionalOptions": { "type": "object" }
              },
              "additionalProperties": false
            }
            """);
        var version = AepOptionSetVersionDescriptor.Create(Version, document.RootElement);
        return new AepOptionSetDescriptor(
            ModelProfileOptionSet,
            AepContributionKinds.ModelProvider,
            "llamacpp",
            AepOptionScopes.ModelProfile,
            Version,
            [version]);
    }
}
