using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.SourceProviders.Git.Tests;

[TestClass]
public sealed class GitPackagingTests
{
    [TestMethod]
    public async Task ProtectedManifestUsesIndependentAssemblyVersionAndOriginalContribution()
    {
        var directory = Path.Combine(Path.GetTempPath(), "git-packaging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var keyFile = Path.Combine(directory, "key");
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        await File.WriteAllTextAsync(keyFile, key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            await using var host = new WebApplicationFactory<Agentstration.Extensions.Git.GitSourceProvider>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Aep:EnrollmentMode", "SharedKeyFile");
                builder.UseSetting("Aep:SharedKeyFile:Path", keyFile);

            });
            using var http = host.CreateClient();
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-proof");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var manifest = await new AepClient(http).DiscoverAsync();
            Assert.AreEqual("Agentstration.Extensions.Git", manifest.Extension.Id);
            Assert.AreEqual("0.1.0", manifest.Extension.Version);
            Assert.AreEqual(typeof(Agentstration.Extensions.Git.GitSourceProvider).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0], manifest.Extension.Version);
            Assert.AreEqual(AepProtocol.Version, manifest.ProtocolVersion);
            Assert.IsTrue(manifest.Contributions.SourceProviders!.Any(value => value.Id == "git"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
