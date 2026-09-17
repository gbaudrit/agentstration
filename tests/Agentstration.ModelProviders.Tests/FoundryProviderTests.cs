using System.Net;
using System.Text;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.Foundry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
[DoNotParallelize]
public sealed class FoundryProviderTests
{
    [TestMethod]
    public void StartupRejectsInvalidConfigurationAndMissingEnvironmentKey()
    {
        var configuration = Configuration("http://foundry.example/api/projects/demo", "https://foundry.example/openai/v1", "ApiKeyEnvironment");
        Assert.ThrowsExactly<InvalidOperationException>(() => FoundryExtensionOptions.FromConfiguration(configuration));

        configuration = Configuration("https://foundry.example/api/projects/demo", "https://other.example/openai/v1?token=bad", "ApiKeyEnvironment");
        Assert.ThrowsExactly<InvalidOperationException>(() => FoundryExtensionOptions.FromConfiguration(configuration));

        configuration = Configuration("https://foundry.example/api/projects/demo", "https://foundry.example/openai/v1", "ImplicitDefault");
        Assert.ThrowsExactly<InvalidOperationException>(() => FoundryExtensionOptions.FromConfiguration(configuration));

        var previous = Environment.GetEnvironmentVariable("FOUNDRY_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FOUNDRY_API_KEY", null);
            Assert.ThrowsExactly<InvalidOperationException>(() => new FoundryRequestAuthenticator(Options()));
        }
        finally { Environment.SetEnvironmentVariable("FOUNDRY_API_KEY", previous); }
    }

    [TestMethod]
    public async Task DevelopmentUserSecretIsAnOptionalFallbackToTheEnvironmentKey()
    {
        var previous = Environment.GetEnvironmentVariable("FOUNDRY_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FOUNDRY_API_KEY", null);
            var authenticator = new FoundryRequestAuthenticator(Options(), "development-secret");
            using var request = new HttpRequestMessage(HttpMethod.Get, Options().DeploymentsEndpoint());
            await authenticator.ApplyAsync(request, CancellationToken.None);
            Assert.AreEqual("development-secret", request.Headers.GetValues("api-key").Single());

            Environment.SetEnvironmentVariable("FOUNDRY_API_KEY", "environment-key");
            authenticator = new FoundryRequestAuthenticator(Options(), "development-secret");
            using var overrideRequest = new HttpRequestMessage(HttpMethod.Get, Options().DeploymentsEndpoint());
            await authenticator.ApplyAsync(overrideRequest, CancellationToken.None);
            Assert.AreEqual("environment-key", overrideRequest.Headers.GetValues("api-key").Single());
        }
        finally { Environment.SetEnvironmentVariable("FOUNDRY_API_KEY", previous); }
    }

    [TestMethod]
    public async Task DiscoveryFiltersUnknownCapabilitiesAndKeepsAuthenticationOnEachRequest()
    {
        var seen = new List<Uri>();
        var key = Guid.NewGuid().ToString("N");
        var previous = Environment.GetEnvironmentVariable("FOUNDRY_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FOUNDRY_API_KEY", key);
            using var client = Client((request, _) =>
            {
                seen.Add(request.RequestUri!);
                Assert.AreEqual(key, request.Headers.GetValues("api-key").Single());
                return Task.FromResult(Json(HttpStatusCode.OK, seen.Count == 1
                    ? """
                      {"value":[
                        {"type":"ModelDeployment","name":"chat-a","modelPublisher":"Microsoft","modelName":"phi","modelVersion":"1","capabilities":{"chatCompletion":"true","toolCalling":"true"}},
                        {"type":"ModelDeployment","name":"embedding","capabilities":{"embeddings":true}},
                        {"type":"Connection","name":"not-a-model","capabilities":{"chat":true}}
                      ],"nextLink":"https://foundry.example/api/projects/demo/deployments?api-version=v1&after=two"}
                      """
                    : """
                      {"value":[{"type":"ModelDeployment","name":"chat-b","capabilities":{"chat":true}}]}
                      """));
            });
            var provider = new FoundryAepModelProvider(client, Options(), new FoundryRequestAuthenticator(Options()));
            var models = await provider.ListModelsAsync();

            Assert.AreEqual(2, models.Count);
            Assert.AreEqual("chat-a", models[0].Id);
            Assert.AreEqual("Microsoft", models[0].Metadata?["publisher"]);
            CollectionAssert.AreEqual(new[] { "chat" }, models[0].Capabilities!.ToArray());
            Assert.AreEqual("chat-b", models[1].Id);
            Assert.AreEqual(2, seen.Count);
            Assert.IsFalse(provider.Descriptor.Capabilities.Chat);
            Assert.IsTrue(provider.Descriptor.Capabilities.ModelDiscovery);
        }
        finally { Environment.SetEnvironmentVariable("FOUNDRY_API_KEY", previous); }
    }

    [TestMethod]
    public async Task PaginationCannotForwardCredentialToAnotherOriginOrPath()
    {
        var calls = 0;
        await WithEnvironmentKeyAsync(async authenticator =>
        {
            using var client = Client((_, _) =>
            {
                calls++;
                return Task.FromResult(Json(HttpStatusCode.OK, """
                    {"value":[],"nextLink":"https://attacker.example/collect?api-version=v1"}
                    """));
            });
            var provider = new FoundryAepModelProvider(client, Options(), authenticator);
            var exception = await Assert.ThrowsAsync<AepServerException>(() => provider.ListModelsAsync());
            Assert.AreEqual("discovery_origin_invalid", exception.Code);
            Assert.AreEqual(1, calls);
        });

        await WithEnvironmentKeyAsync(async authenticator =>
        {
            using var client = Client((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """
                {"value":[],"nextLink":"https://foundry.example/other?api-version=v1"}
                """)));
            var exception = await Assert.ThrowsAsync<AepServerException>(() => new FoundryAepModelProvider(client, Options(), authenticator).ListModelsAsync());
            Assert.AreEqual("discovery_origin_invalid", exception.Code);
        });
    }

    [TestMethod]
    public async Task DiscoveryEnforcesPageAndBodyLimitsAndRejectsRedirects()
    {
        await WithEnvironmentKeyAsync(async authenticator =>
        {
            using var client = Client((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """
                {"value":[],"nextLink":"?api-version=v1&after=again"}
                """)));
            var options = Options() with { MaximumDiscoveryPages = 1 };
            var exception = await Assert.ThrowsAsync<AepServerException>(() => new FoundryAepModelProvider(client, options, authenticator).ListModelsAsync());
            Assert.AreEqual("discovery_limit", exception.Code);
        });

        await WithEnvironmentKeyAsync(async authenticator =>
        {
            using var client = Client((_, _) => Task.FromResult(Json(HttpStatusCode.OK, new string('x', 1500))));
            var options = Options() with { MaximumDiscoveryResponseBytes = 1024 };
            var exception = await Assert.ThrowsAsync<AepServerException>(() => new FoundryAepModelProvider(client, options, authenticator).ListModelsAsync());
            Assert.AreEqual("discovery_limit", exception.Code);
        });

        await WithEnvironmentKeyAsync(async authenticator =>
        {
            using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://attacker.example/") }
            }));
            var exception = await Assert.ThrowsAsync<AepServerException>(() => new FoundryAepModelProvider(client, Options(), authenticator).ListModelsAsync());
            Assert.AreEqual("provider_redirect_denied", exception.Code);
        });
    }

    [TestMethod]
    public async Task DiscoveryMapsTimeoutAndAuthenticationFailureWithoutProviderBodies()
    {
        await WithEnvironmentKeyAsync(async authenticator =>
        {
            using var client = Client(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Json(HttpStatusCode.OK, "{}");
            });
            var options = Options() with { RequestTimeout = TimeSpan.FromSeconds(1) };
            var exception = await Assert.ThrowsAsync<AepServerException>(() => new FoundryAepModelProvider(client, options, authenticator).ListModelsAsync());
            Assert.AreEqual("provider_timeout", exception.Code);
        });

        await WithEnvironmentKeyAsync(async authenticator =>
        {
            using var client = Client((_, _) => Task.FromResult(Json(HttpStatusCode.Unauthorized, "sensitive-provider-body")));
            var provider = new FoundryAepModelProvider(client, Options(), authenticator);
            var exception = await Assert.ThrowsAsync<AepServerException>(() => provider.ListModelsAsync());
            Assert.AreEqual("authentication_failed", exception.Code);
            Assert.IsFalse(exception.Message.Contains("sensitive-provider-body", StringComparison.Ordinal));
            Assert.AreEqual("unavailable", (await provider.GetHealthAsync()).Status);
        });
    }

    [TestMethod]
    public async Task DiscoveryPropagatesCallerCancellation()
    {
        await WithEnvironmentKeyAsync(async authenticator =>
        {
            using var client = Client(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Json(HttpStatusCode.OK, "{}");
            });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var provider = new FoundryAepModelProvider(client, Options(), authenticator);

            await Assert.ThrowsAsync<OperationCanceledException>(() => provider.ListModelsAsync(cancellation.Token));
        });
    }

    [TestMethod]
    public void NetworkPolicyRejectsMetadataAndRequiresExplicitPrivateHost()
    {
        var options = Options();
        Assert.IsFalse(FoundrySecureTransport.IsAddressAllowed(IPAddress.Parse("169.254.169.254"), "foundry.example", options));
        Assert.IsFalse(FoundrySecureTransport.IsAddressAllowed(IPAddress.Parse("10.0.0.5"), "foundry.example", options));
        Assert.IsTrue(FoundrySecureTransport.IsAddressAllowed(IPAddress.Parse("20.1.2.3"), "foundry.example", options));
        options = options with { AllowedPrivateHosts = new HashSet<string>(["foundry.example"], StringComparer.OrdinalIgnoreCase) };
        Assert.IsTrue(FoundrySecureTransport.IsAddressAllowed(IPAddress.Parse("10.0.0.5"), "foundry.example", options));
        Assert.IsFalse(FoundrySecureTransport.IsAddressAllowed(IPAddress.Parse("169.254.169.254"), "foundry.example", options));
    }

    [TestMethod]
    public async Task AepDiscoveryHealthAndModelsAreServedByTheAutonomousHost()
    {
        var values = new Dictionary<string, string?>
        {
            ["Foundry__ProjectEndpoint"] = "https://foundry.example/api/projects/demo",
            ["Foundry__InferenceEndpoint"] = "https://foundry.example/openai/v1",
            ["Foundry__AuthenticationMode"] = "ApiKeyEnvironment",
            ["FOUNDRY_API_KEY"] = Guid.NewGuid().ToString("N")
        };
        var previous = values.ToDictionary(value => value.Key, value => Environment.GetEnvironmentVariable(value.Key));
        try
        {
            foreach (var value in values) Environment.SetEnvironmentVariable(value.Key, value.Value);
            using var factory = new WebApplicationFactory<FoundryAepModelProvider>()
                .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.RemoveAll<FoundryAepModelProvider>();
                    services.AddSingleton(new FoundryAepModelProvider(
                        Client((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """
                            {"value":[{"type":"ModelDeployment","name":"chat-a","capabilities":{"chat":true}}]}
                            """))),
                        Options(),
                        new FoundryRequestAuthenticator(Options())));
                }));
            using var client = factory.CreateClient();
            var manifest = await client.GetStringAsync(AepProtocol.DiscoveryPath);
            var models = await client.GetStringAsync($"{AepProtocol.ModelProvidersPath}/microsoft-foundry/models");
            var health = await client.GetStringAsync($"{AepProtocol.ModelProvidersPath}/microsoft-foundry/health");

            StringAssert.Contains(manifest, "microsoft-foundry");
            StringAssert.Contains(models, "chat-a");
            StringAssert.Contains(health, "available");
        }
        finally
        {
            foreach (var value in previous) Environment.SetEnvironmentVariable(value.Key, value.Value);
        }
    }

    private static FoundryExtensionOptions Options() => new()
    {
        ProjectEndpoint = new Uri("https://foundry.example/api/projects/demo"),
        InferenceEndpoint = new Uri("https://foundry.example/openai/v1"),
        AuthenticationMode = FoundryAuthenticationMode.ApiKeyEnvironment
    };

    private static IConfiguration Configuration(string project, string inference, string mode) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Foundry:ProjectEndpoint"] = project,
            ["Foundry:InferenceEndpoint"] = inference,
            ["Foundry:AuthenticationMode"] = mode
        }).Build();

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) =>
        new(new StubHandler(handle));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static async Task WithEnvironmentKeyAsync(Func<FoundryRequestAuthenticator, Task> action)
    {
        var previous = Environment.GetEnvironmentVariable("FOUNDRY_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FOUNDRY_API_KEY", Guid.NewGuid().ToString("N"));
            await action(new FoundryRequestAuthenticator(Options()));
        }
        finally { Environment.SetEnvironmentVariable("FOUNDRY_API_KEY", previous); }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
}
