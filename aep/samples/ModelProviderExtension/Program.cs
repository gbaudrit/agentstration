using System.Runtime.CompilerServices;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;

namespace Aep.Samples.ModelProvider;

public sealed class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddAep(options => options.Extension = new("sample.model-provider", "Deterministic model sample", "1.0.0", "Provider-neutral AEP model sample."))
            .AddModelProvider<EchoModelProvider>();
        var app = builder.Build();
        app.MapAep();
        await app.RunAsync();
    }
}

public sealed class EchoModelProvider : IAepModelProvider
{
    public AepModelProviderDescriptor Descriptor { get; } = new(
        "echo",
        "Echo model provider",
        new(Chat: true, Streaming: true, ModelDiscovery: true),
        [new("echo-1", "Echo 1", ["chat", "streaming"])]);

    public Task<AepChatResponse> ChatAsync(AepChatRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prompt = request.Messages.LastOrDefault(value => value.Role == AepRole.User)?.Contents.FirstOrDefault(value => value.Kind == AepContentKind.Text)?.Text ?? "";
        return Task.FromResult(new AepChatResponse([new(AepRole.Assistant, [AepContent.FromText($"Echo: {prompt}")])], request.Model, AepFinishReason.Stop));
    }

    public async IAsyncEnumerable<AepChatUpdate> ChatStreamingAsync(AepChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = request.Messages.LastOrDefault(value => value.Role == AepRole.User)?.Contents.FirstOrDefault(value => value.Kind == AepContentKind.Text)?.Text ?? "";
        yield return new([AepContent.FromText("Echo: ")], AepRole.Assistant, request.Model);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new([AepContent.FromText(prompt)], FinishReason: AepFinishReason.Stop);
    }
}
