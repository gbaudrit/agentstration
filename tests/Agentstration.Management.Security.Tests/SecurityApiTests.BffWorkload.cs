using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Security.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class SecurityApiTests
{
    [TestMethod]
    public async Task RegisteredBffCredentialAuthenticatesWithoutLeakingCredentialMaterial()
    {
        var fixture = BffFixture.Create();
        try
        {
            await using var factory = fixture.Factory();
            using var client = UnredirectedClient(factory);
            using var request = fixture.SignedRequest("primary");
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            StringAssert.Contains(body, "console-bff");
            StringAssert.Contains(body, "instance-a");
            Assert.IsFalse(body.Contains(fixture.Secret, StringComparison.Ordinal));
            Assert.IsFalse(body.Contains(fixture.Directory, StringComparison.OrdinalIgnoreCase));
        }
        finally { fixture.Dispose(); }
    }

    [TestMethod]
    public async Task BffLocalSessionAuthenticationReturnsAuthoritativePrincipalAndRevalidatesContext()
    {
        var fixture = BffFixture.Create();
        try
        {
            await using var factory = fixture.Factory();
            using var client = UnredirectedClient(factory);
            await BootstrapAsync(client, "bff-session-user");
            using var login = fixture.SignedRequest(
                "primary",
                "/api/internal/bff/sessions/local",
                method: HttpMethod.Post,
                json: JsonSerializer.Serialize(new BffLocalSessionRequest("bff-session-user", LocalPassword)));
            using var loginResponse = await client.SendAsync(login);
            var identity = await loginResponse.Content.ReadFromJsonAsync<BffSessionIdentityResponse>();

            Assert.AreEqual(HttpStatusCode.OK, loginResponse.StatusCode);
            Assert.IsNotNull(identity);
            Assert.AreEqual(BffSessionAuthenticationMethods.Local, identity.AuthenticationMethod);
            Assert.AreNotEqual(Guid.Empty, identity.TenantId);
            Assert.AreNotEqual(Guid.Empty, identity.WorkspaceId);

            using var validate = fixture.SignedRequest(
                "primary",
                "/api/internal/bff/sessions/validate",
                method: HttpMethod.Post,
                json: JsonSerializer.Serialize(new BffSessionValidationRequest(
                    identity.PrincipalId,
                    identity.TenantId,
                    identity.WorkspaceId,
                    identity.AuthenticationMethod,
                    identity.Provider,
                    identity.AuthenticationVersion)));
            using var validateResponse = await client.SendAsync(validate);
            var validation = await validateResponse.Content.ReadFromJsonAsync<BffSessionValidationResponse>();
            Assert.AreEqual(HttpStatusCode.OK, validateResponse.StatusCode);
            Assert.IsTrue(validation?.Active);

            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalIdentityUser>>();
                var account = await users.FindByNameAsync("bff-session-user")
                    ?? throw new AssertFailedException("The local account was not created.");
                var revoked = await users.UpdateSecurityStampAsync(account);
                Assert.IsTrue(revoked.Succeeded);
            }
            using var revokedSession = fixture.SignedRequest(
                "primary",
                "/api/internal/bff/sessions/validate",
                method: HttpMethod.Post,
                json: JsonSerializer.Serialize(new BffSessionValidationRequest(
                    identity.PrincipalId,
                    identity.TenantId,
                    identity.WorkspaceId,
                    identity.AuthenticationMethod,
                    identity.Provider,
                    identity.AuthenticationVersion)));
            using var revokedResponse = await client.SendAsync(revokedSession);
            var revokedValidation = await revokedResponse.Content.ReadFromJsonAsync<BffSessionValidationResponse>();
            Assert.AreEqual(HttpStatusCode.OK, revokedResponse.StatusCode);
            Assert.IsFalse(revokedValidation?.Active);

            using var invalid = fixture.SignedRequest(
                "primary",
                "/api/internal/bff/sessions/local",
                method: HttpMethod.Post,
                json: JsonSerializer.Serialize(new BffLocalSessionRequest("bff-session-user", "wrong-password")));
            using var invalidResponse = await client.SendAsync(invalid);
            Assert.AreEqual(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
        }
        finally { fixture.Dispose(); }
    }

    [TestMethod]
    public async Task BffAuthenticationRejectsReplayCrossInstanceAndHumanCredentials()
    {
        var fixture = BffFixture.Create();
        try
        {
            await using var factory = fixture.Factory();
            using var client = UnredirectedClient(factory);
            const string nonce = "fixed-replay-nonce";
            using var first = fixture.SignedRequest("primary", nonce: nonce);
            using var firstResponse = await client.SendAsync(first);
            using var replay = fixture.SignedRequest("primary", nonce: nonce);
            using var replayResponse = await client.SendAsync(replay);
            using var wrongInstance = fixture.SignedRequest("primary", targetInstance: "instance-b");
            using var wrongInstanceResponse = await client.SendAsync(wrongInstance);
            using var anonymousResponse = await client.GetAsync("/api/internal/bff/trust");
            using var bearer = new HttpRequestMessage(HttpMethod.Get, "/api/internal/bff/trust");
            bearer.Headers.Authorization = new("Bearer", "agt_not-a-bff-credential");
            using var bearerResponse = await client.SendAsync(bearer);

            Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, wrongInstanceResponse.StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, bearerResponse.StatusCode);
        }
        finally { fixture.Dispose(); }
    }

    [TestMethod]
    public async Task OverlappingCredentialsAuthenticateAndRevocationIsExplicit()
    {
        var fixture = BffFixture.Create(includeNext: true);
        try
        {
            await using (var factory = fixture.Factory())
            {
                using var client = UnredirectedClient(factory);
                using var primary = fixture.SignedRequest("primary");
                using var next = fixture.SignedRequest("next");
                var responses = await Task.WhenAll(client.SendAsync(primary), client.SendAsync(next));
                using var primaryResponse = responses[0];
                using var nextResponse = responses[1];
                Assert.AreEqual(HttpStatusCode.OK, primaryResponse.StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, nextResponse.StatusCode);
            }

            await using var revokedFactory = fixture.Factory(revokePrimary: true);
            using var revokedClient = UnredirectedClient(revokedFactory);
            using var revoked = fixture.SignedRequest("primary");
            using var revokedResponse = await revokedClient.SendAsync(revoked);
            using var active = fixture.SignedRequest("next");
            using var activeResponse = await revokedClient.SendAsync(active);
            Assert.AreEqual(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, activeResponse.StatusCode);
            var audit = await revokedFactory.Services.GetRequiredService<ISecurityAuditStore>()
                .ListLatestAsync(100, CancellationToken.None);
            Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.BffWorkloadCredentialRevoked));
        }
        finally { fixture.Dispose(); }
    }

    [TestMethod]
    public async Task BffCredentialDoesNotAuthorizeBusinessApisAndActionsAreAudited()
    {
        var fixture = BffFixture.Create(includeNext: true);
        try
        {
            await using var factory = fixture.Factory();
            using var client = UnredirectedClient(factory);
            using var trust = fixture.SignedRequest("primary");
            using var trustResponse = await client.SendAsync(trust);
            using var business = fixture.SignedRequest("primary", "/api/identity/principals");
            using var businessResponse = await client.SendAsync(business);
            using var invalid = fixture.SignedRequest("primary", signatureOverride: "invalid");
            using var invalidResponse = await client.SendAsync(invalid);

            var events = await factory.Services.GetRequiredService<ISecurityAuditStore>()
                .ListLatestAsync(100, CancellationToken.None);
            Assert.AreEqual(HttpStatusCode.OK, trustResponse.StatusCode);
            Assert.IsTrue(businessResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
            Assert.AreEqual(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
            Assert.IsTrue(events.Any(value => value.Action == SecurityAuditActions.BffWorkloadEnrolled));
            Assert.IsTrue(events.Any(value => value.Action == SecurityAuditActions.BffWorkloadCredentialRotated));
            Assert.IsTrue(events.Any(value => value.Action == SecurityAuditActions.BffWorkloadAuthenticated));
            Assert.IsTrue(events.Any(value => value.Action == SecurityAuditActions.BffWorkloadAuthenticationFailed));
            Assert.IsFalse(events.Any(value => value.ReasonCode?.Contains(fixture.Secret, StringComparison.Ordinal) == true));
        }
        finally { fixture.Dispose(); }
    }

    private sealed class BffFixture : IDisposable
    {
        private readonly string nextSecret;

        private BffFixture(string directory, string secret, string nextSecret, bool includeNext)
        {
            Directory = directory;
            Secret = secret;
            this.nextSecret = nextSecret;
            IncludeNext = includeNext;
            File.WriteAllText(Path.Combine(directory, "primary.key"), secret);
            File.WriteAllText(Path.Combine(directory, "next.key"), nextSecret);
        }

        public string Directory { get; }
        public string Secret { get; }
        private bool IncludeNext { get; }

        public static BffFixture Create(bool includeNext = false)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"agentstration-bff-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            return new(directory, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), includeNext);
        }

        public WebApplicationFactory<Program> Factory(bool revokePrimary = false) =>
            SecurityApiTests.Factory("Local").WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Agentstration:BffWorkloadTrust:Enabled", "true");
                builder.UseSetting("Agentstration:BffWorkloadTrust:InstanceId", "instance-a");
                Credential(builder, 0, "primary", Path.Combine(Directory, "primary.key"), revokePrimary);
                if (IncludeNext) Credential(builder, 1, "next", Path.Combine(Directory, "next.key"), false);
            });

        public HttpRequestMessage SignedRequest(
            string credentialId,
            string path = "/api/internal/bff/trust",
            string? nonce = null,
            string targetInstance = "instance-a",
            string? signatureOverride = null,
            HttpMethod? method = null,
            string? json = null)
        {
            method ??= HttpMethod.Get;
            var content = json is null ? [] : Encoding.UTF8.GetBytes(json);
            var request = new HttpRequestMessage(method, path);
            if (json is not null)
                request.Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                };
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            nonce ??= BffWorkloadAuthentication.CreateNonce();
            var hash = BffWorkloadAuthentication.HashContent(content);
            var canonical = BffWorkloadAuthentication.Canonicalize(method.Method, path, targetInstance, timestamp, nonce, hash);
            var key = Encoding.UTF8.GetBytes(credentialId == "primary" ? Secret : nextSecret);
            request.Headers.Add(BffWorkloadAuthentication.WorkloadHeader, "console-bff");
            request.Headers.Add(BffWorkloadAuthentication.CredentialHeader, credentialId);
            request.Headers.Add(BffWorkloadAuthentication.InstanceHeader, targetInstance);
            request.Headers.Add(BffWorkloadAuthentication.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add(BffWorkloadAuthentication.NonceHeader, nonce);
            request.Headers.Add(BffWorkloadAuthentication.ContentHashHeader, hash);
            request.Headers.Add(BffWorkloadAuthentication.SignatureHeader,
                signatureOverride ?? BffWorkloadAuthentication.Sign(key, canonical));
            return request;
        }

        private static void Credential(IWebHostBuilder builder, int index, string id, string path, bool revoked)
        {
            var prefix = $"Agentstration:BffWorkloadTrust:Credentials:{index}";
            builder.UseSetting($"{prefix}:WorkloadId", "console-bff");
            builder.UseSetting($"{prefix}:CredentialId", id);
            builder.UseSetting($"{prefix}:SharedKeyFile", path);
            builder.UseSetting($"{prefix}:Revoked", revoked.ToString());
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
