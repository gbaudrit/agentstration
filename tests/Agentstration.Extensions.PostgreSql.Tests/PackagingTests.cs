using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using Agentstration.Aep.Abstractions;
using Agentstration.Asp.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;

namespace Agentstration.Extensions.PostgreSql.Tests;

[TestClass]
public sealed class PackagingTests
{
    [TestMethod]
    public async Task IndependentVersionAndDocumentDiscoveryNeedNoDatabase()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), "postgresql-packaging-" + Guid.NewGuid().ToString("N"));
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        await File.WriteAllTextAsync(keyFile, key);
        try
        {
            await using var host = new WebApplicationFactory<PostgreSqlProgram>().WithWebHostBuilder(builder => builder
                .UseSetting("Aep:EnrollmentMode", "SharedKeyFile")
                .UseSetting("Aep:SharedKeyFile:Path", keyFile)
                .UseSetting("PostgreSqlDocuments:AllowedClients:0", "agentstration-development"));
            using var client = host.CreateClient();
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-proof");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var manifest = await client.GetFromJsonAsync<AepManifest>(AepProtocol.DiscoveryPath, AepProtocol.JsonOptions);
            Assert.IsNotNull(manifest);
            Assert.AreEqual("agentstration.extensions.postgresql", manifest.Extension.Id);
            Assert.AreEqual("0.1.0", manifest.Extension.Version);
            Assert.AreEqual(typeof(PostgreSqlProgram).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion.Split('+')[0], manifest.Extension.Version);
            Assert.AreEqual(new Version(0, 1, 0, 0), typeof(PostgreSqlProgram).Assembly.GetName().Version);
            Assert.AreEqual(AepProtocol.Version, manifest.ProtocolVersion);
            var storage = manifest.Contributions.StorageTypes!.Single();
            Assert.AreEqual("postgresql.jsonb", storage.Id);
            Assert.AreEqual("1.0.0", storage.PreferredVersion);
            var version = storage.Versions.Single();
            Assert.AreEqual("1.0.0", version.Version);
            Assert.AreEqual(AspDocuments.Profile, version.Asp.Profile);
            Assert.AreEqual(AepValueProtection.Secured, version.Requirements.Single(value => value.Id == "password").Protection);
            Assert.AreEqual("postgresql", manifest.Contributions.ConnectedServiceTypes!.Single().Id);
            Assert.AreEqual("1.0.0", manifest.Contributions.ConnectedServiceTypes!.Single().PreferredVersion);
            Assert.AreEqual("/mcp", manifest.Mcp!.Servers.Single().Endpoint);
            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new("http://localhost/mcp") }, client));
            var tools = await mcp.ListToolsAsync();
            Assert.IsTrue(tools.Any(tool => tool.Name == "aspdoc_get"));
            Assert.IsTrue(tools.Any(tool => tool.Name == "aspdoc_query"));
        }
        finally
        {
            File.Delete(keyFile);
        }
    }
}
