using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Security;

public static class PersonalAccessTokenAuthenticationDefaults
{
    public const string Scheme = "AgentstrationPersonalAccessToken";
}

public static class PersonalAccessTokenClaimTypes
{
    private const string Prefix = "agentstration:";
    public const string PrincipalId = Prefix + "principal_id";
    public const string TokenId = Prefix + "pat_id";
    public const string WorkspaceId = Prefix + "workspace_id";
    public const string Permission = Prefix + "permission";
}

public sealed class PersonalAccessTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IPersonalAccessTokenStore tokens,
    IIdentityStore identities,
    TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly TimeSpan LastUseWriteInterval = TimeSpan.FromMinutes(5);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var value = authorization["Bearer ".Length..].Trim();
        if (!TryParse(value, out var tokenId, out var secret))
            return AuthenticateResult.Fail("The personal access token is malformed.");

        var credential = await tokens.GetCredentialAsync(tokenId, Context.RequestAborted);
        var now = timeProvider.GetUtcNow();
        if (credential is null || credential.Token.RevokedAt is not null || credential.Token.ExpiresAt <= now)
            return AuthenticateResult.Fail("The personal access token is invalid.");
        var actualHash = PersonalAccessTokenService.HashSecret(secret);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, credential.SecretHash))
            return AuthenticateResult.Fail("The personal access token is invalid.");

        var token = credential.Token;
        var principal = await identities.GetPrincipalAsync(token.PrincipalId, Context.RequestAborted);
        var workspace = await identities.GetWorkspaceAsync(token.WorkspaceId, Context.RequestAborted);
        var membership = await identities.FindWorkspaceMembershipAsync(token.WorkspaceId, token.PrincipalId, Context.RequestAborted);
        if (principal is not { Status: PrincipalStatus.Active, Kind: PrincipalKind.Human }
            || workspace?.Status != WorkspaceStatus.Active
            || membership?.Status != MembershipStatus.Active)
            return AuthenticateResult.Fail("The personal access token is no longer authorized.");

        await tokens.RecordUseAsync(token.Id, now, LastUseWriteInterval, Context.RequestAborted);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, principal.Id.ToString("D")),
            new(ClaimTypes.Name, principal.DisplayName),
            new(PersonalAccessTokenClaimTypes.PrincipalId, principal.Id.ToString("D")),
            new(PersonalAccessTokenClaimTypes.TokenId, token.Id.ToString("D")),
            new(PersonalAccessTokenClaimTypes.WorkspaceId, token.WorkspaceId.ToString("D"))
        };
        claims.AddRange(token.Permissions.Select(permission => new Claim(PersonalAccessTokenClaimTypes.Permission, permission)));
        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private static bool TryParse(string value, out Guid tokenId, out string secret)
    {
        tokenId = Guid.Empty;
        secret = string.Empty;
        if (!value.StartsWith(PersonalAccessTokenService.TokenPrefix, StringComparison.Ordinal)) return false;
        var separator = value.IndexOf('_', PersonalAccessTokenService.TokenPrefix.Length);
        if (separator < 0) return false;
        var id = value[PersonalAccessTokenService.TokenPrefix.Length..separator];
        secret = value[(separator + 1)..];
        return id.Length == 32 && secret.Length >= 43 && Guid.TryParseExact(id, "N", out tokenId);
    }
}
