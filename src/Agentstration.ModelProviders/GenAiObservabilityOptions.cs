namespace Agentstration.ModelProviders;

public sealed class GenAiObservabilityOptions
{
    public const string SectionName = "Observability:GenAI";
    public const string ChatClientSourceName = "Agentstration.GenAI";

    public bool Enabled { get; init; } = true;
    public HttpPayloadCaptureOptions HttpPayloadCapture { get; init; } = new();

    public void Validate(bool isDevelopment)
    {
        if (!HttpPayloadCapture.Enabled) return;
        if (!isDevelopment)
            throw new InvalidOperationException("Observability:GenAI:HttpPayloadCapture may only be enabled in the Development environment.");
        if (HttpPayloadCapture.MaximumBodyLength is < 256 or > 1_048_576)
            throw new InvalidOperationException("Observability:GenAI:HttpPayloadCapture:MaximumBodyLength must be between 256 and 1048576 characters.");
    }
}

public sealed class HttpPayloadCaptureOptions
{
    public bool Enabled { get; init; }
    public int MaximumBodyLength { get; init; } = 16_384;
    public bool CaptureResponse { get; init; }
}
