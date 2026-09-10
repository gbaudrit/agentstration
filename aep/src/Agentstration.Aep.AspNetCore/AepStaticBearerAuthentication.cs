using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Agentstration.Aep.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentstration.Aep.AspNetCore;

public static class AepAuthenticationDefaults
{
    public const string Scheme = "AepStaticBearer";
    public const string Policy = "AepWorkload";
    public const string PermissionClaim = "aep:permission";
    public const string ClientIdClaim = "aep:client_id";
    public const string InvokePermission = "aep.invoke";
}

public sealed class AepStaticBearerOptions : AuthenticationSchemeOptions
{
    internal IList<AepStaticBearerToken> Tokens { get; } = [];

    public void AddToken(string tokenId, string clientId, string token, params string[] permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (Encoding.UTF8.GetByteCount(token) < 32)
            throw new ArgumentException("An AEP static Bearer must contain at least 256 bits of entropy.", nameof(token));
        if (Tokens.Any(value => string.Equals(value.TokenId, tokenId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"AEP token id '{tokenId}' is registered more than once.");
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        try
        {
            Tokens.Add(new AepStaticBearerToken(
                tokenId,
                clientId,
                SHA256.HashData(tokenBytes),
                permissions.Length == 0 ? [AepAuthenticationDefaults.InvokePermission] : permissions.ToArray()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }
}

public sealed record AepIssuedStaticBearerCredential(string TokenId, string ClientId, string AccessToken)
{
    public override string ToString() => "***";
}

public static class AepStaticBearerCredentials
{
    public static AepIssuedStaticBearerCredential Generate(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var tokenId = Guid.NewGuid().ToString("N");
        var accessToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new(tokenId, clientId, accessToken);
    }
}

internal sealed record AepStaticBearerToken(
    string TokenId,
    string ClientId,
    byte[] Digest,
    IReadOnlyList<string> Permissions);

internal interface IAepDynamicCredentialStore
{
    AepDynamicCredentialMatch Match(ReadOnlySpan<byte> suppliedDigest, out string clientId);
}

internal enum AepDynamicCredentialMatch { None, Authenticated, Revoked }

internal sealed class AepStaticBearerAuthenticationHandler(
    IOptionsMonitor<AepStaticBearerOptions> options,
    IEnumerable<IAepDynamicCredentialStore> dynamicCredentials,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AepStaticBearerOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var suppliedToken = authorization[prefix.Length..].Trim();
        if (suppliedToken.Length == 0)
            return Task.FromResult(AuthenticateResult.Fail("Invalid AEP workload credential."));

        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
        var suppliedDigest = SHA256.HashData(suppliedBytes);
        CryptographicOperations.ZeroMemory(suppliedBytes);
        string? dynamicClientId = null;
        var dynamicMatch = AepDynamicCredentialMatch.None;
        foreach (var store in dynamicCredentials)
        {
            var result = store.Match(suppliedDigest, out var candidateClientId);
            if (result == AepDynamicCredentialMatch.Revoked)
            {
                dynamicMatch = result;
                break;
            }
            if (result == AepDynamicCredentialMatch.Authenticated && dynamicClientId is null)
                dynamicClientId = candidateClientId;
        }
        AepStaticBearerToken? matched = null;
        foreach (var candidate in Options.Tokens)
        {
            if (CryptographicOperations.FixedTimeEquals(suppliedDigest, candidate.Digest))
                matched = candidate;
        }
        CryptographicOperations.ZeroMemory(suppliedDigest);
        var isUnenrollmentRetry = dynamicMatch == AepDynamicCredentialMatch.Revoked
            && string.Equals(Request.Path.Value, AepEnrollmentProtocol.UnenrollmentPath, StringComparison.Ordinal);
        if (dynamicMatch == AepDynamicCredentialMatch.Revoked && !isUnenrollmentRetry
            || matched is null && dynamicClientId is null && !isUnenrollmentRetry)
            return Task.FromResult(AuthenticateResult.Fail("Invalid AEP workload credential."));

        var clientId = matched?.ClientId ?? dynamicClientId ?? "agentstration:unenrollment-retry";
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, clientId),
            new(AepAuthenticationDefaults.ClientIdClaim, clientId),
            new("aep:token_id", matched?.TokenId ?? "pairing-code")
        };
        claims.AddRange((matched?.Permissions ?? [AepAuthenticationDefaults.InvokePermission])
            .Select(value => new Claim(AepAuthenticationDefaults.PermissionClaim, value)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Response.WriteAsJsonAsync(new { error = new { code = "authentication_failed", message = "A valid AEP workload credential is required." } });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Response.WriteAsJsonAsync(new { error = new { code = "authorization_denied", message = "The AEP workload is not permitted to invoke this endpoint." } });
    }
}

internal sealed class AepAuthenticationMarker
{
}

public static class AepAuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddAepStaticBearerAuthentication(
        this IServiceCollection services,
        Action<AepStaticBearerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddSingleton<AepAuthenticationMarker>();
        services.AddAuthentication()
            .AddScheme<AepStaticBearerOptions, AepStaticBearerAuthenticationHandler>(AepAuthenticationDefaults.Scheme, configure);
        services.AddAuthorizationBuilder().AddPolicy(AepAuthenticationDefaults.Policy, policy =>
        {
            policy.AddAuthenticationSchemes(AepAuthenticationDefaults.Scheme);
            policy.RequireAuthenticatedUser();
            policy.RequireClaim(AepAuthenticationDefaults.PermissionClaim, AepAuthenticationDefaults.InvokePermission);
        });
        return services;
    }
}
