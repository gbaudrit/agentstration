namespace Agentstration.Extensions.Ollama;

public sealed class OllamaInferenceOptions
{
    public const string SectionName = "Ollama:Inference";
    public const string HttpClientName = "ollama-inference";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    public void Validate()
    {
        if (Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromDays(1))
            throw new InvalidOperationException($"{SectionName}:Timeout must be between 00:00:00.001 and 1.00:00:00 (finite).");
    }
}
