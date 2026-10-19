using System.Net;
using Agentstration.Console.Web.Security;
using Agentstration.Identity.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Agentstration.Console.Web.Tests;

[TestClass]
public sealed class BffDelegationTests
{
    [TestMethod]
    public void CachedDelegationExpiresBeforeTheSignedToken()
    {
        var time = new ManualTimeProvider();
        var cache = new BffDelegationTokenCache(time);
        var key = new BffDelegationTokenCache.CacheKey("session", Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), InternalDelegationAudiences.Management);
        cache.Put(key, new BffDelegationResponse("agd_test", time.GetUtcNow().AddSeconds(20)));
        Assert.IsTrue(cache.TryGet(key, out _));
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.IsFalse(cache.TryGet(key, out _));
    }

    [TestMethod]
    public async Task TokensAreScopedToAnActiveSessionContextAndAudience()
    {
        var identity = new BffSessionIdentityResponse(
            Guid.NewGuid(), "Test user", BffSessionAuthenticationMethods.Local, "local", Guid.NewGuid(), Guid.NewGuid(), "version");
        var principal = BffSessionClaims.Create(identity);
        var store = new InMemoryBffSessionStore(Options.Create(new BffSessionOptions()), TimeProvider.System);
        var key = await store.StoreAsync(new AuthenticationTicket(principal, ConsoleAuthenticationDefaults.Scheme));
        var authority = new StubAuthority(identity);
        var issuer = new StubIssuer();
        var cache = new BffDelegationTokenCache(TimeProvider.System);
        var provider = new BffDelegationTokenProvider(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } },
            new StubAuthenticationStateProvider(principal), store, authority, issuer, cache,
            Options.Create(new BffWorkloadClientOptions { Enabled = true }));

        var management = await provider.GetAsync(InternalDelegationAudiences.Management, CancellationToken.None);
        var cached = await provider.GetAsync(InternalDelegationAudiences.Management, CancellationToken.None);
        var work = await provider.GetAsync(InternalDelegationAudiences.Work, CancellationToken.None);
        Assert.AreEqual("agd_1", management);
        Assert.AreEqual(management, cached);
        Assert.AreEqual("agd_2", work);
        Assert.AreEqual(2, issuer.Requests.Count);
        Assert.AreEqual(identity.PrincipalId, issuer.Requests[0].PrincipalId);
        Assert.AreEqual(identity.WorkspaceId, issuer.Requests[0].WorkspaceId);
        Assert.AreEqual(principal.FindFirst(BffSessionClaims.SessionId)?.Value, issuer.Requests[0].SessionId);
        Assert.AreEqual(3, authority.Validations);

        authority.Active = false;
        Assert.IsNull(await provider.GetAsync(InternalDelegationAudiences.Management, CancellationToken.None));
        authority.Active = true;
        Assert.AreEqual("agd_3", await provider.GetAsync(InternalDelegationAudiences.Management, CancellationToken.None));

        await store.RemoveAsync(key);
        Assert.IsNull(await provider.GetAsync(InternalDelegationAudiences.Management, CancellationToken.None));
        Assert.AreEqual(3, issuer.Requests.Count);
    }

    [TestMethod]
    public async Task DelegationIsSentOnlyToTheConfiguredOrigin()
    {
        var identity = new BffSessionIdentityResponse(
            Guid.NewGuid(), "Test user", BffSessionAuthenticationMethods.Local, "local", Guid.NewGuid(), Guid.NewGuid(), "version");
        var principal = BffSessionClaims.Create(identity);
        var store = new InMemoryBffSessionStore(Options.Create(new BffSessionOptions()), TimeProvider.System);
        await store.StoreAsync(new AuthenticationTicket(principal, ConsoleAuthenticationDefaults.Scheme));
        var issuer = new StubIssuer();
        var provider = new BffDelegationTokenProvider(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } },
            new StubAuthenticationStateProvider(principal), store, new StubAuthority(identity), issuer,
            new BffDelegationTokenCache(TimeProvider.System),
            Options.Create(new BffWorkloadClientOptions { Enabled = true }));
        var downstream = new CaptureHandler();
        using var client = new HttpClient(new BffDelegationHandler(
            provider, InternalDelegationAudiences.Management, new Uri("https://api.example.test/"))
        {
            InnerHandler = downstream
        });

        using var allowed = await client.GetAsync("https://api.example.test/api/identity/context");
        using var denied = await client.GetAsync("https://other.example.test/api/identity/context");
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        Assert.AreEqual("agd_1", downstream.Token);
        Assert.IsNull(client.DefaultRequestHeaders.Authorization);
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.AreEqual(1, downstream.Calls);
    }

    private sealed class StubAuthority(BffSessionIdentityResponse identity) : IBffSessionAuthorityClient
    {
        public int Validations { get; private set; }
        public bool Active { get; set; } = true;
        public Task<BffLocalAuthenticationResult> AuthenticateLocalAsync(string userName, string password,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BffSessionValidationResponse> ValidateAsync(BffSessionValidationRequest request,
            CancellationToken cancellationToken)
        {
            Validations++;
            return Task.FromResult(new BffSessionValidationResponse(Active, Active ? identity : null));
        }
    }

    private sealed class StubIssuer : IBffDelegationClient
    {
        public List<BffDelegationRequest> Requests { get; } = [];
        public Task<BffDelegationResponse?> IssueAsync(BffDelegationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult<BffDelegationResponse?>(new(
                $"agd_{Requests.Count}", DateTimeOffset.UtcNow.AddMinutes(2)));
        }
    }

    private sealed class StubAuthenticationStateProvider(System.Security.Claims.ClaimsPrincipal principal)
        : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Token { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Token = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-01-01T00:00:00Z",
            global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
