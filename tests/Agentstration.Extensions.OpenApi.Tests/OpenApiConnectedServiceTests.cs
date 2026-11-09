using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Extensions.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.OpenApi.Tests;

public sealed partial class OpenApiExtensionTests
{
    [TestMethod]
    public async Task ConnectedServiceDiscoveryRetrievesRealBoundedDocumentAndKeepsStableIdentity()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(OpenApiService).Assembly.GetName().Name,
            Args = []
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        var current = JsonContract;
        var accepted = 0;
        server.MapGet("/openapi.json", (HttpRequest request) =>
        {
            if (request.Headers.Authorization != "Bearer synthetic-discovery-token") return Results.Unauthorized();
            Interlocked.Increment(ref accepted);
            return Results.Text(current, "application/json");
        });
        await server.StartAsync();
        var origin = server.Urls.Single();
        var settings = DefaultOptions() with { AllowedDomains = ["127.0.0.1"], AllowedPorts = [new Uri(origin).Port] };
        using var http = new HttpClient();
        var service = new OpenApiService(http, new(Options.Create(settings), new Resolver(IPAddress.Parse("8.8.8.8"))),
            Parser(settings), Options.Create(settings), NullLogger<OpenApiService>.Instance);
        var result = await service.DiscoverConnectionAsync(origin, ["/openapi.json"], "synthetic-discovery-token", "first", default);
        Assert.HasCount(1, result.Operations);
        Assert.AreEqual("openapi-operation", result.Operations[0].TypeId);
        Assert.AreEqual("getOrder", result.Operations[0].Definition.GetProperty("operationId").GetString());
        current = JsonContract.Replace("Get one order", "Get an updated order", StringComparison.Ordinal);
        var changed = await service.DiscoverConnectionAsync(origin, ["/openapi.json"], "synthetic-discovery-token", "second", default);
        Assert.AreEqual(result.Operations[0].ExternalId, changed.Operations[0].ExternalId);
        Assert.AreNotEqual(result.Operations[0].Definition.GetProperty("contractDigest").GetString(),
            changed.Operations[0].Definition.GetProperty("contractDigest").GetString());
        Assert.AreEqual(2, accepted);
        Assert.DoesNotContain("synthetic-discovery-token", JsonSerializer.Serialize(changed), StringComparison.Ordinal);
        await server.StopAsync();
    }

    [TestMethod]
    public async Task ConnectedServiceDiscoveryUsesOperationIdWhenSummaryIsBlank()
    {
        var contract = JsonContract.Replace("Get one order", " ", StringComparison.Ordinal);
        var result = await Service((_, _) => Task.FromResult(Response(contract, "application/json")))
            .DiscoverConnectionAsync("https://origin.example.test", ["/openapi.json"], null, "blank-summary", default);
        Assert.AreEqual("getOrder", result.Operations.Single().DisplayName);
    }

    [TestMethod]
    public async Task ConnectedServiceDiscoveryRefusesReflectedCredentialInContractMetadata()
    {
        const string credential = "synthetic-reflected-credential";
        var contract = JsonContract.Replace("Get one order", credential, StringComparison.Ordinal);
        var service = Service((_, _) => Task.FromResult(Response(contract, "application/json")));
        var error = await Assert.ThrowsAsync<OpenApiExtensionException>(() => service.DiscoverConnectionAsync(
            "https://origin.example.test", ["/openapi.json"], credential, "reflected", default));
        Assert.AreEqual("openapi_sensitive_contract", error.Code);
        Assert.DoesNotContain(credential, error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ConnectedServiceDiscoverySeparatesIdenticalOperationIdsAcrossContracts()
    {
        var result = await Service((_, _) => Task.FromResult(Response(JsonContract, "application/json")))
            .DiscoverConnectionAsync("https://origin.example.test", ["/one.json", "/two.json"], null, "multiple", default);
        Assert.HasCount(2, result.Operations);
        Assert.AreNotEqual(result.Operations[0].ExternalId, result.Operations[1].ExternalId);
    }

    [TestMethod]
    public async Task ConnectedServiceDiscoveryRejectsOversizedManifestRatherThanTruncatingOperations()
    {
        var operations = string.Join(',', Enumerable.Range(0, 150).Select(index =>
            $"\"/p{index}\":{{\"get\":{{\"operationId\":\"op{index}\",\"responses\":{{\"200\":{{\"description\":\"ok\"}}}}}}}}"));
        var contract = "{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"bounded\",\"version\":\"1\"},\"paths\":{" + operations + "}}";
        var service = Service((_, _) => Task.FromResult(Response(contract, "application/json")));
        var error = await Assert.ThrowsAsync<OpenApiExtensionException>(() => service.DiscoverConnectionAsync(
            "https://origin.example.test", ["/openapi.json"], null, "large", default));
        Assert.AreEqual("openapi_manifest_too_large", error.Code);
    }

    [TestMethod]
    public async Task ConnectedServiceAepClientBoundsErrorResponsesBeforeParsingThem()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(new string('x', AepConnectedServiceDiscoveryProtocol.MaximumResponseBytes + 1))
        })))
        { BaseAddress = new("https://extension.example.test") };
        var error = await Assert.ThrowsAsync<AepProtocolException>(() => new AepClient(http)
            .DiscoverConnectedServiceOperationsAsync(new("openapi", "1.0.0", JsonSerializer.SerializeToElement(new { }), [], "bounded")));
        Assert.AreEqual("response_too_large", error.Code);
    }

    [TestMethod]
    public async Task ConnectedServiceAepEndpointRequiresEnrollmentTransportAuthentication()
    {
        const string workloadToken = "synthetic-openapi-workload-token-123456";
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenApi:AllowedDomains:0", "example.test");
            builder.ConfigureServices(services => services.AddAepStaticBearerAuthentication(options =>
                options.AddToken("openapi-test", "authority-test", workloadToken)));
        });
        using var http = factory.CreateClient();
        using var anonymous = await http.PostAsJsonAsync(AepConnectedServiceDiscoveryProtocol.Path,
            new AepConnectedServiceDiscoveryRequest("unknown", "1.0.0", JsonSerializer.SerializeToElement(new { }), [], "test"), AepProtocol.JsonOptions);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", workloadToken);
        using var authenticated = await http.PostAsJsonAsync(AepConnectedServiceDiscoveryProtocol.Path,
            new AepConnectedServiceDiscoveryRequest("unknown", "1.0.0", JsonSerializer.SerializeToElement(new { }), [], "test"), AepProtocol.JsonOptions);
        Assert.AreEqual(HttpStatusCode.NotFound, authenticated.StatusCode);
    }

    [TestMethod]
    public async Task ConnectedServiceAepEndpointRejectsMalformedOversizedAndUnknownRequests()
    {
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("OpenApi:AllowedDomains:0", "example.test"));
        using var http = factory.CreateClient();
        using var malformed = await http.PostAsync(AepConnectedServiceDiscoveryProtocol.Path, new StringContent("{"));
        Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var oversized = await http.PostAsync(AepConnectedServiceDiscoveryProtocol.Path, new StringContent(new string('x', 65537)));
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        var options = JsonSerializer.SerializeToElement(new { candidatePaths = new[] { "/openapi.json" } });
        using var unknown = await http.PostAsJsonAsync(AepConnectedServiceDiscoveryProtocol.Path,
            new AepConnectedServiceDiscoveryRequest("other-provider", "1.0.0", options, [], "unknown"), AepProtocol.JsonOptions);
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        using var invalidOptions = await http.PostAsJsonAsync(AepConnectedServiceDiscoveryProtocol.Path,
            new AepConnectedServiceDiscoveryRequest("openapi", "1.0.0", JsonSerializer.SerializeToElement(new { }),
                [AepBoundValue.Inline("baseUrl", JsonSerializer.SerializeToElement("https://origin.example.test"))], "invalid"), AepProtocol.JsonOptions);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidOptions.StatusCode);
    }

    [TestMethod]
    public async Task ConnectedServiceAepEndpointValidatesBindingsAndUsesProtectedSecretGrant()
    {
        var redeemed = 0;
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenApi:AllowedDomains:0", "example.test");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOpenApiAddressResolver>();
                services.AddSingleton<IOpenApiAddressResolver>(new Resolver(IPAddress.Parse("8.8.8.8")));
                services.AddHttpClient<OpenApiService>().ConfigurePrimaryHttpMessageHandler(() => new Handler((request, _) =>
                {
                    Assert.AreEqual("Bearer synthetic-grant-value", request.Headers.Authorization?.ToString());
                    return Task.FromResult(Response(JsonContract, "application/json"));
                }));
                services.AddHttpClient("openapi-secret-access").ConfigurePrimaryHttpMessageHandler(() => new Handler((request, _) =>
                {
                    Assert.AreEqual(AepProtocol.SecretAccessPath, request.RequestUri!.AbsolutePath);
                    Assert.AreEqual(1, Interlocked.Increment(ref redeemed));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new AepSecretAccessResponse(AepProtocol.SecretAccessVersion,
                            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("synthetic-grant-value"))), options: AepProtocol.JsonOptions)
                    });
                }));
            });
        });
        using var http = factory.CreateClient();
        var options = JsonSerializer.SerializeToElement(new { candidatePaths = new[] { "/openapi.json" } });
        var baseUrl = AepBoundValue.Inline("baseUrl", JsonSerializer.SerializeToElement("https://origin.example.test"));
        using var invalid = await http.PostAsJsonAsync(AepConnectedServiceDiscoveryProtocol.Path,
            new AepConnectedServiceDiscoveryRequest("openapi", "1.0.0", options,
                [baseUrl, AepBoundValue.Inline("bearerToken", JsonSerializer.SerializeToElement("raw-secret"))], "invalid"), AepProtocol.JsonOptions);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.AreEqual(0, redeemed);
        var grant = AepBoundValue.Secured("bearerToken", new(AepProtocol.SecretAccessVersion,
            new Uri("https://callback.example.test" + AepProtocol.SecretAccessPath), "Agentstration.Extensions.OpenApi",
            "bearerToken", "execution", "synthetic-capability"));
        var result = await new AepClient(http).DiscoverConnectedServiceOperationsAsync(new("openapi", "1.0.0", options,
            [baseUrl, grant], "valid"));
        Assert.HasCount(1, result.Operations);
        Assert.AreEqual(1, redeemed);
        Assert.DoesNotContain("synthetic-grant-value", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        using var missing = await http.PostAsJsonAsync(AepConnectedServiceDiscoveryProtocol.Path,
            new AepConnectedServiceDiscoveryRequest("openapi", "1.0.0", options, [], "missing"), AepProtocol.JsonOptions);
        Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
    }
}
