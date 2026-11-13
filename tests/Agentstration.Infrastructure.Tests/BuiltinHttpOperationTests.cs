using System.Text.Json;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Resources;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class BuiltinHttpOperationTests
{
    private static ConnectedServiceOperationCandidate Discover(string url) => BuiltinHttpOperation.Discover(url, null!);

    [TestMethod]
    public void ExampleDiscoversOnlyGetStructureAndNoSampleValues()
    {
        var operation = Discover("https://news.google.com/rss/search?q=Nantes+when:1d&hl=fr&gl=FR&ceid=FR:fr");
        Assert.AreEqual("GET /rss/search", operation.DisplayName);
        var definition = operation.Definition;
        Assert.AreEqual("GET", definition.GetProperty("method").GetString());
        Assert.AreEqual("https://news.google.com/rss/search", definition.GetProperty("endpoint").GetString());
        CollectionAssert.AreEqual(new[] { "ceid", "gl", "hl", "q" }, definition.GetProperty("queryParameters").EnumerateArray().Select(item => item.GetString()).ToArray());
        var schema = definition.GetProperty("inputSchema");
        Assert.IsFalse(schema.TryGetProperty("required", out _));
        foreach (var input in schema.GetProperty("properties").EnumerateObject())
        {
            Assert.AreEqual("string", input.Value.GetProperty("type").GetString());
            Assert.IsFalse(input.Value.TryGetProperty("default", out _));
            Assert.IsFalse(input.Value.TryGetProperty("examples", out _));
        }
        Assert.DoesNotContain("Nantes", definition.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("when:1d", definition.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("FR:fr", definition.GetRawText(), StringComparison.Ordinal);
        Assert.AreEqual("https://news.google.com/rss/search", BuiltinHttpOperation.RequestUri(definition, JsonSerializer.SerializeToElement(new { })).AbsoluteUri);
    }

    [TestMethod]
    public void QueryOrderAndValuesDoNotChangeIdentityOrDefinitionButNamesChangeDefinition()
    {
        var original = Discover("https://example.test/search?q=first&hl=fr");
        var changed = Discover("https://example.test/search?hl=en&q=second");
        Assert.AreEqual(original.ExternalId, changed.ExternalId);
        Assert.IsTrue(JsonElement.DeepEquals(original.Definition, changed.Definition));
        var extra = Discover("https://example.test/search?q=second&hl=en&page=2");
        Assert.AreEqual(original.ExternalId, extra.ExternalId);
        Assert.IsFalse(JsonElement.DeepEquals(original.Definition, extra.Definition));
        Assert.AreNotEqual(original.ExternalId, Discover("https://other.test/search?q=first&hl=fr").ExternalId);
        Assert.AreNotEqual(original.ExternalId, Discover("https://example.test/other?q=first&hl=fr").ExternalId);
    }

    [TestMethod]
    public void ParametersAreDecodedOnceAndOnlyConfiguredValuesAreEncodedForInvocation()
    {
        var operation = Discover("https://example.test/search?%71=sample&x+y=discard&empty=&nested%2520name=discard");
        var values = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["q"] = "Rennes when:1d & category=local+news / été",
            ["x y"] = "+", ["empty"] = "", ["nested%20name"] = "value"
        });
        var url = BuiltinHttpOperation.RequestUri(operation.Definition, values);
        Assert.AreEqual("https://example.test/search?empty=&nested%2520name=value&q=Rennes%20when%3A1d%20%26%20category%3Dlocal%2Bnews%20%2F%20%C3%A9t%C3%A9&x%20y=%2B", url.AbsoluteUri);
        Assert.DoesNotContain("sample", url.AbsoluteUri, StringComparison.Ordinal);
        DataSourceDatasetCompositionService.ValidateValue(operation.Definition.GetProperty("inputSchema"), JsonSerializer.SerializeToElement(new { q = "" }));
    }

    [TestMethod]
    public void EmptyQueryProducesEmptyInputOperation()
    {
        var operation = Discover("https://example.test/feed");
        Assert.IsEmpty(operation.Definition.GetProperty("queryParameters").EnumerateArray());
        Assert.AreEqual("https://example.test/feed", BuiltinHttpOperation.RequestUri(operation.Definition, JsonSerializer.SerializeToElement(new { })).AbsoluteUri);
    }

    [TestMethod]
    [DataRow("not a URL", "http_url_invalid")]
    [DataRow("ftp://example.test/feed", "http_url_invalid")]
    [DataRow("https://user:password@example.test/feed", "http_url_invalid")]
    [DataRow("https://example.test/feed#fragment", "http_url_invalid")]
    [DataRow("https://example.test/feed?q=a&q=b", "http_query_repeated_parameter")]
    [DataRow("https://example.test/feed?q=a&%71=b", "http_query_repeated_parameter")]
    [DataRow("https://example.test/feed?=value", "http_query_invalid")]
    [DataRow("https://example.test/feed?q=a&", "http_query_invalid")]
    [DataRow("https://example.test/feed?%0A=value", "http_query_invalid")]
    [DataRow("https://example.test/feed?%FF=value", "http_query_invalid")]
    [DataRow("https://example.test/feed?%zz=value", "http_url_invalid")]
    public void UnsupportedUrlsAndAmbiguousInputsFailExplicitly(string url, string code)
    {
        var failure = Assert.Throws<ConnectedServiceOperationDiscoveryException>(() => Discover(url));
        Assert.AreEqual(code, failure.Code);
    }

    [TestMethod]
    public void InputAndUrlBoundsRejectInvalidCalls()
    {
        var operation = Discover("https://example.test/feed?q=sample");
        foreach (var invalid in new[] { "{\"q\":3}", "{\"q\":[\"a\",\"b\"]}", "{\"url\":\"https://other.test\"}", "{\"unknown\":\"value\"}" })
            Assert.Throws<DataSourceValidationException>(() => BuiltinHttpOperation.RequestUri(operation.Definition, JsonDocument.Parse(invalid).RootElement));
        Assert.Throws<ConnectedServiceOperationDiscoveryException>(() => Discover("https://example.test/" + new string('a', 2048)));
        Assert.Throws<ConnectedServiceOperationDiscoveryException>(() => Discover("https://example.test/?" + string.Join('&', Enumerable.Range(0, 65).Select(index => $"q{index}="))));
        Assert.Throws<ConnectedServiceOperationDiscoveryException>(() => BuiltinHttpOperation.RequestUri(operation.Definition,
            JsonSerializer.SerializeToElement(new { q = new string('é', 1000) })));
    }

    [TestMethod]
    public async Task LateBoundConnectionCannotRedirectAnOperationOrReuseExampleValues()
    {
        var service = new ConnectedServiceResource
        {
            Metadata = new() { Name = "http-fixture" }, ScopeRef = ResourceScopeRef.Instance,
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ConnectedServiceResourceKinds.ConnectedService,
            Definition = new() { DisplayName = "HTTP", Type = new()
            {
                Type = new("http-endpoint", ResourceScopeRef.Instance), TypeUid = Guid.NewGuid(),
                Version = "1.1.0", SchemaDigest = "digest"
            }, Options = JsonSerializer.SerializeToElement(new { }) }
        };
        var type = new ConnectedServiceOperationTypeReference
        {
            Type = new("http-get", ResourceScopeRef.Instance), TypeUid = Guid.NewGuid(), Version = "1.0.0", SchemaDigest = "digest"
        };
        var candidate = BuiltinHttpOperation.Discover("https://example.test/search?q=sample", type);
        var operation = new ConnectedServiceOperationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ConnectedServiceResourceKinds.ConnectedServiceOperation,
            Metadata = new() { Name = "http-operation" }, ScopeRef = ResourceScopeRef.Instance,
            Definition = new() { DisplayName = candidate.DisplayName, ExternalId = candidate.ExternalId,
                Type = type, Definition = candidate.Definition, DefinitionDigest = "digest", Provenance = null!,
                Service = new() { Service = new(service.Address, ResourceScopeRef.Instance), ServiceUid = service.Uid, Generation = 1 } },
            ObservedState = new() { Availability = ConnectedServiceOperationAvailability.Available, ObservedAt = DateTimeOffset.UnixEpoch }
        };
        var resolver = new ConnectionResolver(service, "https://example.test/search?q=changed-sample");
        var fetcher = new Fetcher();
        var executor = new BuiltinHttpOperationExecutor(resolver, fetcher);
        var context = new ConnectedServiceResolutionContext(ResourceScopeRef.Instance, operation.Address);
        var result = await executor.AcquireAsync(service, operation, JsonSerializer.SerializeToElement(new { q = "configured" }), context, default);
        Assert.AreEqual("https://example.test/search?q=configured", fetcher.Request!.Url.AbsoluteUri);
        Assert.AreEqual(0, fetcher.Request.MaximumRedirects);
        Assert.AreEqual("application/rss+xml", result.GetProperty("mediaType").GetString());
        Assert.AreEqual("<rss/>", result.GetProperty("content").GetString());
        await executor.AcquireAsync(service, operation, JsonSerializer.SerializeToElement(new { }), context, default);
        Assert.AreEqual("", fetcher.Request.Url.Query);
        foreach (var changed in new[] { "https://foreign.test/search?q=sample", "https://example.test/other?q=sample", "https://example.test/search?q=sample&extra=value" })
        {
            resolver.Sample = changed;
            var failure = await Assert.ThrowsAsync<DataSourceAcquisitionException>(() =>
                executor.AcquireAsync(service, operation, JsonSerializer.SerializeToElement(new { q = "configured" }), context, default));
            Assert.AreEqual("http_operation_changed", failure.Code);
        }
        Assert.AreEqual(2, fetcher.Calls);
    }

    private sealed class ConnectionResolver(ConnectedServiceResource service, string sample) : IConnectedServiceResolver
    {
        public string Sample { get; set; } = sample;
        public Task<ResolvedConnectedService?> ResolveAsync(ConnectedServiceReference reference,
            ConnectedServiceResolutionContext context, CancellationToken cancellationToken = default) => Task.FromResult<ResolvedConnectedService?>(new()
            {
                Reference = reference, Type = service.Definition.Type, Options = service.Definition.Options, Exports = [],
                Inputs = [new() { Id = "baseUrl", Type = ConnectedServiceValueType.Text, Value = JsonSerializer.SerializeToElement(Sample) }]
            });
    }

    private sealed class Fetcher : IHttpContentFetcher
    {
        public HttpFetchRequest? Request { get; private set; }
        public int Calls { get; private set; }
        public Task<HttpFetchResult> FetchAsync(HttpFetchRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            return Task.FromResult(new HttpFetchResult(request.Url, "rss.xml", "application/rss+xml", "<rss/>"u8.ToArray()));
        }
    }
}
