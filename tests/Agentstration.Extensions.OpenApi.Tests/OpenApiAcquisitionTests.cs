using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Extensions.OpenApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Extensions.OpenApi.Tests;

public sealed partial class OpenApiExtensionTests
{
    [TestMethod]
    [DataRow("application/json; charset=utf-8", "200")]
    [DataRow("Application/JSON; charset=UTF-8", "200")]
    [DataRow("application/json; charset=utf-8", "default")]
    public async Task DiscoveryResolvesReferencedJsonResponseSchemasWithMediaTypeParameters(string mediaType, string status)
    {
        var contract = """
            {"openapi":"3.1.0","info":{"title":"Records","version":"1"},"paths":{"/items":{"get":{"operationId":"getItems",
            "responses":{"STATUS":{"description":"Records","content":{"MEDIA":{"schema":{"$ref":"#/components/schemas/records"}}}}}}}},
            "components":{"schemas":{"records":{"type":"object","properties":{"results":{"type":"array","items":{"type":"object"}}},"required":["results"]}}}}
            """.Replace("STATUS", status, StringComparison.Ordinal).Replace("MEDIA", mediaType, StringComparison.Ordinal);
        var discovery = await Service((_, _) => Task.FromResult(Response(contract, "application/json")))
            .DiscoverConnectionAsync("https://origin.example.test", ["/openapi.json"], null, "json-charset", default);
        var operation = discovery.Operations.Single();
        var output = operation.Definition.GetProperty("outputSchema");
        Assert.AreEqual(JsonValueKind.String, output.GetProperty("type").ValueKind, output.GetRawText());
        Assert.AreEqual("object", output.GetProperty("type").GetString());
        Assert.AreEqual("array", output.GetProperty("properties").GetProperty("results").GetProperty("type").GetString());
        Assert.AreEqual("results", output.GetProperty("required")[0].GetString());
    }

    [TestMethod]
    public async Task DiscoveryRefusesConflictingJsonRepresentationsInsteadOfDroppingTheirSchemas()
    {
        var contract = AcquisitionContract.Replace("\"application/json\":{\"schema\":{\"type\":\"object\"}}",
            "\"application/json\":{\"schema\":{\"type\":\"object\"}},\"application/json; charset=utf-8\":{\"schema\":{\"type\":\"array\"}}", StringComparison.Ordinal);
        var discovery = await Service((_, _) => Task.FromResult(Response(contract, "application/json")))
            .DiscoverConnectionAsync("https://origin.example.test", ["/openapi.json"], null, "ambiguous-json", default);
        var operation = discovery.Operations.Single(item => item.Definition.GetProperty("operationId").GetString() == "getItems");
        Assert.IsFalse(operation.Definition.TryGetProperty("inputSchema", out _));
        Assert.Contains("openapi_acquisition_schema_unsupported", discovery.Diagnostics.Select(item => item.Code));
    }

    private const string AcquisitionContract = """
        {"openapi":"3.1.0","info":{"title":"Items","version":"1"},"paths":{
          "/items":{"get":{"operationId":"getItems","parameters":[{"in":"query","name":"dataset","required":true,"schema":{"type":"string"}}],
            "responses":{"200":{"description":"OK","content":{"application/json":{"schema":{"type":"object"}}}}}},
            "post":{"operationId":"postItems","responses":{"200":{"description":"OK"}}}}
        }}
        """;

    [TestMethod]
    [DataRow("valid", null)]
    [DataRow("changed-contract", "openapi_contract_digest_mismatch")]
    [DataRow("foreign-contract", "openapi_connection_origin_denied")]
    [DataRow("foreign-server", "openapi_connection_origin_denied")]
    [DataRow("action", "openapi_acquisition_action_denied")]
    [DataRow("credential-reflection", "openapi_sensitive_response")]
    public async Task ProtectedOpenApiAcquisitionUsesSavedSelectorAndRefusesChangedOrForeignAuthority(string scenario, string? expected)
    {
        var businessCalls = 0;
        var current = AcquisitionContract;
        var service = Service((request, _) =>
        {
            Assert.AreEqual("Bearer synthetic-acquisition-token", request.Headers.Authorization?.ToString());
            if (request.RequestUri!.AbsolutePath == "/openapi.json") return Task.FromResult(Response(current, "application/json"));
            businessCalls++;
            return Task.FromResult(Response(scenario == "credential-reflection" ? "{\"token\":\"synthetic-acquisition-token\"}" : "{\"dataset\":\"alpha\"}", "application/json"));
        });
        var discovery = await service.DiscoverConnectionAsync("https://origin.example.test", ["/openapi.json"],
            "synthetic-acquisition-token", "discover", default);
        var selected = discovery.Operations.Single(item => item.Definition.GetProperty("operationId").GetString() == (scenario == "action" ? "postItems" : "getItems"));
        var definition = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(selected.Definition)!;
        if (scenario == "changed-contract") current = current.Replace("Items", "Changed", StringComparison.Ordinal);
        if (scenario == "foreign-contract") definition["contractUri"] = JsonSerializer.SerializeToElement("https://other.example.test/openapi.json");
        if (scenario == "foreign-server")
        {
            // A legitimately discovered foreign server is still outside the configured connection's authority.
            current = current.Replace("\"paths\":", "\"servers\":[{\"url\":\"https://other.example.test\"}],\"paths\":", StringComparison.Ordinal);
            var foreign = await service.DiscoverConnectionAsync("https://origin.example.test", ["/openapi.json"], "synthetic-acquisition-token", "foreign", default);
            definition = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(foreign.Operations.Single(item => item.Definition.GetProperty("operationId").GetString() == "getItems").Definition)!;
        }
        using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder => builder.UseSetting("OpenApi:AllowedDomains:0", "example.test").ConfigureServices(services =>
        {
            services.AddSingleton(service);
            services.AddHttpClient("openapi-secret-access").ConfigurePrimaryHttpMessageHandler(() => new Handler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new AepSecretAccessResponse(
                    AepProtocol.SecretAccessVersion, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("synthetic-acquisition-token"))))
                })));
        }));
        var grant = AepBoundValue.Secured("bearerToken", new(AepProtocol.SecretAccessVersion, new("https://authority.example.test/aep/v1/secrets/redeem"),
            "Agentstration.Extensions.OpenApi", "bearerToken", "call", "synthetic-grant"));
        var provider = new OpenApiConnectedServiceAcquisition(service,
            new(Microsoft.Extensions.Options.Options.Create(DefaultOptions()), new Resolver(IPAddress.Parse("8.8.8.8"))), factory.Services.GetRequiredService<IHttpClientFactory>());
        var request = new AepConnectedServiceAcquisitionRequest("openapi", "1.0.0", JsonSerializer.SerializeToElement(new { }),
            [AepBoundValue.Inline("baseUrl", JsonSerializer.SerializeToElement("https://origin.example.test")), grant], "openapi-operation", "1.0.0",
            JsonSerializer.SerializeToElement(definition), JsonSerializer.SerializeToElement(new { query = new { dataset = "alpha" } }), "acquire");
        if (expected is null)
        {
            var output = await provider.AcquireAsync(request, default);
            Assert.AreEqual("alpha", output.Result.GetProperty("dataset").GetString());
        }
        else
        {
            var failure = await Assert.ThrowsAsync<Agentstration.Aep.AspNetCore.AepServerException>(() => provider.AcquireAsync(request, default));
            Assert.AreEqual(expected, failure.Code);
        }
        Assert.AreEqual(scenario is "valid" or "credential-reflection" ? 1 : 0, businessCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DiscoveryProjectsEffectiveSchemasAndReportsUnsupportedValidation(bool unsupported)
    {
        var contract = unsupported ? AcquisitionContract.Replace("{\"type\":\"object\"}", "{\"oneOf\":[{\"type\":\"object\"}]}", StringComparison.Ordinal) : AcquisitionContract;
        var discovery = await Service((_, _) => Task.FromResult(Response(contract, "application/json")))
            .DiscoverConnectionAsync("https://origin.example.test", ["/openapi.json"], null, "schema", default);
        var operation = discovery.Operations.Single(item => item.Definition.GetProperty("operationId").GetString() == "getItems");
        if (unsupported)
        {
            Assert.IsFalse(operation.Definition.TryGetProperty("inputSchema", out _));
            Assert.Contains("openapi_acquisition_schema_unsupported", discovery.Diagnostics.Select(item => item.Code));
        }
        else
        {
            var input = operation.Definition.GetProperty("inputSchema");
            Assert.AreEqual("query", input.GetProperty("required")[0].GetString());
            var query = input.GetProperty("properties").GetProperty("query");
            Assert.AreEqual("dataset", query.GetProperty("required")[0].GetString());
            Assert.AreEqual("string", query.GetProperty("properties").GetProperty("dataset").GetProperty("type").GetString());
            Assert.AreEqual("object", operation.Definition.GetProperty("outputSchema").GetProperty("type").GetString());
        }
        var action = discovery.Operations.Single(item => item.Definition.GetProperty("operationId").GetString() == "postItems");
        Assert.IsFalse(action.Definition.TryGetProperty("inputSchema", out _));
    }

    [TestMethod]
    public async Task DefaultResponseContractRemainsEffectiveForAcquisition()
    {
        var contract = AcquisitionContract.Replace("\"200\"", "\"default\"", StringComparison.Ordinal);
        var discovery = await Service((_, _) => Task.FromResult(Response(contract, "application/json")))
            .DiscoverConnectionAsync("https://origin.example.test", ["/openapi.json"], null, "default-response", default);
        var selected = discovery.Operations.Single(item => item.Definition.GetProperty("operationId").GetString() == "getItems");
        Assert.AreEqual("object", selected.Definition.GetProperty("outputSchema").GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task InvocationPreservesDistinctCaseSensitiveQueryNames()
    {
        var contract = AcquisitionContract.Replace("\"parameters\":[", "\"parameters\":[{\"in\":\"query\",\"name\":\"Dataset\",\"schema\":{\"type\":\"string\"}},", StringComparison.Ordinal);
        var invoked = false;
        var service = Service((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/openapi.json") return Task.FromResult(Response(contract, "application/json"));
            Assert.Contains("dataset=alpha", request.RequestUri.Query);
            Assert.Contains("Dataset=beta", request.RequestUri.Query);
            invoked = true;
            return Task.FromResult(Response("{}", "application/json"));
        });
        var parsed = Parser().Parse(contract, "application/json", new("https://origin.example.test/openapi.json"));
        await service.InvokeAsync(new(parsed.Descriptor.Uri, parsed.Descriptor.Sha256, "https://origin.example.test", "getItems", "GET", "/items"),
            JsonSerializer.Deserialize<JsonElement>("""{"query":{"dataset":"alpha","Dataset":"beta"}}"""), null, null, 1, "query-case", default,
            connectionOrigin: new("https://origin.example.test"), requireReadOnly: true);
        Assert.IsTrue(invoked);
    }

    [TestMethod]
    public async Task AcquisitionEndpointUsesEnrollmentAuthenticationAndRejectsMalformedOrUnprotectedInputs()
    {
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("OpenApi:AllowedDomains:0", "example.test").ConfigureServices(services => services.AddAepStaticBearerAuthentication(options =>
                options.AddToken("extension", "authority", "synthetic-openapi-acquisition-token-123456"))));
        using var http = factory.CreateClient();
        using var anonymous = await http.PostAsync(AepConnectedServiceAcquisitionProtocol.Path, new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-openapi-acquisition-token-123456");
        using var malformed = await http.PostAsync(AepConnectedServiceAcquisitionProtocol.Path, new StringContent("{"));
        Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var oversized = await http.PostAsync(AepConnectedServiceAcquisitionProtocol.Path,
            new StringContent(new string('x', AepConnectedServiceAcquisitionProtocol.MaximumRequestBytes + 1)));
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        var request = new AepConnectedServiceAcquisitionRequest("openapi", "1.0.0", JsonSerializer.SerializeToElement(new { }),
            [AepBoundValue.Inline("baseUrl", JsonSerializer.SerializeToElement("https://origin.example.test")),
             AepBoundValue.Inline("bearerToken", JsonSerializer.SerializeToElement("forbidden-inline-credential"))],
            "openapi-operation", "1.0.0", JsonSerializer.SerializeToElement(new { readOnly = true }), JsonSerializer.SerializeToElement(new { }), "test");
        using var invalid = await http.PostAsJsonAsync(AepConnectedServiceAcquisitionProtocol.Path, request, AepProtocol.JsonOptions);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
