using System.Net;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Aep.Validation;
using Agentstration.Aep.Inspector;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Aep.Tests;

[TestClass]
public sealed class AepConformanceTests
{
    [TestMethod]
    public async Task CanonicalClientDiscoversCapabilitiesAndHealth()
    {
        await using var factory = new WebApplicationFactory<global::Program>();
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);

        var manifest = await client.GetManifestAsync();
        var capabilities = await client.GetCapabilitiesAsync();
        var health = await client.GetHealthAsync();

        Assert.AreEqual(AepProtocol.Version, manifest.ProtocolVersion);
        Assert.AreEqual("sample.hello", manifest.Extension.Id);
        Assert.AreEqual("1.0", capabilities[AepCapabilityNames.Health].Version);
        Assert.AreEqual("available", health.Status);
    }

    [TestMethod]
    public async Task LegacyDiscoveryAliasReturnsTheCanonicalManifest()
    {
        await using var factory = new WebApplicationFactory<global::Program>();
        using var httpClient = factory.CreateClient();

        var canonical = await httpClient.GetStringAsync(AepProtocol.DiscoveryPath);
        var legacy = await httpClient.GetStringAsync(AepProtocol.LegacyDiscoveryPath);

        Assert.AreEqual(canonical, legacy);
    }

    [TestMethod]
    public async Task ValidatorAcceptsTheGenericSample()
    {
        await using var factory = new WebApplicationFactory<global::Program>();
        using var httpClient = factory.CreateClient();

        var result = await new AepValidator().ValidateAsync(new AepClient(httpClient));

        Assert.IsTrue(result.IsValid);
        Assert.IsEmpty(result.Issues);
    }

    [TestMethod]
    public async Task TracingRedactsSensitiveHeadersAndJsonValues()
    {
        var sink = new MemoryTraceSink();
        using var handler = new AepTracingHandler(sink) { InnerHandler = new StaticHandler() };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://extension/test") { Content = new StringContent("{\"apiKey\":\"secret-value\"}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", "secret-token");

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("***", sink.Trace!.RequestHeaders["Authorization"]);
        StringAssert.Contains(sink.Trace.RequestBody, "\"apiKey\":\"***\"");
        Assert.IsFalse(sink.Trace.RequestBody!.Contains("secret-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CoreAssembliesDoNotReferenceAgentstrationApplicationProjects()
    {
        var forbidden = new[]
        {
            "Agentstration.Management",
            "Agentstration.Runtime",
            "Agentstration.Infrastructure",
            "Agentstration.Web",
            "Agentstration.Workplace"
        };
        var assemblies = new[] { typeof(AepProtocol).Assembly, typeof(AepClient).Assembly, typeof(AepValidator).Assembly };
        var references = assemblies.SelectMany(value => value.GetReferencedAssemblies()).Select(value => value.Name ?? "").ToArray();

        Assert.IsFalse(references.Any(reference => forbidden.Any(value => reference.Contains(value, StringComparison.Ordinal))));
    }

    [TestMethod]
    public async Task InspectorSessionExercisesGenericModelProviderWithStreamingAndTraces()
    {
        await using var factory = new WebApplicationFactory<global::Aep.Samples.ModelProvider.Program>();
        _ = factory.CreateClient();
        await using var session = new InspectorSession(NullLoggerFactory.Instance, () => factory.Server.CreateHandler());

        var snapshot = await session.ConnectAsync("http://extension");
        var provider = await session.InspectProviderAsync("echo");
        var updates = new List<string>();
        var response = await session.ChatAsync("echo", "echo-1", "hello", null, 0.2f, 128, true, value => { updates.Add(value); return Task.CompletedTask; });

        Assert.IsTrue(snapshot.Manifest.Capabilities.ContainsKey(AepCapabilityNames.ModelProvider));
        Assert.AreEqual("available", provider.Health.Status);
        Assert.AreEqual("echo-1", provider.Models.Single().Id);
        Assert.AreEqual("Echo: hello", response.Text);
        Assert.AreEqual("Echo: hello", updates.Last());
        Assert.IsTrue(session.Traces.Any(value => value.Url?.AbsolutePath.EndsWith("/chat/stream", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public async Task InspectorSessionDiscoversAndInvokesGenericMcpTool()
    {
        await using var factory = new WebApplicationFactory<global::Aep.Samples.Tools.Program>();
        _ = factory.CreateClient();
        await using var session = new InspectorSession(NullLoggerFactory.Instance, () => factory.Server.CreateHandler());

        var snapshot = await session.ConnectAsync("http://extension");
        var tools = await session.LoadToolsAsync();
        var result = await session.InvokeToolAsync("text.repeat", "{\"text\":\"hi\",\"count\":2}");

        Assert.IsTrue(snapshot.Manifest.Capabilities.ContainsKey(AepCapabilityNames.Tools));
        Assert.AreEqual("text.repeat", tools.Single().Id);
        StringAssert.Contains(result.RawResult, "hi hi");
        Assert.IsTrue(session.Traces.Any(value => value.Url?.AbsolutePath == "/mcp"));
    }

    private sealed class MemoryTraceSink : IAepHttpTraceSink
    {
        public AepHttpTrace? Trace { get; private set; }
        public ValueTask RecordAsync(AepHttpTrace trace, CancellationToken cancellationToken = default) { Trace = trace; return ValueTask.CompletedTask; }
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { token = "response-secret" }), Encoding.UTF8, "application/json")
        });
    }
}
