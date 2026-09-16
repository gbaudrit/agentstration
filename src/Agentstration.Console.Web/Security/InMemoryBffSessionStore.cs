using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Agentstration.Console.Web.Security;

public sealed record BffServerSession(
    string Key,
    Guid PrincipalId,
    Guid TenantId,
    Guid WorkspaceId,
    string AuthenticationMethod,
    string Provider,
    string AuthenticationVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset AbsoluteExpiresAt);

public interface IBffServerSessionStore : ITicketStore
{
    Task<BffServerSession?> FindAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class InMemoryBffSessionStore(
    IOptions<BffSessionOptions> options,
    TimeProvider timeProvider) : IBffServerSessionStore
{
    private readonly ConcurrentDictionary<string, StoredSession> sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim storeGate = new(1, 1);

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var now = timeProvider.GetUtcNow();
        await storeGate.WaitAsync();
        try
        {
            PruneExpired(now);
            if (sessions.Count >= options.Value.MaximumSessions)
            {
                var oldest = sessions.MinBy(value => value.Value.Session.LastSeenAt);
                sessions.TryRemove(oldest.Key, out _);
            }
            var key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var absoluteExpiresAt = ticket.Properties.ExpiresUtc
                ?? now.AddHours(options.Value.AbsoluteLifetimeHours);
            sessions[key] = new(ticket, Session(ticket, key, now, now, absoluteExpiresAt));
            return key;
        }
        finally
        {
            storeGate.Release();
        }
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        await storeGate.WaitAsync();
        try
        {
            var now = timeProvider.GetUtcNow();
            if (!sessions.TryGetValue(key, out var existing)) return;
            if (IsExpired(existing.Session, now))
            {
                sessions.TryRemove(key, out _);
                return;
            }
            sessions[key] = new(ticket, Session(ticket, key, existing.Session.CreatedAt,
                now, existing.Session.AbsoluteExpiresAt));
        }
        finally
        {
            storeGate.Release();
        }
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        await storeGate.WaitAsync();
        try
        {
            if (!sessions.TryGetValue(key, out var stored)) return null;
            var now = timeProvider.GetUtcNow();
            if (IsExpired(stored.Session, now))
            {
                sessions.TryRemove(key, out _);
                return null;
            }
            sessions[key] = stored with { Session = stored.Session with { LastSeenAt = now } };
            return stored.Ticket;
        }
        finally
        {
            storeGate.Release();
        }
    }

    public async Task RemoveAsync(string key)
    {
        await storeGate.WaitAsync();
        try
        {
            sessions.TryRemove(key, out _);
        }
        finally
        {
            storeGate.Release();
        }
    }

    public async Task<BffServerSession?> FindAsync(string key, CancellationToken cancellationToken = default)
    {
        await storeGate.WaitAsync(cancellationToken);
        try
        {
            if (!sessions.TryGetValue(key, out var value)) return null;
            if (!IsExpired(value.Session, timeProvider.GetUtcNow())) return value.Session;
            sessions.TryRemove(key, out _);
            return null;
        }
        finally
        {
            storeGate.Release();
        }
    }

    private static BffServerSession Session(
        AuthenticationTicket ticket,
        string key,
        DateTimeOffset createdAt,
        DateTimeOffset lastSeenAt,
        DateTimeOffset absoluteExpiresAt)
    {
        var validation = BffSessionClaims.ValidationRequest(ticket.Principal)
            ?? throw new InvalidOperationException("A BFF session requires a valid Principal identifier.");
        if (validation.TenantId is not { } tenantId || validation.WorkspaceId is not { } workspaceId)
            throw new InvalidOperationException("A BFF session requires a selected Tenant and Workspace.");
        return new(
            key,
            validation.PrincipalId,
            tenantId,
            workspaceId,
            ticket.Principal.FindFirst(BffSessionClaims.AuthenticationMethod)?.Value ?? string.Empty,
            ticket.Principal.FindFirst(BffSessionClaims.Provider)?.Value ?? string.Empty,
            ticket.Principal.FindFirst(BffSessionClaims.AuthenticationVersion)?.Value ?? string.Empty,
            createdAt,
            lastSeenAt,
            absoluteExpiresAt);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var value in sessions)
        {
            if (IsExpired(value.Value.Session, now))
                sessions.TryRemove(value.Key, out _);
        }
    }

    private bool IsExpired(BffServerSession session, DateTimeOffset now) =>
        now >= session.AbsoluteExpiresAt ||
        now >= session.LastSeenAt.AddMinutes(options.Value.IdleTimeoutMinutes);

    private sealed record StoredSession(AuthenticationTicket Ticket, BffServerSession Session);
}
