using Agentstration.Console.Web.Security;
using Agentstration.Identity.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agentstration.Console.Web.Tests;

[TestClass]
public sealed class InMemoryBffSessionStoreTests
{
    [TestMethod]
    public async Task OpaqueSessionExpiresAfterIdleTimeout()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 9, 15, 20, 0, 0, TimeSpan.Zero));
        var store = new InMemoryBffSessionStore(Options.Create(new BffSessionOptions
        {
            IdleTimeoutMinutes = 5,
            AbsoluteLifetimeHours = 1
        }), time);
        var identity = new BffSessionIdentityResponse(
            Guid.NewGuid(), "Test user", BffSessionAuthenticationMethods.Local, "local", Guid.NewGuid(), Guid.NewGuid());
        var ticket = new AuthenticationTicket(
            BffSessionClaims.Create(identity),
            new AuthenticationProperties { ExpiresUtc = time.GetUtcNow().AddHours(1) },
            ConsoleAuthenticationDefaults.Scheme);

        var key = await store.StoreAsync(ticket);
        Assert.IsNotNull(await store.RetrieveAsync(key));
        var session = await store.FindAsync(key);
        Assert.IsNotNull(session);
        Assert.AreEqual(identity.PrincipalId, session.PrincipalId);
        Assert.IsFalse(key.Contains(identity.PrincipalId.ToString("D"), StringComparison.OrdinalIgnoreCase));

        time.Advance(TimeSpan.FromMinutes(6));
        Assert.IsNull(await store.RetrieveAsync(key));
        Assert.IsNull(await store.FindAsync(key));
    }

    [TestMethod]
    public async Task RemovalInvalidatesAReplayedSessionKey()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBffSessionStore(Options.Create(new BffSessionOptions()), time);
        var identity = new BffSessionIdentityResponse(
            Guid.NewGuid(), "Test user", BffSessionAuthenticationMethods.Local, "local", Guid.NewGuid(), Guid.NewGuid());
        var key = await store.StoreAsync(new AuthenticationTicket(
            BffSessionClaims.Create(identity),
            ConsoleAuthenticationDefaults.Scheme));

        await store.RemoveAsync(key);

        Assert.IsNull(await store.RetrieveAsync(key));
    }

    [TestMethod]
    public async Task AbsoluteExpiryCannotBeExtendedByActivity()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 9, 15, 20, 0, 0, TimeSpan.Zero));
        var store = new InMemoryBffSessionStore(Options.Create(new BffSessionOptions
        {
            IdleTimeoutMinutes = 30,
            AbsoluteLifetimeHours = 1
        }), time);
        var identity = new BffSessionIdentityResponse(
            Guid.NewGuid(), "Test user", BffSessionAuthenticationMethods.Local, "local", Guid.NewGuid(), Guid.NewGuid());
        var key = await store.StoreAsync(new AuthenticationTicket(
            BffSessionClaims.Create(identity),
            new AuthenticationProperties { ExpiresUtc = time.GetUtcNow().AddHours(1) },
            ConsoleAuthenticationDefaults.Scheme));

        for (var index = 0; index < 3; index++)
        {
            time.Advance(TimeSpan.FromMinutes(20));
            if (index < 2) Assert.IsNotNull(await store.RetrieveAsync(key));
        }

        Assert.IsNull(await store.RetrieveAsync(key));
    }

    [TestMethod]
    public async Task ConcurrentSessionsCanBeRevokedIndependently()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBffSessionStore(Options.Create(new BffSessionOptions()), time);
        var identity = new BffSessionIdentityResponse(
            Guid.NewGuid(), "Test user", BffSessionAuthenticationMethods.Local, "local", Guid.NewGuid(), Guid.NewGuid());
        var ticket = new AuthenticationTicket(BffSessionClaims.Create(identity), ConsoleAuthenticationDefaults.Scheme);

        var keys = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.StoreAsync(ticket)));
        Assert.AreEqual(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        await store.RemoveAsync(keys[0]);

        Assert.IsNull(await store.RetrieveAsync(keys[0]));
        var remaining = await Task.WhenAll(keys[1..].Select(store.RetrieveAsync));
        Assert.IsTrue(remaining.All(value => value is not null));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
