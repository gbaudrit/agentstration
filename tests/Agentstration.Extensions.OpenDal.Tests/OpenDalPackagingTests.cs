using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.Extensions.OpenDal.Tests;

[TestClass]
public sealed class OpenDalPackagingTests
{
    [TestMethod]
    public async Task ProtectedManifestUsesIndependentAssemblyVersionAndOriginalContribution()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opendal-packaging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var keyFile = Path.Combine(directory, "key");
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        await File.WriteAllTextAsync(keyFile, key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            await using var host = new WebApplicationFactory<Agentstration.Extensions.OpenDal.OpenDalObjectProvider>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Aep:EnrollmentMode", "SharedKeyFile");
                builder.UseSetting("Aep:SharedKeyFile:Path", keyFile);
                builder.UseSetting("OpenDal:RootDirectory", directory);
                builder.UseSetting("OpenDal:AllowedClients:0", "packaging");
            });
            using var http = host.CreateClient();
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-proof");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var manifest = await new AepClient(http).DiscoverAsync();
            Assert.AreEqual("agentstration.extensions.opendal", manifest.Extension.Id);
            Assert.AreEqual("0.1.0", manifest.Extension.Version);
            Assert.AreEqual(typeof(Agentstration.Extensions.OpenDal.OpenDalProgram).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0], manifest.Extension.Version);
            Assert.AreEqual(AepProtocol.Version, manifest.ProtocolVersion);
            Assert.IsTrue(manifest.Contributions.StorageTypes!.Any(value => value.Id == "opendal.fs"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
