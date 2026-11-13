using System.Net;
using System.Text;
using System.Text.Json;
using Agentstration.Extensions.Contracts;
using Agentstration.Extensions.Ollama;
using Agentstration.Identity;
using Agentstration.Infrastructure.Runtime;
using Agentstration.ModelProviders;
using Agentstration.Models;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.MicrosoftAgentFramework;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using ReasoningEffort = Agentstration.Models.ReasoningEffort;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class OllamaReasoningPropagationTests
{
    [TestMethod]
    [DataRow(ReasoningMode.Disabled, null, null, "false")]
    [DataRow(ReasoningMode.Disabled, ReasoningEffort.High, null, "false")]
    [DataRow(ReasoningMode.Automatic, null, null, null)]
    [DataRow(ReasoningMode.Automatic, ReasoningEffort.High, null, null)]
    [DataRow(ReasoningMode.Enabled, null, null, "true")]
    [DataRow(ReasoningMode.Enabled, ReasoningEffort.Low, null, "\"low\"")]
    [DataRow(ReasoningMode.Enabled, ReasoningEffort.Medium, null, "\"medium\"")]
    [DataRow(ReasoningMode.Enabled, ReasoningEffort.High, null, "\"high\"")]
    [DataRow(ReasoningMode.Enabled, ReasoningEffort.Minimal, null, "true")]
    [DataRow(ReasoningMode.Disabled, null, "true", "true")]
    [DataRow(ReasoningMode.Disabled, null, "\"high\"", "\"high\"")]
    [DataRow(ReasoningMode.Enabled, ReasoningEffort.High, "false", "false")]
    [DataRow(ReasoningMode.Automatic, null, "false", "false")]
    [DataRow(ReasoningMode.Automatic, null, "\"medium\"", "\"medium\"")]
    public async Task ProfileReasoningReachesNativeOllamaThroughWorkerResolvedClientAndMaf(
        ReasoningMode mode, ReasoningEffort? effort, string? nativeThink, string? expectedThink)
    {
        using var capture = new NativeRequestHandler();
        await using var extension = new OllamaExtensionFactory(capture);
        using var http = extension.CreateClient();
        var resolver = CreateResolver(http, new ModelReasoningOptions { Mode = mode, Effort = effort }, nativeThink);
        var gateway = CreateGateway(resolver);
        var request = WorkerRequest();
        using var client = await resolver.ResolveAsync("profile");
        var metadata = Assert.IsInstanceOfType<ModelChatClientMetadata>(client.GetService(typeof(ModelChatClientMetadata)));

        // Exercise the real server-side Worker gateway, ordinary callers, and the in-process MAF mapper.
        // Both AEP HTTP and OllamaSharp serialization remain in the path; only Ollama's response is faked.
        foreach (var path in new[] { "worker", "resolved", "resolved-with-options", "maf" })
        {
            foreach (var streaming in new[] { false, true })
            {
                if (path == "worker")
                {
                    if (streaming)
                    {
                        var updates = new List<RuntimeGovernedModelUpdate>();
                        await foreach (var update in gateway.StreamModelAsync(request, default)) updates.Add(update);
                        Assert.IsTrue(updates.SelectMany(update => update.Contents).OfType<RuntimeGovernedModelText>()
                            .Any(content => content.Text == "OK"));
                    }
                    else
                    {
                        var response = await gateway.InvokeModelAsync(request, default);
                        Assert.AreEqual("OK", Assert.IsInstanceOfType<RuntimeGovernedModelText>(response.Contents.Single()).Text);
                    }
                }
                else
                {
                    var options = path switch
                    {
                        "maf" => AgentFrameworkChatOptionsMapper.Map(metadata, null),
                        "resolved-with-options" => new ChatOptions { AdditionalProperties = [] },
                        _ => null
                    };
                    await InvokeAsync(client, options, streaming);
                    if (path == "resolved-with-options") Assert.IsEmpty(options!.AdditionalProperties!);
                }

                var body = capture.LastRequest;
                Assert.AreEqual("qwen3.5:4b", body.GetProperty("model").GetString());
                if (expectedThink is null)
                    Assert.IsFalse(body.TryGetProperty("think", out _), $"{path}, streaming={streaming}: Automatic must preserve provider defaults.");
                else
                    Assert.AreEqual(expectedThink, body.GetProperty("think").GetRawText(), $"{path}, streaming={streaming}");
                var nativeOptions = body.GetProperty("options");
                Assert.AreEqual(16384, nativeOptions.GetProperty("num_ctx").GetInt32());
                Assert.AreEqual(8192, nativeOptions.GetProperty("num_predict").GetInt32());
                Assert.AreEqual(0.1f, nativeOptions.GetProperty("temperature").GetSingle());
                Assert.IsFalse(nativeOptions.TryGetProperty("reasoning_enabled", out _));
                Assert.IsFalse(nativeOptions.TryGetProperty("reasoning_effort", out _));
            }
        }
        Assert.AreEqual(8, capture.RequestCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WorkerDisablesThinkingWithoutAnyNativeOptionEnvelope(bool streaming)
    {
        using var capture = new NativeRequestHandler();
        await using var extension = new OllamaExtensionFactory(capture);
        using var http = extension.CreateClient();
        var gateway = CreateGateway(CreateResolver(http, new ModelReasoningOptions { Mode = ReasoningMode.Disabled },
            includeNativeOptions: false));
        if (streaming)
        {
            await foreach (var _ in gateway.StreamModelAsync(WorkerRequest(), default)) { }
        }
        else
        {
            await gateway.InvokeModelAsync(WorkerRequest(), default);
        }
        Assert.IsFalse(capture.LastRequest.GetProperty("think").GetBoolean());
        Assert.IsFalse(capture.LastRequest.GetProperty("options").TryGetProperty("num_ctx", out _));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CallerReasoningAndGenerationOverrideProfileDefaultsWithoutMutatingOptions(bool streaming)
    {
        using var capture = new NativeRequestHandler();
        await using var extension = new OllamaExtensionFactory(capture);
        using var http = extension.CreateClient();
        var resolver = CreateResolver(http, new ModelReasoningOptions { Mode = ReasoningMode.Enabled, Effort = ReasoningEffort.High });
        using var client = await resolver.ResolveAsync("profile");
        var options = new ChatOptions
        {
            Temperature = 0.25f,
            MaxOutputTokens = 512,
            AdditionalProperties = new() { ["reasoning_enabled"] = false }
        };

        await InvokeAsync(client, options, streaming);

        Assert.IsFalse(capture.LastRequest.GetProperty("think").GetBoolean());
        Assert.AreEqual(0.25f, capture.LastRequest.GetProperty("options").GetProperty("temperature").GetSingle());
        Assert.AreEqual(512, capture.LastRequest.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.HasCount(1, options.AdditionalProperties);
        Assert.AreEqual(false, options.AdditionalProperties["reasoning_enabled"]);

        await InvokeAsync(client, null, streaming);
        Assert.AreEqual("high", capture.LastRequest.GetProperty("think").GetString());
    }

    private static async Task InvokeAsync(IChatClient client, ChatOptions? options, bool streaming)
    {
        ChatMessage[] messages = [new(ChatRole.User, "Hello")];
        if (streaming)
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync(messages, options)) updates.Add(update);
            Assert.AreEqual("OK", string.Concat(updates.Select(update => update.Text)));
        }
        else
        {
            Assert.AreEqual("OK", (await client.GetResponseAsync(messages, options)).Text);
        }
    }

    private static ChatClientResolver CreateResolver(HttpClient http, ModelReasoningOptions reasoning, string? nativeThink = null,
        bool includeNativeOptions = true)
    {
        var values = new Dictionary<string, object> { ["contextSize"] = 16384 };
        if (nativeThink is not null) values["think"] = JsonSerializer.Deserialize<JsonElement>(nativeThink);
        var nativeOptions = new VersionedExtensionOptions
        {
            OptionSet = OllamaOptionContracts.ModelProfileOptionSet,
            Version = OllamaOptionContracts.Version,
            SchemaDigest = OllamaOptionContracts.ModelProfile.Versions.Single().SchemaDigest,
            Values = JsonSerializer.SerializeToElement(values)
        };
        return new ChatClientResolver(
            new ProfileStore(reasoning), new DeploymentStore(includeNativeOptions ? nativeOptions : null), new ProviderStore(http.BaseAddress!),
            new ModelProviderResolver([new AepModelProvider(new FixedHttpClientFactory(http))]),
            new GenAiObservabilityOptions { Enabled = false }, NullLoggerFactory.Instance, NullLogger<ChatClientResolver>.Instance);
    }

    private static RuntimeWorkerOperationGateway CreateGateway(IChatClientResolver resolver) => new(
        resolver, null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System,
        new CurrentRequestContext(), NullLogger<RuntimeWorkerOperationGateway>.Instance);

    private static RuntimeGovernedModelRequest WorkerRequest() => new(
        new("material", "agent", Guid.NewGuid(), "agent", 1, "revision", "hash", "handler",
            "Agent", "Agent", "Instructions", "profile", ResourceNamespace.Default, []),
        [new(RuntimeMessageRole.User, [new RuntimeGovernedModelText("Hello")])], [], new RuntimeExecutionOptions(),
        Guid.NewGuid(), new WorkspaceId(Guid.NewGuid()), Guid.NewGuid());

    private sealed class ProfileStore(ModelReasoningOptions reasoning) : IModelProfileStore
    {
        public ValueTask<ModelProfileConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ModelProfileConfiguration
            {
                Name = name,
                DeploymentName = name,
                Reasoning = reasoning,
                Generation = new() { Temperature = 0.1, MaxOutputTokens = 8192 }
            });
    }

    private sealed class DeploymentStore(VersionedExtensionOptions? nativeOptions) : IModelDeploymentStore
    {
        public ValueTask<ModelDeploymentConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ModelDeploymentConfiguration
            {
                Name = name,
                ProviderName = "ollama-local",
                ModelName = "qwen3.5:4b",
                ProviderOptions = nativeOptions is null ? new Dictionary<string, VersionedExtensionOptions>()
                    : new Dictionary<string, VersionedExtensionOptions> { ["ollama"] = nativeOptions }
            });
    }

    private sealed class ProviderStore(Uri endpoint) : IModelProviderConfigurationStore
    {
        public ValueTask<ModelProviderConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ModelProviderConfiguration
            {
                Uid = Guid.NewGuid(),
                Name = name,
                AdapterType = "aep",
                ContributionId = "ollama",
                Extension = new ResourceReference("ollama-extension"),
                Endpoint = endpoint
            });

        public ValueTask<IReadOnlyList<ModelProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class OllamaExtensionFactory(NativeRequestHandler capture) : WebApplicationFactory<OllamaAepModelProvider>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<OllamaApiClient>();
            services.AddSingleton(_ => new OllamaApiClient(new HttpClient(capture, disposeHandler: false)
            {
                BaseAddress = new Uri("http://ollama.test")
            }));
        });
    }

    private sealed class NativeRequestHandler : HttpMessageHandler
    {
        public JsonElement LastRequest { get; private set; }
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("/api/chat", request.RequestUri!.AbsolutePath);
            Assert.AreEqual(HttpMethod.Post, request.Method);
            LastRequest = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            RequestCount++;
            // OllamaSharp aggregates streamed NDJSON for ordinary chat as well as streaming.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"model":"qwen3.5:4b","message":{"role":"assistant","content":"OK"},"done":true,"done_reason":"stop"}

                    """, Encoding.UTF8, "application/x-ndjson")
            };
        }
    }
}
