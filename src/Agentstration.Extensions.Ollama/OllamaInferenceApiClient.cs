using OllamaSharp;
using OllamaSharp.Models;

namespace Agentstration.Extensions.Ollama;

internal sealed class OllamaInferenceApiClient(HttpClient client) : OllamaApiClient(client)
{
    protected override async Task<HttpResponseMessage> SendToOllamaAsync(
        HttpRequestMessage requestMessage,
        OllamaRequest? ollamaRequest,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var response = await base.SendToOllamaAsync(requestMessage, ollamaRequest, completionOption, cancellationToken);
        // OllamaSharp 5.4.30 reads streamed lines without a CancellationToken.
        // Closing the response aborts those reads as well. The provider owns this
        // per-inference linked token source and disposes it when inference ends,
        // releasing the registration even after a successful response.
        cancellationToken.Register(static state => ((HttpResponseMessage)state!).Dispose(), response);
        return response;
    }
}
