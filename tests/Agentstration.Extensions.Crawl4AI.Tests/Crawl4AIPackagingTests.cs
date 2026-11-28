using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.Extensions.Crawl4AI.Tests;

[TestClass]
public sealed class Crawl4AIPackagingTests
{
    [TestMethod]
    public async Task ProtectedManifestUsesIndependentAssemblyVersionAndOriginalContribution()
    {
        var directory = Path.Combine(Path.GetTempPath(), "crawl4ai-packaging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var keyFile = Path.Combine(directory, "key");
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        await File.WriteAllTextAsync(keyFile, key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            await using var host = new WebApplicationFactory<Agentstration.Extensions.Crawl4AI.Crawl4AiOptions>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Aep:EnrollmentMode", "SharedKeyFile");
                builder.UseSetting("Aep:SharedKeyFile:Path", keyFile);
                builder.UseSetting("Crawl4AI:AllowedDomains:0", "fixture.example.test");
            });
            using var http = host.CreateClient();
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-proof");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var manifest = await new AepClient(http).DiscoverAsync();
            Assert.AreEqual("Agentstration.Extensions.Crawl4AI", manifest.Extension.Id);
            Assert.AreEqual("0.1.0", manifest.Extension.Version);
            Assert.AreEqual(typeof(Agentstration.Extensions.Crawl4AI.Crawl4AiOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0], manifest.Extension.Version);
            Assert.AreEqual(AepProtocol.Version, manifest.ProtocolVersion);
            Assert.IsTrue(manifest.Contributions.Tools!.Any(value => value.Id == "crawl4ai.web.fetch"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
