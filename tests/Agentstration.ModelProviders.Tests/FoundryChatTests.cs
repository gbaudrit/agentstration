using System.Net;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.Foundry;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
[DoNotParallelize]
public sealed class FoundryChatTests
{
    [TestMethod]
    public async Task NonStreamingChatMapsMessagesOptionsAndResponseWithoutChangingDeploymentIdentity()
    {
        await WithKeyAsync(async key =>
        {
            var calls = 0;
            using var client = Client(async (request, cancellationToken) =>
            {
                calls++;
                Assert.AreEqual(HttpMethod.Post, request.Method);
                Assert.AreEqual("https://foundry.example/openai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
                Assert.AreEqual(key, request.Headers.GetValues("api-key").Single());
                Assert.AreEqual("application/json", request.Content!.Headers.ContentType!.MediaType);
                using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                var root = document.RootElement;
                Assert.AreEqual("Phi-4-reasoning", root.GetProperty("model").GetString());
                Assert.IsFalse(root.GetProperty("stream").GetBoolean());
                var messages = root.GetProperty("messages");
                Assert.AreEqual(4, messages.GetArrayLength());
                Assert.AreEqual("system", messages[0].GetProperty("role").GetString());
                Assert.AreEqual("rules", messages[0].GetProperty("content").GetString());
                Assert.AreEqual("user", messages[1].GetProperty("role").GetString());
                Assert.AreEqual("hello world", messages[1].GetProperty("content").GetString());
                Assert.AreEqual("assistant", messages[2].GetProperty("role").GetString());
                Assert.AreEqual("tool", messages[3].GetProperty("role").GetString());
                Assert.AreEqual("call-1", messages[3].GetProperty("tool_call_id").GetString());
                Assert.AreEqual("done", messages[3].GetProperty("content").GetString());
                Assert.AreEqual(0.5f, root.GetProperty("temperature").GetSingle());
                Assert.AreEqual(0.8f, root.GetProperty("top_p").GetSingle());
                Assert.AreEqual(120, root.GetProperty("max_completion_tokens").GetInt32());
                Assert.AreEqual(42, root.GetProperty("seed").GetInt64());
                Assert.AreEqual("stop", root.GetProperty("stop")[0].GetString());
                Assert.IsFalse(root.TryGetProperty("response_format", out _));
                return Json(HttpStatusCode.OK, """
                    {"model":"backend-model-id","choices":[{"index":0,"message":{"role":"assistant","content":"final answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":34,"total_tokens":46}}
                    """);
            });
            var toolResult = JsonSerializer.SerializeToElement("done");
            var request = new AepChatRequest("Phi-4-reasoning", [
                new(AepRole.System, [AepContent.FromText("rules")]),
                new(AepRole.User, [AepContent.FromText("hello "), AepContent.FromText("world")]),
                new(AepRole.Assistant, [AepContent.FromText("checking")]),
                new(AepRole.Tool, [new AepContent { Kind = AepContentKind.ToolResult, ToolResult = new("call-1", toolResult) }])
            ], new AepModelOptions
            {
                Temperature = 0.5f,
                TopP = 0.8f,
                MaxOutputTokens = 120,
                Seed = 42,
                StopSequences = ["stop"],
                ResponseFormat = JsonSerializer.SerializeToElement(new { type = "text" })
            });

            var response = await Provider(client).ChatAsync(request, default);

            Assert.AreEqual(1, calls);
            Assert.AreEqual("Phi-4-reasoning", response.Model);
            Assert.AreEqual(AepFinishReason.Stop, response.FinishReason);
            Assert.AreEqual("final answer", response.Messages.Single().Contents.Single().Text);
            Assert.AreEqual(12L, response.Usage?.InputTokens);
            Assert.AreEqual(34L, response.Usage?.OutputTokens);
            Assert.AreEqual(46L, response.Usage?.TotalTokens);
        });
    }

    [TestMethod]
    public async Task ProjectInferenceRouteUsesTheConfiguredProjectPath()
    {
        await WithKeyAsync(async _ =>
        {
            var options = Options() with { InferenceEndpoint = new Uri("https://foundry.example/api/projects/demo/openai/v1") };
            using var client = Client((request, _) =>
            {
                Assert.AreEqual("https://foundry.example/api/projects/demo/openai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
                return Task.FromResult(Json(HttpStatusCode.OK, Success));
            });
            var response = await Provider(client, options).ChatAsync(Request(), default);
            Assert.AreEqual("Phi-4-reasoning", response.Model);
        });
    }

    [TestMethod]
    public async Task NonStreamingToolCallsRemainAepDataForTheRuntimePipeline()
    {
        await WithKeyAsync(async _ =>
        {
            using var client = Client((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """
                {"choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"q\":\"term\"}"}}]},"finish_reason":"tool_calls"}]}
                """)));
            var request = Request() with
            {
                Tools = [new AepToolDefinition("lookup", "search", JsonSerializer.SerializeToElement(new { type = "object" }))]
            };
            var response = await Provider(client).ChatAsync(request, default);
            Assert.AreEqual(AepFinishReason.ToolCalls, response.FinishReason);
            var call = response.Messages.Single().Contents.Single().ToolCall!;
            Assert.AreEqual("call-1", call.Id);
            Assert.AreEqual("lookup", call.Name);
            Assert.AreEqual("term", call.Arguments.GetProperty("q").GetString());
        });
    }

    [TestMethod]
    public async Task UnsupportedOptionsAndNonTextContentFailBeforeNetworkAccess()
    {
        await WithKeyAsync(async _ =>
        {
            var calls = 0;
            using var client = Client((_, _) => { calls++; return Task.FromResult(Json(HttpStatusCode.OK, Success)); });
            var provider = Provider(client);
            var requests = new[]
            {
                Request() with { Options = new AepModelOptions { TopK = 10 } },
                Request() with { Options = new AepModelOptions { ResponseFormat = JsonSerializer.SerializeToElement(new { type = "json_object" }) } },
                Request() with { Options = new AepModelOptions { ResponseFormat = JsonSerializer.SerializeToElement(new { type = "text", schema = "ignored" }) } },
                Request() with { Options = new AepModelOptions { AdditionalOptions = new Dictionary<string, JsonElement> { ["reasoning_effort"] = JsonSerializer.SerializeToElement("high") } } },
                Request() with { Messages = [new AepMessage(AepRole.User, [new AepContent { Kind = AepContentKind.Image, MediaType = "image/png" }])] }
            };
            foreach (var request in requests)
            {
                var exception = await Assert.ThrowsAsync<AepServerException>(() => provider.ChatAsync(request, default));
                Assert.AreEqual("unsupported_option", exception.Code);
            }
            var oversized = Request() with
            {
                Messages = [new AepMessage(AepRole.User, [AepContent.FromText(new string('x', 1024 * 1024))])]
            };
            Assert.AreEqual("request_limit", (await Assert.ThrowsAsync<AepServerException>(() => provider.ChatAsync(oversized, default))).Code);
            Assert.AreEqual(0, calls);
        });
    }

    [TestMethod]
    public async Task ProviderFailuresAndContentFilterHaveStableSafeErrors()
    {
        await WithKeyAsync(async _ =>
        {
            var cases = new[]
            {
                (HttpStatusCode.BadRequest, "invalid_request"),
                (HttpStatusCode.Unauthorized, "authentication_failed"),
                (HttpStatusCode.Forbidden, "authorization_failed"),
                (HttpStatusCode.NotFound, "model_unavailable"),
                (HttpStatusCode.TooManyRequests, "rate_limited"),
                (HttpStatusCode.ServiceUnavailable, "provider_unavailable")
            };
            foreach (var (status, code) in cases)
            {
                using var client = Client((_, _) => Task.FromResult(Json(status, "{\"error\":{\"message\":\"sensitive-provider-body\"}}")));
                var exception = await Assert.ThrowsAsync<AepServerException>(() => Provider(client).ChatAsync(Request(), default));
                Assert.AreEqual(code, exception.Code);
                Assert.IsFalse(exception.Message.Contains("sensitive-provider-body", StringComparison.Ordinal));
            }
            using var filteredRequest = Client((_, _) => Task.FromResult(Json(HttpStatusCode.BadRequest, """
                {"error":{"code":"content_filter","message":"sensitive-provider-body"}}
                """)));
            using var filteredResponse = Client((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """
                {"choices":[{"message":{"role":"assistant","content":null},"finish_reason":"content_filter"}]}
                """)));
            Assert.AreEqual("content_filtered", (await Assert.ThrowsAsync<AepServerException>(() => Provider(filteredRequest).ChatAsync(Request(), default))).Code);
            Assert.AreEqual("content_filtered", (await Assert.ThrowsAsync<AepServerException>(() => Provider(filteredResponse).ChatAsync(Request(), default))).Code);
        });
    }

    [TestMethod]
    public async Task ChatBoundsResponsesAndPropagatesCallerCancellation()
    {
        await WithKeyAsync(async _ =>
        {
            using var largeClient = Client((_, _) => Task.FromResult(Json(HttpStatusCode.OK, new string('x', 2 * 1024 * 1024 + 1))));
            Assert.AreEqual("response_limit", (await Assert.ThrowsAsync<AepServerException>(() => Provider(largeClient).ChatAsync(Request(), default))).Code);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            using var client = Client(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Json(HttpStatusCode.OK, Success);
            });
            await Assert.ThrowsAsync<OperationCanceledException>(() => Provider(client).ChatAsync(Request(), cancelled.Token));
        });
    }

    [TestMethod]
    public async Task RedirectMalformedResponseAndTimeoutFailExplicitly()
    {
        await WithKeyAsync(async _ =>
        {
            using var redirected = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://attacker.example/collect") }
            }));
            Assert.AreEqual("provider_redirect_denied", (await Assert.ThrowsAsync<AepServerException>(() => Provider(redirected).ChatAsync(Request(), default))).Code);

            using var malformed = Client((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """
                {"choices":[{"message":{"role":"assistant","content":"text"},"finish_reason":"tool_calls"}]}
                """)));
            Assert.AreEqual("invalid_response", (await Assert.ThrowsAsync<AepServerException>(() => Provider(malformed).ChatAsync(Request(), default))).Code);

            using var slow = Client(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Json(HttpStatusCode.OK, Success);
            });
            var options = Options() with { RequestTimeout = TimeSpan.FromSeconds(1) };
            Assert.AreEqual("provider_timeout", (await Assert.ThrowsAsync<AepServerException>(() => Provider(slow, options).ChatAsync(Request(), default))).Code);
        });
    }

    private const string Success = """
        {"choices":[{"index":0,"message":{"role":"assistant","content":"answer"},"finish_reason":"stop"}]}
        """;

    private static AepChatRequest Request() => new("Phi-4-reasoning", [new AepMessage(AepRole.User, [AepContent.FromText("hello")])]);

    private static FoundryExtensionOptions Options() => new()
    {
        ProjectEndpoint = new Uri("https://foundry.example/api/projects/demo"),
        InferenceEndpoint = new Uri("https://foundry.example/openai/v1"),
        AuthenticationMode = FoundryAuthenticationMode.ApiKeyEnvironment
    };

    private static FoundryAepModelProvider Provider(HttpClient client, FoundryExtensionOptions? options = null)
    {
        options ??= Options();
        return new FoundryAepModelProvider(client, options, new FoundryRequestAuthenticator(options));
    }

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => new(new StubHandler(handler));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static async Task WithKeyAsync(Func<string, Task> action)
    {
        var previous = Environment.GetEnvironmentVariable("FOUNDRY_API_KEY");
        var key = Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable("FOUNDRY_API_KEY", key);
            await action(key);
        }
        finally { Environment.SetEnvironmentVariable("FOUNDRY_API_KEY", previous); }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
