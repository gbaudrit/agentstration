using System.Net;
using System.Text;
using Agentstration.Extensions.Ollama;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.ModelProviders.Tests;

internal sealed class OllamaFactory(string timeout, TimeSpan delay = default, bool delayBody = false) : WebApplicationFactory<OllamaAepModelProvider>
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource BodyReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Calls { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Ollama:Inference:Timeout", timeout);
        builder.ConfigureServices(services => services.AddHttpClient(OllamaInferenceOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new Handler(async (_, token) =>
            {
                Calls++;
                Started.TrySetResult();
                try
                {
                    if (!delayBody) await Task.Delay(delay, token);
                    const string body = "{\"model\":\"test-model\",\"message\":{\"role\":\"assistant\",\"content\":\"pong\"},\"done\":true,\"done_reason\":\"stop\"}\n";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = delayBody
                            ? new StreamContent(new DelayedStream("{\"model\":\"test-model\",\"message\":{\"role\":\"assistant\",\"content\":\"partial\"},\"done\":false}\n", body, delay, Cancelled, BodyReadStarted))
                            : new StringContent(body, Encoding.UTF8, "application/x-ndjson")
                    };
                }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            })));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class DelayedStream(string prefix, string body, TimeSpan delay, TaskCompletionSource cancelled, TaskCompletionSource bodyReadStarted) : MemoryStream(Encoding.UTF8.GetBytes(prefix + body))
    {
        private readonly CancellationTokenSource disposed = new();

        protected override void Dispose(bool disposing)
        {
            if (disposing) disposed.Cancel();
            base.Dispose(disposing);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < prefix.Length)
                return await base.ReadAsync(buffer[..Math.Min(buffer.Length, prefix.Length - (int)Position)], cancellationToken);
            using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposed.Token);
            bodyReadStarted.TrySetResult();
            try { await Task.Delay(delay, read.Token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
