namespace Agentstration.Identity.Contracts;

public static class BffSessionAuthenticationMethods
{
    public const string Local = "local";
    public const string External = "external";
}

public sealed record BffLocalSessionRequest(string UserName, string Password);

public sealed record BffSessionValidationRequest(
    Guid PrincipalId,
    Guid? TenantId = null,
    Guid? WorkspaceId = null,
    string AuthenticationMethod = BffSessionAuthenticationMethods.Local,
    string Provider = "local",
    string AuthenticationVersion = "");

public sealed record BffSessionIdentityResponse(
    Guid PrincipalId,
    string DisplayName,
    string AuthenticationMethod,
    string Provider,
    Guid TenantId,
    Guid WorkspaceId,
    string AuthenticationVersion = "");

public sealed record BffSessionValidationResponse(
    bool Active,
    BffSessionIdentityResponse? Identity,
    string? ReasonCode = null);
