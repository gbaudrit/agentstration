using System.Text.Json;
using Agentstration.Aep.Abstractions;

namespace Agentstration.Extensions.LocalAI;

public static class LocalAiOptionContracts
{
    public const string ModelProfileOptionSet = "io.agentstration.localai/model-profile";
    public const string Version = "1.0.0";

    public static AepOptionSetDescriptor ModelProfile { get; } = Create();

    private static AepOptionSetDescriptor Create()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "frequencyPenalty": { "type": "number", "minimum": -2, "maximum": 2 },
                "presencePenalty": { "type": "number", "minimum": -2, "maximum": 2 }
              },
              "additionalProperties": false
            }
            """);
        var version = AepOptionSetVersionDescriptor.Create(Version, document.RootElement);
        return new AepOptionSetDescriptor(
            ModelProfileOptionSet,
            AepContributionKinds.ModelProvider,
            "localai",
            AepOptionScopes.ModelProfile,
            Version,
            [version]);
    }
}
