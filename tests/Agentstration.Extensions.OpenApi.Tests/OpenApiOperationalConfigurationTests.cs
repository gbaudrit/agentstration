using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Extensions.OpenApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Extensions.OpenApi.Tests;

[TestClass]
public sealed class OpenApiOperationalConfigurationTests
{
    [TestMethod]
    public async Task UnconfiguredAuthenticatedHostAppliesDomainsHotAndPersistsThemAcrossRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "openapi-configuration-" + Guid.NewGuid().ToString("N"), "state.json");
        var token = AepStaticBearerCredentials.Generate("authority").AccessToken;
        try
        {
            await using (var host = Host(path, token))
            {
                using var anonymous = host.CreateClient();
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(AepOperationalConfigurationProtocol.Path)).StatusCode);
                using var http = host.CreateClient();
                var client = new AepClient(http, new StaticAepAccessTokenProvider(token));
                var before = await client.GetOperationalConfigurationAsync();
                Assert.IsNotNull(before);
                Assert.IsFalse(before.Ready);
                var policy = host.Services.GetRequiredService<OpenApiDestinationPolicy>();
                await Assert.ThrowsExactlyAsync<OpenApiExtensionException>(() => policy.ValidateAsync("https://api.example.test", default));
                var request = Request(before, Guid.NewGuid(), 1, "revision-one", ["example.test"]);
                var applied = await client.ApplyOperationalConfigurationAsync(request);
                Assert.AreEqual("revision-one", applied.AppliedRevision);
                Assert.IsTrue(applied.Ready);
                await policy.ValidateAsync("https://api.example.test", default);
                Assert.AreEqual(applied.AppliedRevision, (await client.ApplyOperationalConfigurationAsync(request)).AppliedRevision);
                var revoked = Request(applied, request.ConfigurationId, 2, "revision-two", []);
                await client.ApplyOperationalConfigurationAsync(revoked);
                await Assert.ThrowsExactlyAsync<OpenApiExtensionException>(() => policy.ValidateAsync("https://api.example.test", default));
                await Assert.ThrowsExactlyAsync<AepProtocolException>(() => client.ApplyOperationalConfigurationAsync(request));
            }
            await using var restarted = Host(path, token);
            using var restartedHttp = restarted.CreateClient();
            var state = await new AepClient(restartedHttp, new StaticAepAccessTokenProvider(token)).GetOperationalConfigurationAsync();
            Assert.AreEqual("revision-two", state!.AppliedRevision);
            Assert.IsFalse(state.Ready);
            Assert.IsFalse((await File.ReadAllTextAsync(path)).Contains(token, StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [TestMethod]
    public async Task InvalidSchemaStaleIdentityAndDeploymentCeilingLeaveActivePolicyUnchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), "openapi-ceiling-" + Guid.NewGuid().ToString("N"), "state.json");
        var token = AepStaticBearerCredentials.Generate("authority").AccessToken;
        try
        {
            await using var host = Host(path, token, ceiling: "example.test");
            using var http = host.CreateClient();
            var client = new AepClient(http, new StaticAepAccessTokenProvider(token));
            var initial = (await client.GetOperationalConfigurationAsync())!;
            var first = Request(initial, Guid.NewGuid(), 1, "good", ["example.test"]);
            var active = await client.ApplyOperationalConfigurationAsync(first);
            foreach (var invalid in new[]
            {
                Request(active, first.ConfigurationId, 2, "bad-domain", ["other.test"]),
                Request(active, Guid.NewGuid(), 2, "bad-identity", ["example.test"]),
                Request(active, first.ConfigurationId, 2, "bad-schema", ["example.test"]) with { SchemaDigest = "sha256:invalid" },
                Request(active, first.ConfigurationId, 2, "bad-type", []) with { Values = [AepBoundValue.Inline("AllowedDomains", JsonSerializer.SerializeToElement(true))] },
                Request(active, first.ConfigurationId, 2, "too-many", Enumerable.Repeat("example.test", 129).ToArray()),
                Request(active, first.ConfigurationId, 2, "bad-empty", [""])
            })
                await Assert.ThrowsExactlyAsync<AepProtocolException>(() => client.ApplyOperationalConfigurationAsync(invalid));
            Assert.AreEqual("good", (await client.GetOperationalConfigurationAsync())!.AppliedRevision);
            using var oversized = new HttpRequestMessage(HttpMethod.Put, AepOperationalConfigurationProtocol.Path)
            {
                Content = new StringContent(new string(' ', AepOperationalConfigurationProtocol.MaximumBytes + 1))
            };
            oversized.Headers.Authorization = new("Bearer", token);
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, (await http.SendAsync(oversized)).StatusCode);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [TestMethod]
    public async Task DisabledEnrollmentNeverExposesOperationalConfigurationAnonymously()
    {
        await using var host = new WebApplicationFactory<global::Program>();
        using var client = host.CreateClient();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync(AepOperationalConfigurationProtocol.Path)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(AepOperationalConfigurationProtocol.Path, new { })).StatusCode);
    }

    private static AepOperationalConfigurationRequest Request(AepOperationalConfigurationState state, Guid id,
        long generation, string revision, string[] domains) => new(state.Descriptor.Id, state.Descriptor.Version,
            state.Descriptor.SchemaDigest, id, generation, revision, state.AppliedRevision,
            [AepBoundValue.Inline("AllowedDomains", JsonSerializer.SerializeToElement(domains))]);

    private static WebApplicationFactory<global::Program> Host(string path, string token, string? ceiling = null) =>
        new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenApi:ConfigurationStateFile", path);
            if (ceiling is not null) builder.UseSetting("OpenApi:DomainCeiling:0", ceiling);
            builder.ConfigureTestServices(services =>
            {
                services.AddAepStaticBearerAuthentication(options => options.AddToken("test", "authority", token));
                services.AddSingleton<IOpenApiAddressResolver, PublicResolver>();
            });
        });

    private sealed class PublicResolver : IOpenApiAddressResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("8.8.8.8")]);
    }
}
