using System.Text.Json;
using Agentstration.Aep.Abstractions;

namespace Agentstration.Extensions.Ollama;

public static class OllamaOptionContracts
{
    public const string ModelProfileOptionSet = "io.agentstration.ollama/model-profile";
    public const string Version = "1.0.0";

    public static AepOptionSetDescriptor ModelProfile { get; } = Create();

    private static AepOptionSetDescriptor Create()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "think": { "description": "Ollama thinking mode or effort." },
                "keepAlive": { "type": "string" },
                "contextSize": { "type": "integer", "minimum": 1 },
                "numGpu": { "type": "integer", "minimum": 0 },
                "numThread": { "type": "integer", "minimum": 1 },
                "numBatch": { "type": "integer", "minimum": 1 },
                "mirostat": { "type": "integer", "minimum": 0, "maximum": 2 },
                "endpointMode": { "type": "string", "enum": ["chat"] },
                "additionalOptions": { "type": "object" }
              },
              "additionalProperties": false
            }
            """);
        var version = AepOptionSetVersionDescriptor.Create(Version, document.RootElement);
        return new AepOptionSetDescriptor(
            ModelProfileOptionSet,
            AepContributionKinds.ModelProvider,
            "ollama",
            AepOptionScopes.ModelProfile,
            Version,
            [version]);
    }
}
