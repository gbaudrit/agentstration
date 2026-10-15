using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Client;
using Agentstration.Extensions.OpenApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.OpenApi.Tests;

[TestClass]
public sealed class OpenApiExtensionTests
{
    private const string JsonContract = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Orders", "version": "1.0" },
          "servers": [{ "url": "https://api.example.test" }],
          "paths": {
            "/orders/{id}": {
              "get": {
                "operationId": "getOrder",
                "summary": "Get one order",
                "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }],
                "responses": { "200": { "description": "OK" } }
              }
            }
          }
        }
        """;

    private const string YamlContract = """
        openapi: 3.1.0
        info:
          title: Catalog
          version: '1.0'
        paths:
          /items:
            post:
              operationId: createItem
              summary: Create item
              requestBody:
                required: true
                content:
                  application/json:
                    schema: { type: object }
              responses:
                '201': { description: Created }
        """;

    [TestMethod]
    public void ParserAcceptsOpenApi30JsonAnd31Yaml()
    {
        var parser = Parser();
        var json = parser.Parse(JsonContract, "application/json", new("https://origin.example.test/openapi.json"));
        var yaml = parser.Parse(YamlContract, "application/yaml", new("https://origin.example.test/openapi.yaml"));

        Assert.AreEqual("3.0.3", json.Descriptor.OpenApiVersion);
        Assert.IsTrue(json.Descriptor.Operations.Single().Eligible);
        Assert.IsTrue(json.Descriptor.Operations.Single().ReadOnly);
        Assert.AreEqual("3.1.0", yaml.Descriptor.OpenApiVersion);
        Assert.IsFalse(yaml.Descriptor.Operations.Single().ReadOnly);
        Assert.AreEqual("createItem", yaml.Descriptor.Operations.Single().OperationId);
    }

    [TestMethod]
    public void ParserRetainsMissingAndDuplicateOperationIdsAsIneligible()
    {
        const string contract = """
            {"openapi":"3.1.0","info":{"title":"x","version":"1"},"paths":{
              "/a":{"get":{"operationId":"same","responses":{"200":{"description":"ok"}}}},
              "/b":{"get":{"operationId":"same","responses":{"200":{"description":"ok"}}}},
              "/c":{"get":{"responses":{"200":{"description":"ok"}}}}
            }}
            """;
        var operations = Parser().Parse(contract, "application/json", new("https://origin.example.test/openapi.json")).Descriptor.Operations;

        Assert.IsTrue(operations.All(value => !value.Eligible));
        Assert.HasCount(2, operations.Where(value => value.Diagnostics.Any(issue => issue.Code == "openapi_operation_id_duplicate")));
        Assert.HasCount(1, operations.Where(value => value.Diagnostics.Any(issue => issue.Code == "openapi_operation_id_required")));
    }

    [TestMethod]
    public void ParserEnforcesReferencePolicyAndVersion()
    {
        AssertCode("openapi_version_unsupported", """{"openapi":"2.0","info":{"title":"x","version":"1"},"paths":{}}""");
        AssertCode("openapi_reference_unresolved", ContractWithSchema("#/components/schemas/Missing"));
        AssertCode("openapi_external_reference_denied", ContractWithSchema("other.yaml#/Thing"));
        AssertCode("openapi_reference_scheme_unsupported", ContractWithSchema("file:///tmp/schema.json"));
        AssertCode("openapi_reference_cycle", """
            {"openapi":"3.1.0","info":{"title":"x","version":"1"},"paths":{},"components":{"schemas":{
              "A":{"$ref":"#/components/schemas/B"},"B":{"$ref":"#/components/schemas/A"}
            }}}
            """);
    }

    [TestMethod]
    public async Task DiscoveryChecksOnlyExplicitCandidatesAndReturnsExactContentAndHierarchicalChoices()
    {
        var requests = new List<Uri>();
        var service = Service((request, _) =>
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(request.RequestUri!.AbsolutePath == "/openapi.json"
                ? Response(JsonContract, "application/json")
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var result = await service.DiscoverAsync("https://origin.example.test/", ["/missing", "/openapi.json"], "corr-1", default);

        Assert.HasCount(2, requests);
        Assert.HasCount(1, result.Contracts);
        Assert.AreEqual(JsonContract, result.Contracts[0].Content);
        Assert.AreEqual("corr-1", result.CorrelationId);
        var contract = result.Choices.Single(value => value.Metadata["kind"] == "contract");
        var operation = result.Choices.Single(value => value.Metadata["kind"] == "operation");
        Assert.IsFalse(contract.Eligible);
        Assert.IsTrue(operation.Eligible);
        Assert.AreEqual(contract.Id, operation.ParentId);
    }

    [TestMethod]
    public async Task DiscoveryRejectsCrossOriginAndExcessCandidates()
    {
        var service = Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var crossOrigin = await Assert.ThrowsAsync<OpenApiExtensionException>(() =>
            service.DiscoverAsync("https://origin.example.test/", ["https://outside.test/openapi.json"], null, default));
        Assert.AreEqual("openapi_candidate_invalid", crossOrigin.Code);
        var excessive = await Assert.ThrowsAsync<OpenApiExtensionException>(() =>
            service.DiscoverAsync("https://origin.example.test/", Enumerable.Range(0, 9).Select(value => $"/{value}.json").ToArray(), null, default));
        Assert.AreEqual("openapi_candidate_limit", excessive.Code);
    }

    [TestMethod]
    public async Task InvocationRequiresExactDigestSelectorAndAppliesFixedBindingsAndCredential()
    {
        HttpRequestMessage? invoked = null;
        var service = Service((request, _) =>
        {
            if (request.RequestUri!.Host == "origin.example.test") return Task.FromResult(Response(JsonContract, "application/json"));
            invoked = Clone(request);
            return Task.FromResult(Response("{\"id\":\"42\"}", "application/json"));
        }, credentials: new Dictionary<string, OpenApiCredentialOptions>
        {
            ["orders"] = new() { Scheme = "Bearer", Name = "Authorization", Value = "secret-token", Location = "header" }
        });
        var parsed = Parser().Parse(JsonContract, "application/json", new("https://origin.example.test/openapi.json"));
        var selector = new OpenApiOperationSelector(parsed.Descriptor.Uri, parsed.Descriptor.Sha256,
            parsed.Descriptor.Servers.Single(), "getOrder", "GET", "/orders/{id}");
        var arguments = JsonSerializer.SerializeToElement(new { query = new { view = "full" } });
        var fixedBindings = new OpenApiFixedBindings(Path: new Dictionary<string, string> { ["id"] = "42" });

        var result = await service.InvokeAsync(selector, arguments, fixedBindings, "orders", 1, "corr-2", default);

        Assert.AreEqual(200, result.StatusCode);
        Assert.AreEqual("https://api.example.test/orders/42?view=full", invoked!.RequestUri!.AbsoluteUri);
        Assert.AreEqual("Bearer", invoked.Headers.Authorization!.Scheme);
        Assert.AreEqual("secret-token", invoked.Headers.Authorization.Parameter);
        Assert.AreEqual("corr-2", result.CorrelationId);
        Assert.IsFalse(JsonSerializer.Serialize(result).Contains("secret-token", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InvocationRejectsDigestChangesAndFixedBindingOverrides()
    {
        var service = Service((request, _) => Task.FromResult(request.RequestUri!.Host == "origin.example.test"
            ? Response(JsonContract, "application/json") : Response("{}", "application/json")));
        var parsed = Parser().Parse(JsonContract, "application/json", new("https://origin.example.test/openapi.json"));
        var selector = new OpenApiOperationSelector(parsed.Descriptor.Uri, new string('0', 64),
            parsed.Descriptor.Servers.Single(), "getOrder", "GET", "/orders/{id}");
        var digest = await Assert.ThrowsAsync<OpenApiExtensionException>(() =>
            service.InvokeAsync(selector, JsonSerializer.SerializeToElement(new { }), null, null, 1, null, default));
        Assert.AreEqual("openapi_contract_digest_mismatch", digest.Code);

        selector = selector with { ContractDigest = parsed.Descriptor.Sha256 };
        var collision = await Assert.ThrowsAsync<OpenApiExtensionException>(() => service.InvokeAsync(selector,
            JsonSerializer.SerializeToElement(new { path = new { id = "caller" } }),
            new(Path: new Dictionary<string, string> { ["id"] = "fixed" }), null, 1, null, default));
        Assert.AreEqual("openapi_fixed_binding_override", collision.Code);
    }

    [TestMethod]
    public async Task InvocationMapsRemoteFailuresAndBoundsPagination()
    {
        var contract = Parser().Parse(JsonContract, "application/json", new("https://origin.example.test/openapi.json"));
        var selector = new OpenApiOperationSelector(contract.Descriptor.Uri, contract.Descriptor.Sha256,
            contract.Descriptor.Servers.Single(), "getOrder", "GET", "/orders/{id}");
        var service = Service((request, _) =>
        {
            if (request.RequestUri!.Host == "origin.example.test") return Task.FromResult(Response(JsonContract, "application/json"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        });
        var failure = await Assert.ThrowsAsync<OpenApiExtensionException>(() => service.InvokeAsync(selector,
            JsonSerializer.SerializeToElement(new { path = new { id = "42" } }), null, null, 1, null, default));
        Assert.AreEqual("openapi_rate_limited", failure.Code);

        var page = 0;
        service = Service((request, _) =>
        {
            if (request.RequestUri!.Host == "origin.example.test") return Task.FromResult(Response(JsonContract, "application/json"));
            var response = Response("[]", "application/json");
            if (++page < 3) response.Headers.TryAddWithoutValidation("Link", $"</orders/42?page={page + 1}>; rel=\"next\"");
            return Task.FromResult(response);
        });
        var pagination = await Assert.ThrowsAsync<OpenApiExtensionException>(() => service.InvokeAsync(selector,
            JsonSerializer.SerializeToElement(new { path = new { id = "42" } }), null, null, 2, null, default));
        Assert.AreEqual("openapi_pagination_limit", pagination.Code);
    }

    [TestMethod]
    public async Task InvocationEnforcesArgumentsResponseMediaSizeAndCancellation()
    {
        var contract = Parser().Parse(JsonContract, "application/json", new("https://origin.example.test/openapi.json"));
        var selector = new OpenApiOperationSelector(contract.Descriptor.Uri, contract.Descriptor.Sha256,
            contract.Descriptor.Servers.Single(), "getOrder", "GET", "/orders/{id}");
        var service = Service((request, _) => Task.FromResult(request.RequestUri!.Host == "origin.example.test"
            ? Response(JsonContract, "application/json") : Response("{}", "application/json")));
        var required = await Assert.ThrowsAsync<OpenApiExtensionException>(() => service.InvokeAsync(selector,
            JsonSerializer.SerializeToElement(new { }), null, null, 1, null, default));
        Assert.AreEqual("openapi_argument_required", required.Code);

        var writeContract = Parser().Parse(YamlContract, "application/yaml", new("https://origin.example.test/openapi.yaml"));
        var writeSelector = new OpenApiOperationSelector(writeContract.Descriptor.Uri, writeContract.Descriptor.Sha256,
            writeContract.Descriptor.Servers.Single(), "createItem", "POST", "/items");
        service = Service((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/openapi.yaml"
            ? Response(YamlContract, "application/yaml") : Response("{}", "application/json")));
        var body = await Assert.ThrowsAsync<OpenApiExtensionException>(() => service.InvokeAsync(writeSelector,
            JsonSerializer.SerializeToElement(new { }), null, null, 1, null, default));
        Assert.AreEqual("openapi_request_body_required", body.Code);

        service = Service((request, _) => Task.FromResult(request.RequestUri!.Host == "origin.example.test"
            ? Response(JsonContract, "application/json") : Response("html", "text/html")));
        var mediaType = await Assert.ThrowsAsync<OpenApiExtensionException>(() => service.InvokeAsync(selector,
            JsonSerializer.SerializeToElement(new { path = new { id = "42" } }), null, null, 1, null, default));
        Assert.AreEqual("openapi_response_content_type_denied", mediaType.Code);

        service = Service((request, _) => Task.FromResult(request.RequestUri!.Host == "origin.example.test"
            ? Response(JsonContract, "application/json") : Response(new string('x', 1_025), "text/plain")),
            configure: options => options.MaximumResponseBytes = 1_024);
        var size = await Assert.ThrowsAsync<OpenApiExtensionException>(() => service.InvokeAsync(selector,
            JsonSerializer.SerializeToElement(new { path = new { id = "42" } }), null, null, 1, null, default));
        Assert.AreEqual("openapi_response_too_large", size.Code);

        service = Service(async (request, token) =>
        {
            if (request.RequestUri!.Host == "origin.example.test") return Response(JsonContract, "application/json");
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response("{}", "application/json");
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => service.InvokeAsync(selector,
            JsonSerializer.SerializeToElement(new { path = new { id = "42" } }), null, null, 1, null, cancellation.Token));
    }

    [TestMethod]
    public async Task InvocationMapsAuthenticationAuthorizationInvalidAndUnavailableFailures()
    {
        var contract = Parser().Parse(JsonContract, "application/json", new("https://origin.example.test/openapi.json"));
        var selector = new OpenApiOperationSelector(contract.Descriptor.Uri, contract.Descriptor.Sha256,
            contract.Descriptor.Servers.Single(), "getOrder", "GET", "/orders/{id}");
        var arguments = JsonSerializer.SerializeToElement(new { path = new { id = "42" } });
        foreach (var testCase in new[]
        {
            (HttpStatusCode.Unauthorized, "openapi_authentication_failed"),
            (HttpStatusCode.Forbidden, "openapi_authorization_failed"),
            (HttpStatusCode.BadRequest, "openapi_remote_response_invalid"),
            (HttpStatusCode.ServiceUnavailable, "openapi_remote_unavailable")
        })
        {
            var service = Service((request, _) => Task.FromResult(request.RequestUri!.Host == "origin.example.test"
                ? Response(JsonContract, "application/json") : new HttpResponseMessage(testCase.Item1)));
            var failure = await Assert.ThrowsAsync<OpenApiExtensionException>(() =>
                service.InvokeAsync(selector, arguments, null, null, 1, null, default));
            Assert.AreEqual(testCase.Item2, failure.Code);
        }

        var unavailable = Service((request, _) => request.RequestUri!.Host == "origin.example.test"
            ? Task.FromResult(Response(JsonContract, "application/json"))
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("sensitive transport detail")));
        var exception = await Assert.ThrowsAsync<OpenApiExtensionException>(() =>
            unavailable.InvokeAsync(selector, arguments, null, null, 1, null, default));
        Assert.AreEqual("openapi_remote_unavailable", exception.Code);
        Assert.IsFalse(exception.Message.Contains("sensitive transport detail", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DestinationPolicyRejectsPrivateAddresses()
    {
        var options = Options.Create(DefaultOptions() with { AllowPrivateAddresses = false });
        var policy = new OpenApiDestinationPolicy(options, new Resolver(IPAddress.Loopback));
        var exception = await Assert.ThrowsAsync<OpenApiExtensionException>(() => policy.ValidateAsync("https://origin.example.test", default));
        Assert.AreEqual("openapi_destination_denied", exception.Code);
    }

    [TestMethod]
    public async Task ExtensionPublishesSchemaFormatToolsAndDataSourceBundleWithoutOperationTools()
    {
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("OpenApi:AllowedDomains:0", "example.test"));
        var manifest = await new AepClient(factory.CreateClient()).GetManifestAsync();

        Assert.AreEqual("Agentstration.Extensions.OpenApi", manifest.Extension.Id);
        Assert.AreEqual("openapi", manifest.Contributions.SchemaFormats?.Single().Format);
        CollectionAssert.AreEquivalent(new[] { "openapi.contract.discover", "openapi.operation.invoke" },
            manifest.Contributions.Tools?.Select(value => value.Id).ToArray());
        var bundle = manifest.Contributions.DataSourceProfileBundles?.Single();
        Assert.AreEqual("openapi-governed", bundle?.Id);
        Assert.HasCount(4, bundle!.ResourceManifests);
        Assert.IsTrue(bundle.ResourceManifests.Any(value => value.Contains("flow.contract: datasource.discovery/v1", StringComparison.Ordinal)));
        Assert.IsTrue(bundle.ResourceManifests.Any(value => value.Contains("flow.contract: datasource.acquisition/v1", StringComparison.Ordinal)));
        Assert.IsTrue(bundle.ResourceManifests.Any(value => value.Contains("kind: DataSourceProfile", StringComparison.Ordinal)));
        Assert.IsFalse(manifest.Contributions.Tools!.Any(value => value.Id == "getOrder"));
    }

    private static OpenApiContractParser Parser(OpenApiOptions? options = null) => new(Options.Create(options ?? DefaultOptions()));

    private static OpenApiService Service(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        Dictionary<string, OpenApiCredentialOptions>? credentials = null,
        Action<OpenApiOptions>? configure = null)
    {
        var settings = DefaultOptions() with { Credentials = credentials ?? new(StringComparer.Ordinal) };
        configure?.Invoke(settings);
        var configured = Options.Create(settings);
        var policy = new OpenApiDestinationPolicy(configured, new Resolver(IPAddress.Parse("8.8.8.8")));
        return new(new HttpClient(new Handler(handler)), policy, new(configured), configured, NullLogger<OpenApiService>.Instance);
    }

    private static OpenApiOptions DefaultOptions() => new()
    {
        AllowedDomains = ["example.test"], AllowedPorts = [443], AllowPrivateAddresses = true
    };

    private static string ContractWithSchema(string reference) => """
        {"openapi":"3.1.0","info":{"title":"x","version":"1"},"paths":{},"components":{"schemas":{"A":{"$ref":"__REFERENCE__"}}}}
        """.Replace("__REFERENCE__", reference, StringComparison.Ordinal);

    private static void AssertCode(string code, string contract)
    {
        var exception = Assert.Throws<OpenApiExtensionException>(() =>
            Parser().Parse(contract, "application/json", new("https://origin.example.test/openapi.json")));
        Assert.AreEqual(code, exception.Code);
    }

    private static HttpResponseMessage Response(string value, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, mediaType) };
        return response;
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class Resolver(IPAddress address) : IOpenApiAddressResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([address]);
    }
}
