using System.Net;
using System.Net.Http.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Aep.Validation;

namespace Agentstration.Aep.Tests;

[TestClass]
public sealed class ProtocolVersionTests
{
    [TestMethod]
    [DataRow("2026-08-01")]
    [DataRow("2026-09-18")]
    [DataRow("2026-09-22")]
    public async Task ClientRejectsDatedDiscoveryWithoutFallback(string version)
    {
        using var handler = new ManifestHandler(Manifest(version));
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var exception = await Assert.ThrowsExactlyAsync<AepProtocolException>(() => new AepClient(http).GetManifestAsync());

        Assert.AreEqual("protocol_incompatible", exception.Code);
        StringAssert.Contains(exception.Message, "agentstration.io/v1");
        Assert.AreEqual(1, handler.Requests);
    }

    [TestMethod]
    [DataRow("agentstration.io/v1", true)]
    [DataRow("2026-08-01", false)]
    [DataRow("2026-09-18", false)]
    [DataRow("2026-09-22", false)]
    public async Task ValidatorAcceptsOnlyTheCanonicalVersion(string version, bool supported)
    {
        var result = await new AepValidator().ValidateAsync(new ManifestClient(Manifest(version)));

        Assert.AreEqual(supported, result.IsValid);
        Assert.AreEqual(!supported, result.Issues.Any(issue => issue.Code == "AEP003"));
    }

    private static AepManifest Manifest(string version) => new(
        version, new("test", "Test", "1.0.0"),
        new Dictionary<string, AepCapabilityDescriptor> { [AepCapabilityNames.Health] = new("1.0", AepProtocol.HealthPath) }, new([]));

    private sealed class ManifestHandler(AepManifest manifest) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.AreEqual(AepProtocol.DiscoveryPath, request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(manifest, options: AepProtocol.JsonOptions)
            });
        }
    }

    private sealed class ManifestClient(AepManifest manifest) : IAepClient
    {
        public Task<AepManifest> GetManifestAsync(CancellationToken cancellationToken = default) => Task.FromResult(manifest);
        public Task<AepHealth> GetHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AepHealth("available"));
        public Task<IReadOnlyDictionary<string, AepCapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => Task.FromResult(manifest.Capabilities);
        public Task<AepConfigurationCatalog> GetConfigurationAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AepOptionMigrationResponse> MigrateOptionsAsync(AepOptionMigrationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
