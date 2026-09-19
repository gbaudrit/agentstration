using System.Net;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.Foundry;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
[DoNotParallelize]
public sealed class FoundryStreamingTests
{
    [TestMethod]
    public async Task StreamsTextUsageFinishAndGovernedToolExchange()
    {
        await WithKeyAsync(async () =>
        {
            var calls = 0;
            using var client = Client(async (request, token) =>
            {
                calls++;
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var body = json.RootElement;
                Assert.IsTrue(body.GetProperty("stream").GetBoolean());
                Assert.IsTrue(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
                Assert.AreEqual("lookup", body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
                Assert.AreEqual("call-1", body.GetProperty("messages")[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
                Assert.AreEqual("call-1", body.GetProperty("messages")[2].GetProperty("tool_call_id").GetString());
                return Stream("""
                    data: {"choices":[{"delta":{"role":"assistant","content":"hel"},"finish_reason":null}]}

                    data: {"choices":[{"delta":{"content":"lo"},"finish_reason":null}]}

                    data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-2","type":"function","function":{"name":"lookup","arguments":"{\"q\":"}}]},"finish_reason":null}]}

                    data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"term\"}"}}]},"finish_reason":"tool_calls"}]}

                    data: {"choices":[],"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":7}}

                    data: [DONE]

                    """);
            });
            var provider = Provider(client);
            Assert.IsTrue(provider.Descriptor.Capabilities.Streaming);
            Assert.IsTrue(provider.Descriptor.Capabilities.Tools);
            var request = Request() with
            {
                Tools = [new("lookup", "search", JsonSerializer.SerializeToElement(new { type = "object" }))],
                Messages =
                [
                    new(AepRole.User, [AepContent.FromText("hello")]),
                    new(AepRole.Assistant, [new AepContent { Kind = AepContentKind.ToolCall,
                        ToolCall = new("call-1", "lookup", JsonSerializer.SerializeToElement(new { q = "prior" })) }]),
                    new(AepRole.Tool, [new AepContent { Kind = AepContentKind.ToolResult,
                        ToolResult = new("call-1", JsonSerializer.SerializeToElement("done")) }])
                ]
            };
            var updates = await CollectAsync(provider, request);
            Assert.AreEqual(1, calls);
            Assert.AreEqual("hello", string.Concat(updates.SelectMany(x => x.Contents).Where(x => x.Kind == AepContentKind.Text).Select(x => x.Text)));
            var call = updates.SelectMany(x => x.Contents).Single(x => x.Kind == AepContentKind.ToolCall).ToolCall!;
            Assert.AreEqual("call-2", call.Id);
            Assert.AreEqual("term", call.Arguments.GetProperty("q").GetString());
            Assert.AreEqual(3L, updates[^2].Usage?.InputTokens);
            Assert.AreEqual(4L, updates[^2].Usage?.OutputTokens);
            Assert.AreEqual(AepFinishReason.ToolCalls, updates[^1].FinishReason);
        });
    }

    [TestMethod]
    public async Task StreamingStructuredOutputChecksCurrentDeploymentBeforeInference()
    {
        await WithKeyAsync(async () =>
        {
            var inferenceCalls = 0;
            using var client = Client(async (request, token) =>
            {
                if (request.Method == HttpMethod.Get)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                        """{"value":[{"type":"ModelDeployment","name":"deployment","capabilities":{"chat":true,"jsonObject":true}}]}""",
                        Encoding.UTF8, "application/json") };
                inferenceCalls++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.AreEqual("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
                return Stream("data: {\"choices\":[{\"delta\":{\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
            });
            var request = Request() with { Options = new AepModelOptions
            { ResponseFormat = JsonSerializer.SerializeToElement(new { type = "json_object" }) } };
            var updates = await CollectAsync(Provider(client), request);
            Assert.AreEqual("{}", string.Concat(updates.SelectMany(update => update.Contents).Select(content => content.Text)));
            Assert.AreEqual(1, inferenceCalls);
        });
    }

    [TestMethod]
    public async Task MalformedIncompleteAndOversizedStreamsFailWithoutRetry()
    {
        await WithKeyAsync(async () =>
        {
            var cases = new[]
            {
                ("data: nonsense\n\n", "invalid_response"),
                ("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\n", "invalid_response"),
                ("data: {\"choices\":[{\"delta\":{\"content\":{\"bad\":true}},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", "invalid_response"),
                ("data: " + new string('x', 128 * 1024) + "\n\n", "response_limit")
            };
            foreach (var (body, expected) in cases)
            {
                var calls = 0;
                using var client = Client((_, _) => { calls++; return Task.FromResult(Stream(body)); });
                var exception = await Assert.ThrowsAsync<AepServerException>(async () => await CollectAsync(Provider(client), Request()));
                Assert.AreEqual(expected, exception.Code);
                Assert.AreEqual(1, calls);
            }
        });
    }

    [TestMethod]
    public async Task InterruptedStreamFailsAfterFirstDeltaWithoutReissuingRequest()
    {
        await WithKeyAsync(async () =>
        {
            var calls = 0;
            using var client = Client((_, _) =>
            {
                calls++;
                return Task.FromResult(Stream("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\n"));
            });
            await using var updates = Provider(client).ChatStreamingAsync(Request(), default).GetAsyncEnumerator();
            Assert.IsTrue(await updates.MoveNextAsync());
            Assert.AreEqual("partial", updates.Current.Contents.Single().Text);
            var exception = await Assert.ThrowsAsync<AepServerException>(async () => { _ = await updates.MoveNextAsync(); });
            Assert.AreEqual("invalid_response", exception.Code);
            Assert.AreEqual(1, calls);
        });
    }

    [TestMethod]
    public async Task CancellationAndUnsupportedOptionsDoNotInvokeOrRetryProvider()
    {
        await WithKeyAsync(async () =>
        {
            var calls = 0;
            using var client = Client(async (_, token) =>
            {
                calls++;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Stream("");
            });
            var provider = Provider(client);
            var bad = Request() with { Options = new AepModelOptions
                { ResponseFormat = JsonSerializer.SerializeToElement(new { type = "json_object", extra = true }) } };
            Assert.AreEqual("unsupported_option", (await Assert.ThrowsAsync<AepServerException>(
                async () => await CollectAsync(provider, bad))).Code);
            Assert.AreEqual(0, calls);
            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await CollectAsync(provider, Request(), cancellation.Token));
            Assert.AreEqual(1, calls);
        });
    }

    private static async Task<List<AepChatUpdate>> CollectAsync(FoundryAepModelProvider provider,
        AepChatRequest request, CancellationToken token = default)
    {
        var updates = new List<AepChatUpdate>();
        await foreach (var update in provider.ChatStreamingAsync(request, token)) updates.Add(update);
        return updates;
    }

    private static AepChatRequest Request() => new("deployment", [new AepMessage(AepRole.User, [AepContent.FromText("hello")])],
        BoundValues: BoundValues());
    private static FoundryAepModelProvider Provider(HttpClient client)
    {
        return new(client, new FoundryExtensionOptions(), new FoundryBoundConnectionResolver(Client((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(
                new AepSecretAccessResponse(AepProtocol.SecretAccessVersion, Convert.ToBase64String(Encoding.UTF8.GetBytes("offline-test-key"))),
                AepProtocol.JsonOptions), Encoding.UTF8, "application/json") }))));
    }
    private static IReadOnlyList<AepBoundValue> BoundValues() =>
    [
        AepBoundValue.Inline(FoundryValueRequirements.ProjectEndpoint, JsonSerializer.SerializeToElement("https://foundry.example/api/projects/demo")),
        AepBoundValue.Inline(FoundryValueRequirements.InferenceEndpoint, JsonSerializer.SerializeToElement("https://foundry.example/openai/v1")),
        AepBoundValue.Inline(FoundryValueRequirements.AuthenticationMode, JsonSerializer.SerializeToElement("ApiKey")),
        AepBoundValue.Secured(FoundryValueRequirements.Credential, new AepSecretAccessGrant(AepProtocol.SecretAccessVersion,
            new Uri("https://secrets.test/api/aep/secrets/redeem"), "Agentstration.Extensions.Foundry",
            FoundryValueRequirements.Credential, "execution", "capability"))
    ];
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new StubHandler(handler));
    private static HttpResponseMessage Stream(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body + "\n\n", Encoding.UTF8, "text/event-stream") };
    private static async Task WithKeyAsync(Func<Task> action)
    {
        await action();
    }
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            handler(request, token);
    }
}
