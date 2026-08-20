using System.Net;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.LlamaCpp;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class LlamaCppProviderTests
{
    [TestMethod]
    public async Task ChatMapsCanonicalNativeStructuredAndToolOptions()
    {
        string? captured = null;
        using var client = Client(async (request, cancellationToken) =>
        {
            captured = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json(HttpStatusCode.OK, """
                {"model":"local-gguf","choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"weather","arguments":"{\"city\":\"Paris\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":12,"completion_tokens":4,"total_tokens":16}}
                """);
        });
        var provider = new LlamaCppAepModelProvider(client);
        var native = JsonSerializer.SerializeToElement(new
        {
            minP = 0.05,
            repeatPenalty = 1.1,
            chatTemplateKwargs = new { enable_thinking = false }
        });
        var responseFormat = JsonSerializer.SerializeToElement(new
        {
            type = "json_schema",
            json_schema = new { name = "answer", schema = new { type = "object" } }
        });
        var request = Request() with
        {
            Options = new AepModelOptions
            {
                Temperature = 0.2f,
                MaxOutputTokens = 128,
                ResponseFormat = responseFormat,
                NativeOptions = new AepVersionedOptions(
                    LlamaCppOptionContracts.ModelProfileOptionSet,
                    LlamaCppOptionContracts.Version,
                    LlamaCppOptionContracts.ModelProfile.Versions.Single().SchemaDigest,
                    native)
            },
            Tools = [new AepToolDefinition("weather", "Weather", JsonSerializer.SerializeToElement(new { type = "object" }))]
        };

        var response = await provider.ChatAsync(request, default);

        Assert.IsNotNull(captured);
        using var body = JsonDocument.Parse(captured);
        Assert.AreEqual("local-gguf", body.RootElement.GetProperty("model").GetString());
        Assert.AreEqual(128, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual(0.05, body.RootElement.GetProperty("min_p").GetDouble(), 0.001);
        Assert.AreEqual(1.1, body.RootElement.GetProperty("repeat_penalty").GetDouble(), 0.001);
        Assert.IsFalse(body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.AreEqual("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.AreEqual("weather", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.AreEqual(AepFinishReason.ToolCalls, response.FinishReason);
        Assert.AreEqual("Paris", response.Messages.Single().Contents.Single().ToolCall?.Arguments.GetProperty("city").GetString());
        Assert.AreEqual(16L, response.Usage?.TotalTokens);
    }

    [TestMethod]
    public async Task StreamingMapsTextAndAccumulatesFragmentedToolCalls()
    {
        using var client = Client((_, _) => Task.FromResult(Sse("""
            data: {"model":"local-gguf","choices":[{"delta":{"role":"assistant","content":"hello "},"finish_reason":null}]}

            data: {"model":"local-gguf","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"weather","arguments":"{\"city\":"}}]},"finish_reason":null}]}

            data: {"model":"local-gguf","choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"Paris\"}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """)));
        var provider = new LlamaCppAepModelProvider(client);
        var updates = new List<AepChatUpdate>();

        await foreach (var update in provider.ChatStreamingAsync(Request(), default)) updates.Add(update);

        Assert.AreEqual("hello ", updates[0].Contents.Single().Text);
        var call = updates.SelectMany(value => value.Contents).Single(value => value.ToolCall is not null).ToolCall!;
        Assert.AreEqual("weather", call.Name);
        Assert.AreEqual("Paris", call.Arguments.GetProperty("city").GetString());
        Assert.AreEqual(AepFinishReason.ToolCalls, updates[^1].FinishReason);
    }

    [TestMethod]
    public async Task DiscoveryUsesModelsAndPropsForModelSpecificCapabilities()
    {
        using var client = Client((request, _) => Task.FromResult(request.RequestUri?.AbsolutePath switch
        {
            "/v1/models" => Json(HttpStatusCode.OK, """
                {"data":[{"id":"local-gguf","object":"model","meta":{"n_ctx_train":32768,"n_params":7000000000,"size":4200000000}}]}
                """),
            "/props" => Json(HttpStatusCode.OK, """
                {"chat_template_caps":{"supports_tools":true,"supports_reasoning":true},"modalities":{"vision":true,"video":false,"audio":false}}
                """),
            _ => Json(HttpStatusCode.NotFound, "{}")
        }));
        var provider = new LlamaCppAepModelProvider(client);

        var model = (await provider.ListModelsAsync()).Single();

        CollectionAssert.IsSubsetOf(new[] { "chat", "streaming", "structuredOutput", "tools", "reasoning", "vision" }, model.Capabilities!.ToArray());
        Assert.AreEqual("32768", model.Metadata?["contextLength"]);
        Assert.AreEqual("7000000000", model.Metadata?["parameterCount"]);
    }

    [TestMethod]
    public async Task HealthDistinguishesReadyLoadingAndUnreachable()
    {
        using var readyClient = Client((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":\"ok\"}")));
        using var loadingClient = Client((_, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"Loading model\"}}")));
        using var unreachableClient = Client((_, _) => throw new HttpRequestException("refused"));

        Assert.AreEqual("available", (await new LlamaCppAepModelProvider(readyClient).GetHealthAsync()).Status);
        var loading = await new LlamaCppAepModelProvider(loadingClient).GetHealthAsync();
        Assert.AreEqual("loading", loading.Status);
        Assert.AreEqual("Loading model", loading.Details);
        Assert.AreEqual("unreachable", (await new LlamaCppAepModelProvider(unreachableClient).GetHealthAsync()).Status);
    }

    [TestMethod]
    public async Task ApiAndNetworkFailuresUseStableAepErrors()
    {
        using var rejectedClient = Client((_, _) => Task.FromResult(Json(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"invalid grammar\"}}")));
        using var unreachableClient = Client((_, _) => throw new HttpRequestException("refused"));

        var rejected = await Assert.ThrowsAsync<AepServerException>(() => new LlamaCppAepModelProvider(rejectedClient).ChatAsync(Request(), default));
        var unreachable = await Assert.ThrowsAsync<AepServerException>(() => new LlamaCppAepModelProvider(unreachableClient).ChatAsync(Request(), default));

        Assert.AreEqual("invalid_request", rejected.Code);
        Assert.AreEqual("provider_unavailable", unreachable.Code);
    }

    private static AepChatRequest Request() => new("local-gguf", [new AepMessage(AepRole.User, [AepContent.FromText("ping")])]);

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new DelegateHandler(handler)) { BaseAddress = new Uri("http://localhost:8080/") };

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
