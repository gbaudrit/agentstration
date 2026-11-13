using System.Net;
using System.Text;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Ollama;
using Agentstration.Models;
using Agentstration.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class InferenceTimeoutTests
{
    [TestMethod]
    public async Task DefaultsAndConfiguredBudgetsApplyOnlyToInferenceClients()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(90), new AepInferenceOptions().Timeout);
        Assert.AreEqual(TimeSpan.FromSeconds(90), new OllamaInferenceOptions().Timeout);
        await using var extension = new OllamaFactory("00:05:00");
        using var services = Services(extension, "00:06:00");
        var aepClients = services.GetRequiredService<IHttpClientFactory>();
        var ollamaClients = extension.Services.GetRequiredService<IHttpClientFactory>();
        using var aep = aepClients.CreateClient(AepInferenceOptions.HttpClientName);
        using var management = aepClients.CreateClient("agentstration-aep");
        using var ollama = ollamaClients.CreateClient(OllamaInferenceOptions.HttpClientName);
        using var discovery = ollamaClients.CreateClient("ollama");
        Assert.AreEqual(TimeSpan.FromMinutes(6), aep.Timeout);
        Assert.AreEqual(TimeSpan.FromMinutes(5), ollama.Timeout);
        Assert.AreEqual(TimeSpan.FromSeconds(90), management.Timeout);
        Assert.AreEqual(TimeSpan.FromSeconds(90), discovery.Timeout);
    }

    [TestMethod]
    [DataRow("00:00:00")]
    [DataRow("-00:00:00.001")]
    [DataRow("1.00:00:00.001")]
    [DataRow("not-a-duration")]
    public async Task InvalidConfigurationFailsDuringComposition(string timeout)
    {
        var aepError = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddAgentstrationModelProviders(Configuration(AepInferenceOptions.SectionName, timeout)));
        StringAssert.Contains(aepError.ToString(), "Timeout");
        await using var extension = new OllamaFactory(timeout);
        var ollamaError = Assert.Throws<InvalidOperationException>(() => _ = extension.Services);
        StringAssert.Contains(ollamaError.ToString(), "Timeout");
    }

    [TestMethod]
    [DataRow("00:00:00.001")]
    [DataRow("1.00:00:00")]
    public async Task FiniteBoundaryValuesAreAccepted(string timeout)
    {
        await using var extension = new OllamaFactory(timeout);
        using var services = Services(extension, timeout);
        Assert.AreEqual(TimeSpan.Parse(timeout, System.Globalization.CultureInfo.InvariantCulture),
            services.GetRequiredService<AepInferenceOptions>().Timeout);
        Assert.AreEqual(services.GetRequiredService<AepInferenceOptions>().Timeout,
            extension.Services.GetRequiredService<OllamaInferenceOptions>().Timeout);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DelayedInferenceSucceedsThroughBothHops(bool streaming)
    {
        await using var extension = new OllamaFactory("00:00:02", TimeSpan.FromMilliseconds(150));
        using var services = Services(extension, "00:00:03");
        using var client = ChatClient(services);
        Assert.AreEqual("pong", await ReadAsync(client, streaming));
        Assert.AreEqual(1, extension.Calls);
    }

    [TestMethod]
    public async Task OllamaTimeoutReturnsDistinctGatewayDiagnosticAndCancelsNativeRequest()
    {
        await using var extension = new OllamaFactory("00:00:00.050", TimeSpan.FromSeconds(5));
        using var services = Services(extension, "00:00:03");
        using var client = ChatClient(services);
        var error = await Assert.ThrowsAsync<AepProtocolException>(() => ReadAsync(client, false));
        Assert.AreEqual("provider_timeout", error.Code);
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, error.StatusCode);
        StringAssert.Contains(error.Message, "Ollama:Inference:Timeout");
        await extension.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, extension.Calls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AepTimeoutCancelsBothHopsIncludingStreamingBody(bool streaming)
    {
        await using var extension = new OllamaFactory("00:00:03", TimeSpan.FromSeconds(5), delayBody: streaming);
        using var services = Services(extension, "00:00:00.200");
        using var client = ChatClient(services);
        var error = await Assert.ThrowsAsync<AepProtocolException>(() => ReadAsync(client, streaming));
        Assert.AreEqual("extension_timeout", error.Code);
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, error.StatusCode);
        StringAssert.Contains(error.Message, "Agentstration:Aep:Inference:Timeout");
        await extension.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CallerCancellationRemainsCancellationAndStopsBothHops(bool streaming)
    {
        await using var extension = new OllamaFactory("00:00:03", TimeSpan.FromSeconds(5), delayBody: streaming);
        using var services = Services(extension, "00:00:03");
        using var client = ChatClient(services);
        using var caller = new CancellationTokenSource();
        var response = ReadAsync(client, streaming, caller.Token);
        await extension.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (streaming) await extension.BodyReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await caller.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => response);
        await extension.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task AepStreamingDeadlineRemainsActiveAfterFirstUpdate()
    {
        await using var extension = new OllamaFactory("00:00:03", TimeSpan.FromSeconds(5), delayBody: true);
        using var services = Services(extension, "00:00:00.500");
        using var client = ChatClient(services);
        await using var updates = client.GetStreamingResponseAsync([new(ChatRole.User, "ping")]).GetAsyncEnumerator();
        Assert.IsTrue(await updates.MoveNextAsync());
        Assert.AreEqual("partial", updates.Current.Text);
        var error = await Assert.ThrowsAsync<AepProtocolException>(() => updates.MoveNextAsync().AsTask());
        Assert.AreEqual("extension_timeout", error.Code);
        await extension.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task OllamaStreamingBodyTimeoutHasProviderDiagnostic()
    {
        await using var extension = new OllamaFactory("00:00:00.100", TimeSpan.FromSeconds(5), delayBody: true);
        var provider = extension.Services.GetRequiredService<IEnumerable<IAepModelProvider>>().Single();
        var error = await Assert.ThrowsAsync<AepServerException>(async () =>
        {
            await foreach (var _ in provider.ChatStreamingAsync(new("test-model", [new(Agentstration.Aep.Abstractions.AepRole.User,
                [Agentstration.Aep.Abstractions.AepContent.FromText("ping")])]), default)) { }
        });
        Assert.AreEqual("provider_timeout", error.Code);
        Assert.AreEqual(504, error.StatusCode);
        await extension.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static IConfiguration Configuration(string section, string timeout) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [$"{section}:Timeout"] = timeout }).Build();

    private static ServiceProvider Services(OllamaFactory extension, string timeout)
    {
        var services = new ServiceCollection();
        services.AddAgentstrationModelProviders(Configuration(AepInferenceOptions.SectionName, timeout));
        services.AddHttpClient(AepInferenceOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => extension.Server.CreateHandler());
        return services.BuildServiceProvider();
    }

    private static IChatClient ChatClient(ServiceProvider services) => services.GetRequiredService<AepModelProvider>().CreateChatClient(
        new ModelProviderConfiguration
        {
            Uid = Guid.NewGuid(), Namespace = ResourceNamespace.Default, Name = "ollama", AdapterType = "aep",
            ContributionId = "ollama", Extension = new ResourceReference("ollama"), Endpoint = new Uri("http://localhost/")
        },
        new ModelDeploymentConfiguration { Name = "test", ProviderName = "ollama", ModelName = "test-model" });

    private static async Task<string> ReadAsync(IChatClient client, bool streaming, CancellationToken cancellationToken = default)
    {
        ChatMessage[] messages = [new(ChatRole.User, "ping")];
        if (!streaming) return (await client.GetResponseAsync(messages, cancellationToken: cancellationToken)).Text;
        var text = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken)) text.Append(update.Text);
        return text.ToString();
    }

    private sealed class OllamaFactory(string timeout, TimeSpan delay = default, bool delayBody = false) : WebApplicationFactory<OllamaAepModelProvider>
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
