using System.Security.Claims;
using System.Security.Cryptography;
using Agentstration.Identity.Contracts;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agentstration.Identity.Api.Security;

public static class InternalDelegationDefaults
{
    public const string Scheme = "Agentstration.InternalDelegation";
    public const string Issuer = "agentstration-internal-delegation";
    public const string TokenPrefix = "agd_";
    public const string TenantClaim = "agentstration_tenant";
    public const string WorkspaceClaim = "agentstration_workspace";
    public const string SessionClaim = "agentstration_session";
    public const string MethodClaim = "agentstration_method";
}

public sealed class InternalDelegationService(
    BffSessionAuthorityService authority,
    InternalDelegationKeys keys,
    Microsoft.Extensions.Options.IOptions<InternalDelegationOptions> options,
    TimeProvider timeProvider)
{
    public async Task<BffDelegationResponse?> IssueAsync(
        BffDelegationRequest request,
        CancellationToken cancellationToken)
    {
        if (!InternalDelegationAudiences.IsKnown(request.Audience)
            || request.PrincipalId == Guid.Empty || request.TenantId == Guid.Empty || request.WorkspaceId == Guid.Empty
            || request.SessionId.Length != 43 || request.SessionId.Any(value =>
                !char.IsAsciiLetterOrDigit(value) && value != '-' && value != '_'))
            return null;

        var validation = await authority.ValidateAsync(new(
            request.PrincipalId,
            request.TenantId,
            request.WorkspaceId,
            request.AuthenticationMethod,
            request.Provider,
            request.AuthenticationVersion), cancellationToken);
        if (!validation.Active || validation.Identity is not { } identity
            || identity.PrincipalId != request.PrincipalId
            || identity.TenantId != request.TenantId
            || identity.WorkspaceId != request.WorkspaceId)
            return null;

        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddSeconds(options.Value.LifetimeSeconds);
        var claims = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, identity.PrincipalId.ToString("D")),
            new Claim(InternalDelegationDefaults.TenantClaim, identity.TenantId.ToString("D")),
            new Claim(InternalDelegationDefaults.WorkspaceClaim, identity.WorkspaceId.ToString("D")),
            new Claim(InternalDelegationDefaults.SessionClaim, request.SessionId),
            new Claim(InternalDelegationDefaults.MethodClaim, identity.AuthenticationMethod),
            new Claim(JwtRegisteredClaimNames.Jti, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)))
        });
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = InternalDelegationDefaults.Issuer,
            Audience = request.Audience,
            Subject = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(keys.SigningKey, SecurityAlgorithms.RsaSha256)
        };
        return new(InternalDelegationDefaults.TokenPrefix + new JsonWebTokenHandler().CreateToken(descriptor), expiresAt);
    }
}
