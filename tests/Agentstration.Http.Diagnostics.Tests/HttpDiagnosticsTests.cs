using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Agentstration.Http.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentstration.Http.Diagnostics.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HttpDiagnosticsTests
{
    private const string RequestJson = "{\"input\":\"diagnostic-request\",\"api_key\":\"request-secret\",\"nested\":[{\"secretCapability\":\"grant-secret\"}]}";
    private const string ResponseJson = "{\"output\":\"diagnostic-response\",\"access_token\":\"response-secret\"}";

    [TestMethod]
    public async Task IncomingCapturePreservesBytesRedactsSecretsAndAttachesPayloadsToTrace()
    {
        using var traces = new Traces();
        var logs = new Logs();
        var builder = Builder(logs, request: true, response: true);
        await using var app = builder.Build();
        app.UseAgentstrationHttpDiagnostics();
        string? received = null;
        app.MapPost("/probe", async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            received = await reader.ReadToEndAsync(context.RequestAborted);
            context.Response.StatusCode = 422;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(ResponseJson, context.RequestAborted);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.PostAsync("/probe?token=query-secret", Json(RequestJson));
        Assert.AreEqual(RequestJson, received);
        Assert.AreEqual(ResponseJson, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var diagnostic = traces.Items.Single(item => item.OperationName == "http.incoming.diagnostics");
        Assert.HasCount(2, diagnostic.Events);
        Assert.AreEqual(ActivityStatusCode.Error, diagnostic.Status);
        AssertSafe(logs.Text + traces.Text);
        StringAssert.Contains(logs.Text, "diagnostic-request");
        StringAssert.Contains(logs.Text, "diagnostic-response");
        StringAssert.Contains(logs.Text, diagnostic.TraceId.ToString());
    }

    [TestMethod]
    public async Task IncomingPipeWriterPreservesBufferedOutputAndDuplicateJsonPropertiesAreSafe()
    {
        var logs = new Logs();
        var builder = Builder(logs, response: true);
        await using var app = builder.Build();
        app.UseAgentstrationHttpDiagnostics();
        const string value = "{\"token\":\"response-secret\",\"token\":\"response-secret\",\"output\":\"diagnostic-response\"}";
        app.MapGet("/probe", context =>
        {
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(value);
            bytes.CopyTo(context.Response.BodyWriter.GetMemory(bytes.Length));
            context.Response.BodyWriter.Advance(bytes.Length);
            return Task.CompletedTask;
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/probe");
        Assert.AreEqual(value, await response.Content.ReadAsStringAsync());
        StringAssert.Contains(logs.Text, "diagnostic-response");
        Assert.DoesNotContain("response-secret", logs.Text);
    }

    [TestMethod]
    public async Task IncomingResponseFlushesBeforeEndpointFinishes()
    {
        var logs = new Logs();
        var builder = Builder(logs, response: true);
        await using var app = builder.Build();
        app.UseAgentstrationHttpDiagnostics();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/probe", async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"output\":");
            await context.Response.Body.FlushAsync();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("\"diagnostic-response\"}");
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        try
        {
            using var response = await client.GetAsync("/probe", HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(10));
            using var stream = await response.Content.ReadAsStreamAsync();
            var prefix = new byte[10];
            await stream.ReadExactlyAsync(prefix).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("{\"output\":", Encoding.UTF8.GetString(prefix));
            release.SetResult();
            using var reader = new StreamReader(stream);
            Assert.AreEqual("\"diagnostic-response\"}", await reader.ReadToEndAsync());
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task OutgoingCapturePreservesBytesAndUsesCorrelatedResponseSpan()
    {
        using var traces = new Traces();
        var logs = new Logs();
        var builder = Builder(logs, request: true, response: true);
        string? received = null;
        builder.Services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => new Stub(async (request, ct) =>
        {
            received = await request.Content!.ReadAsStringAsync(ct);
            Assert.IsTrue(request.Options.TryGetValue(new HttpRequestOptionsKey<bool>("Agentstration.Http.Diagnostics.Active"), out var owner) && owner);
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = Json(ResponseJson) };
        }));
        await using var app = builder.Build();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://user:password@localhost/probe?token=query-secret") { Content = Json(RequestJson) };
        using var response = await client.SendAsync(request);
        Assert.AreEqual(RequestJson, received);
        Assert.AreEqual(ResponseJson, await response.Content.ReadAsStringAsync());
        var exchange = traces.Items.Single(item => item.OperationName == "http.outgoing.diagnostics");
        var payload = traces.Items.Single(item => item.OperationName == "http.response.payload");
        Assert.AreEqual(exchange.TraceId, payload.TraceId);
        Assert.AreEqual(exchange.SpanId, payload.ParentSpanId);
        AssertSafe(logs.Text + traces.Text);
        Assert.DoesNotContain("user:password", logs.Text + traces.Text);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task BodySwitchesAreIndependent(bool request, bool response)
    {
        var logs = new Logs();
        var builder = Builder(logs, request, response);
        AddProbe(builder);
        await using var app = builder.Build();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
        using var result = await client.PostAsync("http://localhost/probe", Json(RequestJson));
        _ = await result.Content.ReadAsStringAsync();
        Assert.AreEqual(request, logs.Text.Contains("diagnostic-request", StringComparison.Ordinal));
        Assert.AreEqual(response, logs.Text.Contains("diagnostic-response", StringComparison.Ordinal));
        Assert.AreEqual(request || response, logs.Items.Any(item => item.Contains("payload", StringComparison.Ordinal)));
        StringAssert.Contains(logs.Text, "HTTP outgoing");
    }

    [TestMethod]
    public async Task DisabledDiagnosticsLeaveTransportAndExistingActivityAlone()
    {
        using var traces = new Traces();
        var logs = new Logs();
        var builder = Builder(logs, enabled: false);
        AddProbe(builder);
        await using var app = builder.Build();
        using var parent = new Activity("existing-http-context").Start();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
        using var response = await client.PostAsync("http://localhost/probe", Json(RequestJson));
        Assert.AreEqual(ResponseJson, await response.Content.ReadAsStringAsync());
        Assert.IsEmpty(logs.Items);
        Assert.IsEmpty(traces.Items);
        Assert.AreSame(parent, Activity.Current);
    }

    [TestMethod]
    public async Task MetadataCanBeDisabledWithoutDisablingBodies()
    {
        var logs = new Logs();
        var builder = Builder(logs, response: true, metadata: false);
        AddProbe(builder);
        await using var app = builder.Build();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
        using var response = await client.GetAsync("http://localhost/probe");
        _ = await response.Content.ReadAsStringAsync();
        Assert.HasCount(1, logs.Items);
        StringAssert.Contains(logs.Text, "diagnostic-response");
    }

    [TestMethod]
    public async Task HeadersReadNeverConsumesResponseEarlyAndPartialDisposalOmitsBody()
    {
        var logs = new Logs();
        var stream = new CountingStream(Encoding.UTF8.GetBytes(ResponseJson));
        var builder = Builder(logs, response: true);
        builder.Services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => new Stub((_, _) =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        await using var app = builder.Build();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
        using (var response = await client.GetAsync("http://localhost/probe", HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.AreEqual(0, stream.BytesRead);
            var body = await response.Content.ReadAsStreamAsync();
            var bytes = new byte[8];
            Assert.AreEqual(8, await body.ReadAsync(bytes));
            Assert.AreEqual(8, stream.BytesRead);
        }
        Assert.IsTrue(stream.Disposed);
        StringAssert.Contains(logs.Text, "incomplete");
        Assert.DoesNotContain("diagnostic-response", logs.Text);
    }

    [TestMethod]
    public async Task StreamingToEndCapturesOnlyAfterConsumptionAndHonorsCancellation()
    {
        var logs = new Logs();
        var stream = new CountingStream(Encoding.UTF8.GetBytes(ResponseJson));
        var builder = Builder(logs, response: true);
        builder.Services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => new Stub((_, _) =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        await using var app = builder.Build();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
        using var response = await client.GetAsync("http://localhost/probe", HttpCompletionOption.ResponseHeadersRead);
        using var body = await response.Content.ReadAsStreamAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await body.ReadExactlyAsync(new byte[1], cancelled.Token));
        Assert.AreEqual(0, stream.BytesRead);
        using var reader = new StreamReader(body);
        Assert.AreEqual(ResponseJson, await reader.ReadToEndAsync());
        StringAssert.Contains(logs.Text, "diagnostic-response");
    }

    [TestMethod]
    [DataRow("application/json", "{\"output\":\"short\",\"token\":\"hidden\"}", false)]
    [DataRow("application/json", "invalid diagnostic-response", false)]
    [DataRow("text/event-stream", "data: diagnostic-response", false)]
    [DataRow("application/octet-stream", "diagnostic-response", false)]
    [DataRow("application/json", "{\"output\":\"diagnostic-response\"}", true)]
    public async Task UnsupportedInvalidAndEncodedBodiesAreOmitted(string contentType, string value, bool encoded)
    {
        var logs = new Logs();
        var builder = Builder(logs, response: true);
        builder.Services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => new Stub((_, _) =>
        {
            var content = new StringContent(value, Encoding.UTF8, contentType);
            if (encoded) content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        await using var app = builder.Build();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
        using var response = await client.GetAsync("http://localhost/probe");
        Assert.AreEqual(value, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("hidden", logs.Text);
        Assert.DoesNotContain("diagnostic-response", logs.Text);
    }

    [TestMethod]
    [DataRow('x', 100_000)]
    [DataRow('é', 130)]
    public async Task OversizedJsonIsOmittedWithoutLeakingUnredactedPrefix(char character, int count)
    {
        var logs = new Logs();
        var builder = Builder(logs, response: true, extra: new() { ["MaximumBodyBytes"] = "256" });
        var value = "{\"token\":\"response-secret\",\"content\":\"" + new string(character, count) + "\"}";
        AddProbe(builder, value);
        await using var app = builder.Build();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
        using var response = await client.GetAsync("http://localhost/probe");
        Assert.AreEqual(value, await response.Content.ReadAsStringAsync());
        StringAssert.Contains(logs.Text, "Truncated=True");
        Assert.DoesNotContain("response-secret", logs.Text);
        Assert.IsLessThan(1500, logs.Text.Length);
    }

    [TestMethod]
    [DataRow("http://localhost/excluded", false)]
    [DataRow("http://otherhost/probe", false)]
    [DataRow("http://localhost/probe/ignored", false)]
    [DataRow("http://localhost/probe/secrets/redeem", true)]
    [DataRow("http://localhost/probe/identity/login", true)]
    public async Task FiltersAndProtectedRoutesDoNotLeakPayloads(string uri, bool metadataExpected)
    {
        var logs = new Logs();
        var builder = Builder(logs, request: true, response: true, extra: new()
        {
            ["IncludedPaths:0"] = "/probe", ["ExcludedPaths:0"] = "/probe/ignored", ["IncludedHosts:0"] = "localhost"
        });
        AddProbe(builder);
        await using var app = builder.Build();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
        using var response = await client.PostAsync(uri, Json(RequestJson));
        _ = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(metadataExpected, !logs.Items.IsEmpty);
        Assert.DoesNotContain("diagnostic-request", logs.Text);
        Assert.DoesNotContain("diagnostic-response", logs.Text);
    }

    [TestMethod]
    public async Task ExcludedPathsComeFromConfigurationWithoutImplicitArrayEntries()
    {
        var logs = new Logs();
        var builder = Builder(logs, response: true, extra: new() { ["ExcludedPaths:0"] = "/unused" });
        AddProbe(builder);
        await using var app = builder.Build();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
        using var response = await client.GetAsync("http://localhost/health");
        _ = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(logs.Text, "diagnostic-response");
    }

    [TestMethod]
    public async Task TransportFailuresAreRecordedWithoutExceptionMessagesAndRethrown()
    {
        var logs = new Logs();
        var builder = Builder(logs);
        var failure = new HttpRequestException("query-secret");
        builder.Services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => new Stub((_, _) => throw failure));
        await using var app = builder.Build();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
        var thrown = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetAsync("http://localhost/probe"));
        Assert.AreSame(failure, thrown);
        StringAssert.Contains(logs.Text, "HttpRequestException");
        Assert.DoesNotContain("query-secret", logs.Text);
    }

    [TestMethod]
    [DataRow("Production", "CaptureResponseBody", "true")]
    [DataRow("Production", "CaptureRequestBody", "true")]
    [DataRow("Development", "MaximumBodyBytes", "1")]
    [DataRow("Development", "MaximumBodyBytes", "1048577")]
    [DataRow("Development", "IncludedPaths:0", "relative")]
    [DataRow("Development", "IncludedHosts:0", "https://localhost")]
    public void InvalidConfigurationFailsAtStartup(string environment, string key, string value)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => Builder(new Logs(), environment: environment, extra: new() { [key] = value }));
    }

    private static WebApplicationBuilder Builder(Logs logs, bool request = false, bool response = false,
        bool enabled = true, bool metadata = true, string environment = "Development", Dictionary<string, string?>? extra = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        var configuration = new Dictionary<string, string?>
        {
            ["Enabled"] = enabled.ToString(), ["CaptureRequestBody"] = request.ToString(),
            ["CaptureResponseBody"] = response.ToString(), ["LogMetadata"] = metadata.ToString()
        };
        if (extra is not null) foreach (var entry in extra) configuration[entry.Key] = entry.Value;
        builder.Configuration.AddInMemoryCollection(configuration.Select(entry =>
            new KeyValuePair<string, string?>(HttpDiagnosticsOptions.SectionName + ":" + entry.Key, entry.Value)));
        builder.AddAgentstrationHttpDiagnostics();
        return builder;
    }

    private static void AddProbe(WebApplicationBuilder builder, string value = ResponseJson) =>
        builder.Services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => new Stub(async (request, ct) =>
        {
            if (request.Content is not null) _ = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(value) };
        }));

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private static void AssertSafe(string text)
    {
        foreach (var secret in new[] { "request-secret", "response-secret", "grant-secret", "query-secret" })
            Assert.DoesNotContain(secret, text);
        StringAssert.Contains(text, "REDACTED");
    }

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class Logs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Items { get; } = new();
        public string Text => string.Join('\n', Items);
        public ILogger CreateLogger(string categoryName) => categoryName.StartsWith("Agentstration.Http.Diagnostics", StringComparison.Ordinal) ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Items.Enqueue(formatter(state, exception));
        public void Dispose() { }
    }

    private sealed class Traces : IDisposable
    {
        public ConcurrentQueue<Activity> Items { get; } = new();
        private readonly ActivityListener listener;
        public string Text => string.Join('\n', Items.SelectMany(item => item.Events.SelectMany(evt => evt.Tags).Concat(item.TagObjects)).Select(tag => tag.Value));
        public Traces()
        {
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == HttpDiagnosticsExtensions.ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Items.Enqueue
            };
            ActivitySource.AddActivityListener(listener);
        }
        public void Dispose() => listener.Dispose();
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = base.Read(buffer.Span);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); }
    }
}
