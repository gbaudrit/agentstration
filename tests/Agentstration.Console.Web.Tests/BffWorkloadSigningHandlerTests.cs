using System.Net;
using System.Text;
using Agentstration.Console.Web.Security;
using Agentstration.Identity.Contracts;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentstration.Console.Web.Tests;

[TestClass]
public sealed class BffWorkloadSigningHandlerTests
{
    [TestMethod]
    public async Task DedicatedClientSignsAnInstanceBoundRequestWithoutSendingTheSecret()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-bff-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "primary.key");
        const string secret = "0123456789abcdef0123456789abcdef";
        File.WriteAllText(path, secret);
        try
        {
            var capture = new CaptureHandler();
            var handler = new BffWorkloadSigningHandler(
                new StaticOptionsMonitor<BffWorkloadClientOptions>(new()
                {
                    Enabled = true,
                    WorkloadId = "console-bff",
                    CredentialId = "primary",
                    SharedKeyFile = path,
                    TargetInstanceId = "instance-a"
                }),
                new TestHostEnvironment(directory),
                new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)))
            { InnerHandler = capture };
            using var client = new HttpClient(handler) { BaseAddress = new("http://authority/") };

            using var response = await client.GetAsync("api/internal/bff/trust?scope=session");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var request = capture.Request ?? throw new AssertFailedException("No request was captured.");
            var hash = request.Headers.GetValues(BffWorkloadAuthentication.ContentHashHeader).Single();
            var nonce = request.Headers.GetValues(BffWorkloadAuthentication.NonceHeader).Single();
            var signature = request.Headers.GetValues(BffWorkloadAuthentication.SignatureHeader).Single();
            var canonical = BffWorkloadAuthentication.Canonicalize(
                "GET", "/api/internal/bff/trust?scope=session", "instance-a", 1_800_000_000, nonce, hash);

            Assert.IsTrue(BffWorkloadAuthentication.Verify(Encoding.UTF8.GetBytes(secret), canonical, signature));
            Assert.IsFalse(request.Headers.SelectMany(value => value.Value).Any(value => value.Contains(secret, StringComparison.Ordinal)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void EnabledClientRequiresStableIdentifiersAndCredentialPath()
    {
        Assert.IsFalse(new BffWorkloadClientOptions { Enabled = true }.Validate());
        Assert.IsTrue(new BffWorkloadClientOptions { Enabled = false }.Validate());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
