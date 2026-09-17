using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class AepSecretAccessApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task ExtensionClientRedeemsOneUseGrantWithoutExposingValueInErrors()
    {
        var capabilities = new FixedCapabilityService();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISecretCapabilityService>();
            services.AddSingleton<ISecretCapabilityService>(capabilities);
        }));
        using var http = factory.CreateClient();
        var client = new AepSecretAccessClient(http);
        var grant = new AepSecretAccessGrant(AepProtocol.SecretAccessVersion,
            new Uri(http.BaseAddress!, AepProtocol.SecretAccessPath),
            "sample.extension", "credential", "execution-1", "opaque-capability");

        var bytes = await client.RedeemAsync(grant);
        Assert.AreEqual("sample-secret", Encoding.UTF8.GetString(bytes));
        CryptographicOperations.ZeroMemory(bytes);
        Assert.AreEqual("[REDACTED]", grant.ToString());
        var replay = await Assert.ThrowsExactlyAsync<AepProtocolException>(() => client.RedeemAsync(grant));
        Assert.AreEqual("capability_invalid", replay.Code);

        using var wrong = await http.PostAsJsonAsync(AepProtocol.SecretAccessPath,
            new AepSecretAccessRequest(AepProtocol.SecretAccessVersion, "other.extension", "credential", "execution-1", "opaque-capability"));
        Assert.AreEqual(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.AreEqual(true, wrong.Headers.CacheControl?.NoStore);
        var error = await wrong.Content.ReadAsStringAsync();
        Assert.IsFalse(error.Contains("sample-secret", StringComparison.Ordinal));
        Assert.IsFalse(error.Contains("opaque-capability", StringComparison.Ordinal));
    }

    private sealed class FixedCapabilityService : ISecretCapabilityService
    {
        private bool redeemed;

        public Task<SecretCapabilityHandle> IssueAsync(SecretCapabilityContext context, SecretBinding binding,
            IReadOnlyCollection<string> declaredRequirements, CancellationToken lifetimeToken,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ResolvedSecret> RedeemAsync(string token, SecretCapabilityContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ResolvedSecret> RedeemAsync(string token, string extensionId, string requirementId,
            string executionId, CancellationToken cancellationToken = default)
        {
            if (redeemed || token != "opaque-capability" || extensionId != "sample.extension"
                || requirementId != "credential" || executionId != "execution-1")
                throw new SecretCapabilityException("capability_invalid", "Invalid capability.");
            redeemed = true;
            return Task.FromResult(new ResolvedSecret(
                new(ResourceNamespace.Default, "Secret", "key"),
                new(ResourceNamespace.Default, "Vault", "vault"),
                new SecretValue(Encoding.UTF8.GetBytes("sample-secret"))));
        }

        public void Revoke(SecretCapabilityHandle handle) { }
        public void RevokeExecution(SecretCapabilityContext context) { }
        public int PruneExpired() => 0;
    }
}
