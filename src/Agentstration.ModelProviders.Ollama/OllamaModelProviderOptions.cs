namespace Agentstration.ModelProviders.Ollama;

public sealed class OllamaModelProviderOptions
{
    public const string SectionName = "Agentstration:ModelProviders:Ollama";
    public Uri Endpoint { get; set; } = new("http://localhost:11434");
    public string DefaultModel { get; set; } = string.Empty;

    public void Validate()
    {
        if (!Endpoint.IsAbsoluteUri || (Endpoint.Scheme != Uri.UriSchemeHttp && Endpoint.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("The Ollama endpoint must be an absolute HTTP(S) URI.");
        if (string.IsNullOrWhiteSpace(DefaultModel)) throw new InvalidOperationException("An Ollama default model must be configured.");
    }
}
