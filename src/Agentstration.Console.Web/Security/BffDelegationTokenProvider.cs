using System.Collections.Concurrent;
using System.Security.Claims;
using Agentstration.Identity.Contracts;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace Agentstration.Console.Web.Security;

public sealed class BffDelegationTokenCache(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<CacheKey, BffDelegationResponse> entries = new();

    public bool TryGet(CacheKey key, out string token)
    {
        token = string.Empty;
        if (!entries.TryGetValue(key, out var entry)) return false;
        if (entry.ExpiresAt <= timeProvider.GetUtcNow().AddSeconds(15))
        {
            entries.TryRemove(key, out _);
            return false;
        }
        token = entry.AccessToken;
        return true;
    }

    public void Put(CacheKey key, BffDelegationResponse token)
    {
        if (entries.Count >= 10_000)
        {
            foreach (var entry in entries.Where(value => value.Value.ExpiresAt <= timeProvider.GetUtcNow()))
                entries.TryRemove(entry.Key, out _);
            if (entries.Count >= 10_000 && entries.Keys.FirstOrDefault() is { } oldest)
                entries.TryRemove(oldest, out _);
        }
        entries[key] = token;
    }

    public void RevokeSession(string sessionId)
    {
        foreach (var key in entries.Keys.Where(value => value.SessionId == sessionId))
            entries.TryRemove(key, out _);
    }

    public sealed record CacheKey(
        string SessionId, Guid PrincipalId, Guid TenantId, Guid WorkspaceId, string Audience);
}

public sealed class BffDelegationTokenProvider(
    IHttpContextAccessor httpContextAccessor,
    AuthenticationStateProvider authenticationState,
    IBffServerSessionStore sessions,
    IBffSessionAuthorityClient authority,
    IBffDelegationClient delegations,
    BffDelegationTokenCache cache,
    IOptions<BffWorkloadClientOptions> workloadOptions)
{
    public async Task<string?> GetAsync(string audience, CancellationToken cancellationToken)
    {
        if (!InternalDelegationAudiences.IsKnown(audience) || !workloadOptions.Value.Enabled) return null;
        var principal = httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true
            ? httpContextAccessor.HttpContext.User
            : (await authenticationState.GetAuthenticationStateAsync()).User;
        if (principal.Identity?.IsAuthenticated != true) return null;
        var sessionId = principal.FindFirst(BffSessionClaims.SessionId)?.Value;
        var validationRequest = BffSessionClaims.ValidationRequest(principal);
        if (string.IsNullOrWhiteSpace(sessionId) || validationRequest is null
            || validationRequest.TenantId is not { } tenantId
            || validationRequest.WorkspaceId is not { } workspaceId
            || !await sessions.IsActiveSessionAsync(sessionId, cancellationToken))
            return null;

        BffSessionValidationResponse validation;
        try { validation = await authority.ValidateAsync(validationRequest, cancellationToken); }
        catch (HttpRequestException) { cache.RevokeSession(sessionId); return null; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { cache.RevokeSession(sessionId); return null; }
        if (!validation.Active || validation.Identity is not { } identity
            || identity.PrincipalId != validationRequest.PrincipalId
            || identity.TenantId != tenantId || identity.WorkspaceId != workspaceId)
        {
            cache.RevokeSession(sessionId);
            return null;
        }

        var key = new BffDelegationTokenCache.CacheKey(sessionId, identity.PrincipalId,
            identity.TenantId, identity.WorkspaceId, audience);
        if (cache.TryGet(key, out var token)) return token;
        BffDelegationResponse? issued;
        try
        {
            issued = await delegations.IssueAsync(new(
                identity.PrincipalId,
                identity.TenantId,
                identity.WorkspaceId,
                sessionId,
                identity.AuthenticationMethod,
                identity.Provider,
                identity.AuthenticationVersion,
                audience), cancellationToken);
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        if (issued is null) return null;
        cache.Put(key, issued);
        return issued.AccessToken;
    }
}
