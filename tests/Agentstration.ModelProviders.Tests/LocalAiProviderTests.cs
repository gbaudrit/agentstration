using System.Net;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.LocalAI;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class LocalAiProviderTests
{
    [TestMethod]
    public void DescriptorDoesNotClaimBackendDependentStructuredOutputOrVision()
    {
        using var client = Client((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        var capabilities = new LocalAiAepModelProvider(client).Descriptor.Capabilities;

        Assert.IsTrue(capabilities.Chat);
        Assert.IsTrue(capabilities.Streaming);
        Assert.IsTrue(capabilities.Tools);
        Assert.IsTrue(capabilities.Thinking);
        Assert.IsFalse(capabilities.StructuredOutput);
        Assert.IsFalse(capabilities.Vision);
    }

    [TestMethod]
    public async Task ChatMapsCanonicalAllowlistedNativeAndToolOptions()
    {
        string? captured = null;
        using var client = Client(async (request, cancellationToken) =>
        {
            captured = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json(HttpStatusCode.OK, """
                {"model":"chat-model","choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"weather","arguments":"{\"city\":\"Paris\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":12,"completion_tokens":4,"total_tokens":16}}
                """);
        });
        var native = JsonSerializer.SerializeToElement(new { frequencyPenalty = 0.2, presencePenalty = 0.3 });
        var request = Request() with
        {
            Options = new AepModelOptions
            {
                Temperature = 0.25f,
                MaxOutputTokens = 128,
                NativeOptions = VersionedOptions(native)
            },
            Tools = [new AepToolDefinition("weather", "Weather", JsonSerializer.SerializeToElement(new { type = "object" }))]
        };

        var response = await new LocalAiAepModelProvider(client).ChatAsync(request, default);

        Assert.IsNotNull(captured);
        using var body = JsonDocument.Parse(captured);
        Assert.AreEqual("chat-model", body.RootElement.GetProperty("model").GetString());
        Assert.AreEqual(128, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual(0.2, body.RootElement.GetProperty("frequency_penalty").GetDouble(), 0.001);
        Assert.AreEqual(0.3, body.RootElement.GetProperty("presence_penalty").GetDouble(), 0.001);
        Assert.AreEqual("weather", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.IsFalse(body.RootElement.TryGetProperty("metadata", out _));
        Assert.AreEqual(AepFinishReason.ToolCalls, response.FinishReason);
        Assert.AreEqual("Paris", response.Messages.Single().Contents.Single().ToolCall?.Arguments.GetProperty("city").GetString());
        Assert.AreEqual(16L, response.Usage?.TotalTokens);
    }

    [TestMethod]
    public async Task NativeOptionsCannotEnableLocalAiMcpOrArbitraryPassthrough()
    {
        var invoked = false;
        using var client = Client((_, _) =>
        {
            invoked = true;
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        });
        var native = JsonSerializer.SerializeToElement(new { metadata = new { mcp_servers = "untrusted" } });
        var request = Request() with
        {
            Options = new AepModelOptions
            {
                NativeOptions = VersionedOptions(native)
            }
        };

        var exception = await Assert.ThrowsAsync<AepServerException>(() => new LocalAiAepModelProvider(client).ChatAsync(request, default));

        Assert.AreEqual("invalid_request", exception.Code);
        Assert.IsFalse(invoked);
    }

    [TestMethod]
    public async Task StreamingMapsTextAndAccumulatesFragmentedToolCalls()
    {
        using var client = Client((_, _) => Task.FromResult(Sse("""
            data: {"model":"chat-model","choices":[{"delta":{"role":"assistant","content":"hello "},"finish_reason":null}]}

            data: {"model":"chat-model","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"weather","arguments":"{\"city\":"}}]},"finish_reason":null}]}

            data: {"model":"chat-model","choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"Paris\"}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """)));
        var updates = new List<AepChatUpdate>();

        await foreach (var update in new LocalAiAepModelProvider(client).ChatStreamingAsync(Request(), default)) updates.Add(update);

        Assert.AreEqual("hello ", updates[0].Contents.Single().Text);
        var call = updates.SelectMany(value => value.Contents).Single(value => value.ToolCall is not null).ToolCall!;
        Assert.AreEqual("weather", call.Name);
        Assert.AreEqual("Paris", call.Arguments.GetProperty("city").GetString());
        Assert.AreEqual(AepFinishReason.ToolCalls, updates[^1].FinishReason);
    }

    [TestMethod]
    public async Task DiscoveryFiltersNonChatModelsAndMapsReportedCapabilities()
    {
        using var client = Client((request, _) => Task.FromResult(request.RequestUri?.AbsolutePath == "/v1/models/capabilities"
            ? Json(HttpStatusCode.OK, """
                {"object":"list","data":[
                  {"id":"chat-model","object":"model","capabilities":["chat","tools","thinking","vision"],"input_modalities":["text","image"],"output_modalities":["text"]},
                  {"id":"embedding-model","object":"model","capabilities":["embeddings"],"input_modalities":["text"],"output_modalities":["text"]}
                ]}
                """)
            : Json(HttpStatusCode.NotFound, "{}")));

        var model = (await new LocalAiAepModelProvider(client).ListModelsAsync()).Single();

        Assert.AreEqual("chat-model", model.Id);
        CollectionAssert.AreEquivalent(new[] { "chat", "streaming", "tools", "reasoning" }, model.Capabilities!.ToArray());
        Assert.AreEqual("text,image", model.Metadata?["inputModalities"]);
        CollectionAssert.DoesNotContain(model.Capabilities!.ToArray(), "vision");
        CollectionAssert.DoesNotContain(model.Capabilities!.ToArray(), "structuredOutput");
    }

    [TestMethod]
    public async Task DiscoveryRequiresCapabilitySafeLocalAiEndpoint()
    {
        using var client = Client((_, _) => Task.FromResult(Json(HttpStatusCode.NotFound, "{}")));

        var exception = await Assert.ThrowsAsync<AepServerException>(() => new LocalAiAepModelProvider(client).ListModelsAsync());

        Assert.AreEqual("provider_incompatible", exception.Code);
    }

    [TestMethod]
    public async Task HealthUsesReadinessAndDistinguishesLoadingAndUnreachable()
    {
        string? path = null;
        using var readyClient = Client((request, _) =>
        {
            path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":\"ok\"}"));
        });
        using var loadingClient = Client((_, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"models loading\"}}")));
        using var unreachableClient = Client((_, _) => throw new HttpRequestException("refused"));

        Assert.AreEqual("available", (await new LocalAiAepModelProvider(readyClient).GetHealthAsync()).Status);
        Assert.AreEqual("/readyz", path);
        var loading = await new LocalAiAepModelProvider(loadingClient).GetHealthAsync();
        Assert.AreEqual("loading", loading.Status);
        Assert.AreEqual("models loading", loading.Details);
        Assert.AreEqual("unreachable", (await new LocalAiAepModelProvider(unreachableClient).GetHealthAsync()).Status);
    }

    [TestMethod]
    public async Task ApiAndNetworkFailuresUseStableAepErrors()
    {
        using var rejectedClient = Client((_, _) => Task.FromResult(Json(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"invalid request\"}}")));
        using var unreachableClient = Client((_, _) => throw new HttpRequestException("refused"));

        var rejected = await Assert.ThrowsAsync<AepServerException>(() => new LocalAiAepModelProvider(rejectedClient).ChatAsync(Request(), default));
        var unreachable = await Assert.ThrowsAsync<AepServerException>(() => new LocalAiAepModelProvider(unreachableClient).ChatAsync(Request(), default));

        Assert.AreEqual("invalid_request", rejected.Code);
        Assert.AreEqual("provider_unavailable", unreachable.Code);
    }

    private static AepChatRequest Request() => new("chat-model", [new AepMessage(AepRole.User, [AepContent.FromText("ping")])]);

    private static AepVersionedOptions VersionedOptions(JsonElement values) => new(
        LocalAiOptionContracts.ModelProfileOptionSet,
        LocalAiOptionContracts.Version,
        LocalAiOptionContracts.ModelProfile.Versions.Single().SchemaDigest,
        values);

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new DelegateHandler(handler)) { BaseAddress = new Uri("http://localhost:8081/") };

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Sse(string content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8) };
        response.Content.Headers.ContentType = new("text/event-stream");
        return response;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
