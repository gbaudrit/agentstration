using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Ollama;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class OllamaPackagingTests
{
    [TestMethod]
    public async Task ProtectedManifestKeepsIndependentAssemblyAndOptionContractVersions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ollama-packaging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var keyFile = Path.Combine(directory, "key");
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        await File.WriteAllTextAsync(keyFile, key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            await using var extension = new FixtureFactory(keyFile);
            using var http = extension.CreateClient();
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-proof");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var client = new AepClient(http);
            var manifest = await client.DiscoverAsync();
            Assert.AreEqual("Agentstration.Extensions.Ollama", manifest.Extension.Id);
            Assert.AreEqual("0.1.0", manifest.Extension.Version);
            Assert.AreEqual(typeof(OllamaAepModelProvider).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0], manifest.Extension.Version);
            Assert.AreEqual(AepProtocol.Version, manifest.ProtocolVersion);
            Assert.AreEqual("ollama", manifest.Contributions.ModelProviders.Single().Id);
            var configuration = await client.GetConfigurationAsync();
            var options = configuration.OptionSets.Single(value => value.Id == OllamaOptionContracts.ModelProfileOptionSet);
            Assert.AreEqual("1.0.0", options.PreferredVersion);
            Assert.AreEqual("1.0.0", options.Versions.Single().Version);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task NativeHealthModelsAndChatUseDeterministicHttpWithoutAModelServer()
    {
        await using var extension = new FixtureFactory();
        using var http = extension.CreateClient();
        var client = new AepClient(http);
        var provider = client.CreateModelProvider("ollama");
        Assert.AreEqual("available", (await provider.GetHealthAsync()).Status);
        var models = await provider.ListModelsAsync();
        Assert.AreEqual("fixture-model", models.Single().Id);
        var response = await provider.ChatAsync(new AepChatRequest("fixture-model", [new(AepRole.User, [AepContent.FromText("ping")])]));
        Assert.AreEqual("fixture-pong", response.Messages.Single().Contents.Single().Text);
    }

    private sealed class FixtureFactory(string? keyFile = null) : WebApplicationFactory<OllamaAepModelProvider>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ollama:Endpoint", "http://ollama.fixture.invalid");
            if (keyFile is not null)
            {
                builder.UseSetting("Aep:EnrollmentMode", "SharedKeyFile");
                builder.UseSetting("Aep:SharedKeyFile:Path", keyFile);
            }
            builder.ConfigureServices(services =>
            {
                foreach (var name in new[] { "ollama", OllamaInferenceOptions.HttpClientName })
                    services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new NativeFixture());
            });
        }
    }

    private sealed class NativeFixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = request.RequestUri!.AbsolutePath switch
            {
                "/" => "Ollama is running",
                "/api/version" => "{\"version\":\"0.0.0-fixture\"}",
                "/api/tags" => "{\"models\":[{\"name\":\"fixture-model\",\"model\":\"fixture-model\"}]}",
                "/api/show" => "{\"capabilities\":[\"completion\",\"tools\"]}",
                "/api/chat" => "{\"model\":\"fixture-model\",\"message\":{\"role\":\"assistant\",\"content\":\"fixture-pong\"},\"done\":true,\"done_reason\":\"stop\"}\n",
                _ => throw new InvalidOperationException("Unexpected native request: " + request.RequestUri.AbsolutePath)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, request.RequestUri.AbsolutePath == "/api/chat" ? "application/x-ndjson" : "application/json")
            });
        }
    }
}
