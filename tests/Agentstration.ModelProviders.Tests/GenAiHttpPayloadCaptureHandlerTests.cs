using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Agentstration.ModelProviders;
using Microsoft.Extensions.Logging;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class GenAiHttpPayloadCaptureHandlerTests
{
    [TestMethod]
    public async Task CaptureLogsRedactedJsonAndPreservesTransportBodies()
    {
        const string requestJson = "{\"model\":\"qwen3\",\"messages\":[{\"content\":\"inspect this prompt\"}],\"api_key\":\"request-secret\"}";
        const string responseJson = "{\"message\":{\"content\":\"answer\"},\"access_token\":\"response-secret\"}";
        var logger = new RecordingLogger();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GenAiHttpPayloadCaptureHandler.TelemetrySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        string? transportedRequest = null;
        using var handler = new GenAiHttpPayloadCaptureHandler(
            new GenAiObservabilityOptions
            {
                HttpPayloadCapture = new HttpPayloadCaptureOptions
                {
                    Enabled = true,
                    CaptureResponse = true,
                    MaximumBodyLength = 4096
                }
            },
            logger)
        {
            InnerHandler = new StubHandler(async (request, cancellationToken) =>
            {
                transportedRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };
            })
        };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/chat?api_key=query-secret")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request);
        var transportedResponse = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(requestJson, transportedRequest);
        StringAssert.Contains(transportedResponse, "response-secret");
        Assert.HasCount(2, logger.Messages);
        var logs = string.Join(' ', logger.Messages);
        StringAssert.Contains(logs, "inspect this prompt");
        StringAssert.Contains(logs, "answer");
        StringAssert.Contains(logs, "***REDACTED***");
        Assert.IsFalse(logs.Contains("request-secret", StringComparison.Ordinal));
        Assert.IsFalse(logs.Contains("response-secret", StringComparison.Ordinal));
        Assert.IsFalse(logs.Contains("query-secret", StringComparison.Ordinal));
        Assert.HasCount(1, stopped);
        var traceData = string.Join(' ', stopped.Single().Events.SelectMany(item => item.Tags).Select(tag => $"{tag.Key}={tag.Value}"));
        StringAssert.Contains(traceData, "inspect this prompt");
        Assert.IsFalse(traceData.Contains("request-secret", StringComparison.Ordinal));
        Assert.IsFalse(traceData.Contains("response-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CaptureTruncatesPayloadAndCanBeDisabled()
    {
        var logger = new RecordingLogger();
        var options = new GenAiObservabilityOptions
        {
            HttpPayloadCapture = new HttpPayloadCaptureOptions { Enabled = true, MaximumBodyLength = 256 }
        };
        using var handler = new GenAiHttpPayloadCaptureHandler(options, logger)
        {
            InnerHandler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
        };
        using var client = new HttpClient(handler);
        using var content = JsonContent.Create(new { content = new string('x', 1000) });

        _ = await client.PostAsync("http://localhost/api/chat", content);

        Assert.HasCount(1, logger.Messages);
        StringAssert.Contains(logger.Messages[0], "...[truncated]");

        var disabledLogger = new RecordingLogger();
        using var disabledHandler = new GenAiHttpPayloadCaptureHandler(new GenAiObservabilityOptions(), disabledLogger)
        {
            InnerHandler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
        };
        using var disabledClient = new HttpClient(disabledHandler);
        using var secondContent = JsonContent.Create(new { content = "not logged" });
        _ = await disabledClient.PostAsync("http://localhost/api/chat", secondContent);
        Assert.IsEmpty(disabledLogger.Messages);
    }

    [TestMethod]
    public void OptionsRejectPayloadCaptureOutsideDevelopmentAndInvalidLimits()
    {
        var enabled = new GenAiObservabilityOptions
        {
            HttpPayloadCapture = new HttpPayloadCaptureOptions { Enabled = true }
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => enabled.Validate(isDevelopment: false));
        enabled.Validate(isDevelopment: true);

        var invalidLimit = new GenAiObservabilityOptions
        {
            HttpPayloadCapture = new HttpPayloadCaptureOptions { Enabled = true, MaximumBodyLength = 128 }
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => invalidLimit.Validate(isDevelopment: true));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responseFactory(request, cancellationToken);
    }

    private sealed class RecordingLogger : ILogger<GenAiHttpPayloadCaptureHandler>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
