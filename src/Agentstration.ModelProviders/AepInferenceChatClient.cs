using System.Net;
using System.Runtime.CompilerServices;
using Agentstration.Aep.Client;
using Microsoft.Extensions.AI;

namespace Agentstration.ModelProviders;

// HttpClient.Timeout stops at response headers for SSE. Keep the inference budget
// alive through discovery, bound-value issuance and consumption of the full stream.
internal sealed class AepInferenceChatClient(IChatClient inner, TimeSpan timeout) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try { return await base.GetResponseAsync(messages, options, deadline.Token); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimedOut(exception);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await using var updates = base.GetStreamingResponseAsync(messages, options, deadline.Token).GetAsyncEnumerator(deadline.Token);
        while (true)
        {
            bool hasNext;
            try { hasNext = await updates.MoveNextAsync(); }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw TimedOut(exception);
            }
            catch (IOException exception) when (deadline.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw TimedOut(exception);
            }
            if (!hasNext) yield break;
            yield return updates.Current;
        }
    }

    private AepProtocolException TimedOut(Exception exception) => new(
        "extension_timeout",
        $"AEP inference exceeded {timeout}. Configure {AepInferenceOptions.SectionName}:Timeout to change this transport budget.",
        HttpStatusCode.GatewayTimeout,
        exception);
}
