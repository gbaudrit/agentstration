using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Security.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Agentstration.Management.Tests;

public sealed partial class SecurityApiTests
{
    [TestMethod]
    public async Task DevelopmentAuthenticationDoesNotAcceptAnInvalidInternalDelegation()
    {
        await using var factory = Factory("Development");
        using var client = UnredirectedClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/identity/context");
        request.Headers.Authorization = new("Bearer", "agd_invalid");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

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
    public async Task BffDelegationIsAudienceBoundAndRechecksCurrentPrincipalState()
    {
        var fixture = BffFixture.Create();
        try
        {
            await using var factory = fixture.Factory();
            using var client = UnredirectedClient(factory);
            await BootstrapAsync(client, "delegated-user");
            using var login = fixture.SignedRequest("primary", "/api/internal/bff/sessions/local",
                method: HttpMethod.Post,
                json: JsonSerializer.Serialize(new BffLocalSessionRequest("delegated-user", LocalPassword)));
            using var loginResponse = await client.SendAsync(login);
            var identity = await loginResponse.Content.ReadFromJsonAsync<BffSessionIdentityResponse>();
            Assert.AreEqual(HttpStatusCode.OK, loginResponse.StatusCode);
            Assert.IsNotNull(identity);

            var sessionId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var delegationRequest = new BffDelegationRequest(
                identity.PrincipalId, identity.TenantId, identity.WorkspaceId, sessionId,
                identity.AuthenticationMethod, identity.Provider, identity.AuthenticationVersion,
                InternalDelegationAudiences.Management);
            using var humanOnly = await client.PostAsJsonAsync("/api/internal/bff/delegations", delegationRequest);
            Assert.AreEqual(HttpStatusCode.Unauthorized, humanOnly.StatusCode);
            using var issue = fixture.SignedRequest("primary", "/api/internal/bff/delegations",
                method: HttpMethod.Post,
                json: JsonSerializer.Serialize(delegationRequest));
            using var issuedResponse = await client.SendAsync(issue);
            var issued = await issuedResponse.Content.ReadFromJsonAsync<BffDelegationResponse>();
            Assert.AreEqual(HttpStatusCode.OK, issuedResponse.StatusCode);
            Assert.IsNotNull(issued);
            StringAssert.StartsWith(issued.AccessToken, "agd_");
            var tokenClaims = new JsonWebTokenHandler().ReadJsonWebToken(issued.AccessToken[4..]).Claims
                .Select(value => value.Type).ToHashSet(StringComparer.Ordinal);
            Assert.IsFalse(tokenClaims.Contains("agentstration_provider"));
            Assert.IsFalse(tokenClaims.Contains("agentstration_authentication_version"));
            Assert.IsFalse(tokenClaims.Contains("email"));
            Assert.IsFalse(tokenClaims.Contains("role"));
            Assert.IsFalse(tokenClaims.Contains("permission"));
            using var keyRequest = fixture.SignedRequest("primary", "/api/internal/bff/delegation-keys");
            using var keyResponse = await client.SendAsync(keyRequest);
            Assert.AreEqual(HttpStatusCode.OK, keyResponse.StatusCode);
            using var keyDocument = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync());
            var publishedKey = keyDocument.RootElement.GetProperty("keys")[0];
            Assert.IsTrue(publishedKey.TryGetProperty("n", out _));
            Assert.IsTrue(publishedKey.TryGetProperty("e", out _));
            Assert.IsFalse(publishedKey.TryGetProperty("d", out _));

            using var management = new HttpRequestMessage(HttpMethod.Get, "/api/identity/context");
            management.Headers.Authorization = new("Bearer", issued.AccessToken);
            using var managementResponse = await client.SendAsync(management);
            Assert.AreEqual(HttpStatusCode.OK, managementResponse.StatusCode);
            using var lowerCaseScheme = new HttpRequestMessage(HttpMethod.Get, "/api/identity/context");
            lowerCaseScheme.Headers.Authorization = new("bearer", issued.AccessToken);
            using var lowerCaseResponse = await client.SendAsync(lowerCaseScheme);
            Assert.AreEqual(HttpStatusCode.OK, lowerCaseResponse.StatusCode);

            using var wrongAudience = new HttpRequestMessage(HttpMethod.Get, "/api/work/workitems");
            wrongAudience.Headers.Authorization = new("Bearer", issued.AccessToken);
            using var wrongAudienceResponse = await client.SendAsync(wrongAudience);
            Assert.AreEqual(HttpStatusCode.Unauthorized, wrongAudienceResponse.StatusCode);

            var audienceRoutes = new[]
            {
                (InternalDelegationAudiences.Management, "/api/identity/context"),
                (InternalDelegationAudiences.Work, "/api/work/workitems"),
                (InternalDelegationAudiences.Flow, "/api/flows"),
                (InternalDelegationAudiences.Runtime, "/api/runtime/runs")
            };
            foreach (var (_, path) in audienceRoutes.Skip(2))
            {
                using var mismatch = new HttpRequestMessage(HttpMethod.Get, path);
                mismatch.Headers.Authorization = new("Bearer", issued.AccessToken);
                using var mismatchResponse = await client.SendAsync(mismatch);
                Assert.AreEqual(HttpStatusCode.Unauthorized, mismatchResponse.StatusCode, path);
            }
            foreach (var (audience, path) in audienceRoutes.Skip(1))
            {
                using var audienceIssue = fixture.SignedRequest("primary", "/api/internal/bff/delegations",
                    method: HttpMethod.Post,
                    json: JsonSerializer.Serialize(new BffDelegationRequest(
                        identity.PrincipalId, identity.TenantId, identity.WorkspaceId, sessionId,
                        identity.AuthenticationMethod, identity.Provider, identity.AuthenticationVersion, audience)));
                using var audienceIssueResponse = await client.SendAsync(audienceIssue);
                Assert.AreEqual(HttpStatusCode.OK, audienceIssueResponse.StatusCode, audience);
                var audienceToken = await audienceIssueResponse.Content.ReadFromJsonAsync<BffDelegationResponse>();
                Assert.IsNotNull(audienceToken);
                using var business = new HttpRequestMessage(HttpMethod.Get, path);
                business.Headers.Authorization = new("Bearer", audienceToken.AccessToken);
                using var businessResponse = await client.SendAsync(business);
                Assert.AreNotEqual(HttpStatusCode.Unauthorized, businessResponse.StatusCode, audience);
                foreach (var (otherAudience, otherPath) in audienceRoutes.Where(value => value.Item1 != audience))
                {
                    using var mismatch = new HttpRequestMessage(HttpMethod.Get, otherPath);
                    mismatch.Headers.Authorization = new("Bearer", audienceToken.AccessToken);
                    using var mismatchResponse = await client.SendAsync(mismatch);
                    Assert.AreEqual(HttpStatusCode.Unauthorized, mismatchResponse.StatusCode,
                        $"{audience} must not authorize {otherAudience}.");
                }
            }

            using (var scope = factory.Services.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IIdentityStore>();
                var principal = await store.GetPrincipalAsync(identity.PrincipalId, CancellationToken.None);
                Assert.IsNotNull(principal);
                await store.UpdatePrincipalAsync(principal with { Status = PrincipalStatus.Disabled }, CancellationToken.None);
            }
            using var disabled = new HttpRequestMessage(HttpMethod.Get, "/api/identity/context");
            disabled.Headers.Authorization = new("Bearer", issued.AccessToken);
            using var disabledResponse = await client.SendAsync(disabled);
            Assert.AreEqual(HttpStatusCode.Unauthorized, disabledResponse.StatusCode);

        }
        finally { fixture.Dispose(); }
    }

    [TestMethod]
    public async Task BffDelegationExpiresWithoutClockSkew()
    {
        var fixture = BffFixture.Create();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            await using var factory = fixture.Factory(timeProvider: time);
            using var client = UnredirectedClient(factory);
            await BootstrapAsync(client, "delegation-expiry-user");
            using var login = fixture.SignedRequest("primary", "/api/internal/bff/sessions/local",
                method: HttpMethod.Post,
                json: JsonSerializer.Serialize(new BffLocalSessionRequest("delegation-expiry-user", LocalPassword)));
            using var loginResponse = await client.SendAsync(login);
            var identity = await loginResponse.Content.ReadFromJsonAsync<BffSessionIdentityResponse>();
            Assert.AreEqual(HttpStatusCode.OK, loginResponse.StatusCode);
            Assert.IsNotNull(identity);
            var sessionId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using var issue = fixture.SignedRequest("primary", "/api/internal/bff/delegations",
                method: HttpMethod.Post,
                json: JsonSerializer.Serialize(new BffDelegationRequest(identity.PrincipalId, identity.TenantId,
                    identity.WorkspaceId, sessionId, identity.AuthenticationMethod, identity.Provider,
                    identity.AuthenticationVersion, InternalDelegationAudiences.Management)));
            using var issuedResponse = await client.SendAsync(issue);
            var issued = await issuedResponse.Content.ReadFromJsonAsync<BffDelegationResponse>();
            Assert.AreEqual(HttpStatusCode.OK, issuedResponse.StatusCode);
            Assert.IsNotNull(issued);
            using var before = new HttpRequestMessage(HttpMethod.Get, "/api/identity/context");
            before.Headers.Authorization = new("Bearer", issued.AccessToken);
            using var beforeResponse = await client.SendAsync(before);
            Assert.AreEqual(HttpStatusCode.OK, beforeResponse.StatusCode);

            time.Advance(TimeSpan.FromSeconds(121));
            using var after = new HttpRequestMessage(HttpMethod.Get, "/api/identity/context");
            after.Headers.Authorization = new("Bearer", issued.AccessToken);
            using var afterResponse = await client.SendAsync(after);
            Assert.AreEqual(HttpStatusCode.Unauthorized, afterResponse.StatusCode);
        }
        finally { fixture.Dispose(); }
    }

    [TestMethod]
    public async Task BffDelegationLosesAccessWhenWorkspaceMembershipIsRemoved()
    {
        var fixture = BffFixture.Create();
        try
        {
            await using var factory = fixture.Factory();
            using var administrator = UnredirectedClient(factory);
            await BootstrapAsync(administrator, "delegation-owner");
            var context = await administrator.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
            Assert.IsNotNull(context);
            var member = await CreateAccountAsync(administrator, context.Context.WorkspaceId,
                "delegation-member", BuiltInIdentityRoles.Viewer);
            using var login = fixture.SignedRequest("primary", "/api/internal/bff/sessions/local",
                method: HttpMethod.Post,
                json: JsonSerializer.Serialize(new BffLocalSessionRequest("delegation-member", LocalPassword)));
            using var loginResponse = await administrator.SendAsync(login);
            var identity = await loginResponse.Content.ReadFromJsonAsync<BffSessionIdentityResponse>();
            Assert.AreEqual(HttpStatusCode.OK, loginResponse.StatusCode);
            Assert.IsNotNull(identity);
            var sessionId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using var issue = fixture.SignedRequest("primary", "/api/internal/bff/delegations",
                method: HttpMethod.Post,
                json: JsonSerializer.Serialize(new BffDelegationRequest(identity.PrincipalId, identity.TenantId,
                    identity.WorkspaceId, sessionId, identity.AuthenticationMethod, identity.Provider,
                    identity.AuthenticationVersion, InternalDelegationAudiences.Management)));
            using var issuedResponse = await administrator.SendAsync(issue);
            var issued = await issuedResponse.Content.ReadFromJsonAsync<BffDelegationResponse>();
            Assert.AreEqual(HttpStatusCode.OK, issuedResponse.StatusCode);
            Assert.IsNotNull(issued);
            using var before = new HttpRequestMessage(HttpMethod.Get, "/api/identity/context");
            before.Headers.Authorization = new("Bearer", issued.AccessToken);
            using var beforeResponse = await administrator.SendAsync(before);
            Assert.AreEqual(HttpStatusCode.OK, beforeResponse.StatusCode);

            using var removal = await administrator.DeleteAsync(
                $"/api/identity/workspaces/{context.Context.WorkspaceId:D}/memberships/{member.PrincipalId:D}");
            Assert.AreEqual(HttpStatusCode.NoContent, removal.StatusCode);
            using var after = new HttpRequestMessage(HttpMethod.Get, "/api/identity/context");
            after.Headers.Authorization = new("Bearer", issued.AccessToken);
            using var afterResponse = await administrator.SendAsync(after);
            Assert.AreEqual(HttpStatusCode.Unauthorized, afterResponse.StatusCode);
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

        public WebApplicationFactory<Program> Factory(bool revokePrimary = false, TimeProvider? timeProvider = null) =>
            SecurityApiTests.Factory("Local").WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Agentstration:BffWorkloadTrust:Enabled", "true");
                builder.UseSetting("Agentstration:BffWorkloadTrust:InstanceId", "instance-a");
                Credential(builder, 0, "primary", Path.Combine(Directory, "primary.key"), revokePrimary);
                if (IncludeNext) Credential(builder, 1, "next", Path.Combine(Directory, "next.key"), false);
                if (timeProvider is not null)
                    builder.ConfigureTestServices(services => services.AddSingleton(timeProvider));
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

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset now = initial;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
