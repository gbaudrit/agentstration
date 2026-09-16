using System.Security.Claims;
using Agentstration.Identity.Contracts;

namespace Agentstration.Console.Web.Security;

public static class BffSessionClaims
{
    public const string PrincipalId = "agentstration:bff:principal_id";
    public const string TenantId = "agentstration:bff:tenant_id";
    public const string WorkspaceId = "agentstration:bff:workspace_id";
    public const string AuthenticationMethod = "agentstration:bff:authentication_method";
    public const string Provider = "agentstration:bff:provider";
    public const string AuthenticationVersion = "agentstration:bff:authentication_version";

    public static ClaimsPrincipal Create(BffSessionIdentityResponse identity)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, identity.PrincipalId.ToString("D")),
            new Claim(ClaimTypes.Name, identity.DisplayName),
            new Claim(PrincipalId, identity.PrincipalId.ToString("D")),
            new Claim(TenantId, identity.TenantId.ToString("D")),
            new Claim(WorkspaceId, identity.WorkspaceId.ToString("D")),
            new Claim(AuthenticationMethod, identity.AuthenticationMethod),
            new Claim(Provider, identity.Provider),
            new Claim(AuthenticationVersion, identity.AuthenticationVersion)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, ConsoleAuthenticationDefaults.Scheme));
    }

    public static BffSessionValidationRequest? ValidationRequest(ClaimsPrincipal principal)
    {
        if (!Guid.TryParse(principal.FindFirst(PrincipalId)?.Value, out var principalId)) return null;
        var tenantId = Guid.TryParse(principal.FindFirst(TenantId)?.Value, out var tenant) ? tenant : (Guid?)null;
        var workspaceId = Guid.TryParse(principal.FindFirst(WorkspaceId)?.Value, out var workspace) ? workspace : (Guid?)null;
        return new(
            principalId,
            tenantId,
            workspaceId,
            principal.FindFirst(AuthenticationMethod)?.Value ?? string.Empty,
            principal.FindFirst(Provider)?.Value ?? string.Empty,
            principal.FindFirst(AuthenticationVersion)?.Value ?? string.Empty);
    }
}
