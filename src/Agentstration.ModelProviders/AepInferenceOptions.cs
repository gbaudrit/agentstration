namespace Agentstration.ModelProviders;

public sealed class AepInferenceOptions
{
    public const string SectionName = "Agentstration:Aep:Inference";
    public const string HttpClientName = "agentstration-aep-inference";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    public void Validate()
    {
        if (Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromDays(1))
            throw new InvalidOperationException($"{SectionName}:Timeout must be between 00:00:00.001 and 1.00:00:00 (finite).");
    }
}
