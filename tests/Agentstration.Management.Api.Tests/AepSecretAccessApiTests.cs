using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Secrets.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class AepSecretAccessApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task LocalVaultValueIsLateBoundThroughCapabilityAndAbsentFromResourceReads()
    {
        await using var factory = Factory();
        using var http = factory.CreateClient();
        var targets = (await http.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            "/api/resource-scopes/targets?kind=Secret"))!;
        var workspace = targets.Single(value => value.Kind == ResourceScopeKind.Workspace).ScopeRef;
        const string value = "local-vault-private-value";

        using var vault = await http.PostAsJsonAsync("/api/vaults", new CreateVaultRequest(
            "aep-local-vault", new VaultProperties { DisplayName = "AEP local Vault", ProviderType = "local" }, workspace));
        Assert.AreEqual(HttpStatusCode.Created, vault.StatusCode);
        // Vault initialization is restricted to Platform administrators; initialize the
        // fixture under the system context while exercising the public secret APIs below.
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            await factory.Services.GetRequiredService<SecretManagementService>()
                .InitializeVaultExactAsync(workspace, "aep-local-vault", CancellationToken.None);
        using var secret = await http.PostAsJsonAsync("/api/secrets", new CreateSecretRequest(
            "aep-local-secret", new SecretProperties
            {
                DisplayName = "AEP local Secret",
                Vault = new ResourceReference("aep-local-vault", workspace),
                Key = "credential"
            }, workspace));
        Assert.AreEqual(HttpStatusCode.Created, secret.StatusCode);
        using var set = await http.PutAsJsonAsync(
            $"/api/secrets/aep-local-secret/value?scopeRef={Uri.EscapeDataString(workspace.Value)}",
            new SetSecretValueRequest(value));
        Assert.AreEqual(HttpStatusCode.NoContent, set.StatusCode);

        var capabilities = factory.Services.GetRequiredService<ISecretCapabilityService>();
        var context = new SecretCapabilityContext(
            ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default, "ExtensionRegistration", "extension"),
            "extension.test",
            ScopedResourceAddress.Create(workspace, ResourceNamespace.Default, "ModelProfile", "profile"),
            "credential", "local-run");
        async Task<SecretCapabilityHandle> IssueAsync()
        {
            using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            return await capabilities.IssueAsync(context, new SecretBinding("credential",
                new SecretReference(new(ResourceNamespace.Default, "Secret", "aep-local-secret"), workspace)),
                ["credential"], CancellationToken.None);
        }

        foreach (var wrong in new[]
        {
            (Extension: "other.extension", Requirement: context.RequirementId, Execution: context.ExecutionId),
            (Extension: context.ExtensionId, Requirement: "undeclared", Execution: context.ExecutionId),
            (Extension: context.ExtensionId, Requirement: context.RequirementId, Execution: "other-run")
        })
        {
            var wrongHandle = await IssueAsync();
            using var denied = await http.PostAsJsonAsync(AepProtocol.SecretAccessPath,
                new AepSecretAccessRequest(AepProtocol.SecretAccessVersion,
                    wrong.Extension, wrong.Requirement, wrong.Execution, wrongHandle.RevealForTransport()));
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            var error = await denied.Content.ReadAsStringAsync();
            StringAssert.Contains(error, "context_mismatch");
            Assert.IsFalse(error.Contains(value, StringComparison.Ordinal));
            Assert.IsFalse(error.Contains(wrongHandle.RevealForTransport(), StringComparison.Ordinal));
        }
        using (var unknown = await http.PostAsJsonAsync(AepProtocol.SecretAccessPath,
            new AepSecretAccessRequest(AepProtocol.SecretAccessVersion,
                context.ExtensionId, context.RequirementId, context.ExecutionId, "unknown-capability")))
            Assert.AreEqual(HttpStatusCode.Forbidden, unknown.StatusCode);

        var handle = await IssueAsync();
        var grant = new AepSecretAccessGrant(AepProtocol.SecretAccessVersion,
            new Uri(http.BaseAddress!, AepProtocol.SecretAccessPath), context.ExtensionId,
            context.RequirementId, context.ExecutionId, handle.RevealForTransport());
        var bytes = await new AepSecretAccessClient(http).RedeemAsync(grant);
        Assert.AreEqual(value, Encoding.UTF8.GetString(bytes));
        CryptographicOperations.ZeroMemory(bytes);

        using var list = await http.GetAsync("/api/secrets");
        var listJson = await list.Content.ReadAsStringAsync();
        Assert.IsFalse(listJson.Contains(value, StringComparison.Ordinal));
        Assert.IsFalse(listJson.Contains(handle.RevealForTransport(), StringComparison.Ordinal));
        var replay = await Assert.ThrowsExactlyAsync<AepProtocolException>(() => new AepSecretAccessClient(http).RedeemAsync(grant));
        Assert.AreEqual("capability_invalid", replay.Code);
    }

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

    [TestMethod]
    public async Task OversizedResolvedValueFailsWithoutReturningMaterial()
    {
        var capabilities = new FixedCapabilityService { ValueBytes = new byte[65_537] };
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISecretCapabilityService>();
            services.AddSingleton<ISecretCapabilityService>(capabilities);
        }));
        using var http = factory.CreateClient();
        using var response = await http.PostAsJsonAsync(AepProtocol.SecretAccessPath,
            new AepSecretAccessRequest(AepProtocol.SecretAccessVersion,
                "sample.extension", "credential", "execution-1", "opaque-capability"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.AreEqual(true, response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "secret_value_invalid");
        Assert.IsFalse(body.Contains("opaque-capability", StringComparison.Ordinal));
    }

    private sealed class FixedCapabilityService : ISecretCapabilityService
    {
        private bool redeemed;
        public byte[] ValueBytes { get; set; } = Encoding.UTF8.GetBytes("sample-secret");

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
                new SecretValue(ValueBytes)));
        }

        public void Revoke(SecretCapabilityHandle handle) { }
        public void RevokeExecution(SecretCapabilityContext context) { }
        public int PruneExpired() => 0;
    }
}
