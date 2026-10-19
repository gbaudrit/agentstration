namespace Agentstration.Identity.Contracts;

public static class InternalDelegationAudiences
{
    public const string Management = "agentstration-management";
    public const string Work = "agentstration-work";
    public const string Flow = "agentstration-flow";
    public const string Runtime = "agentstration-runtime";

    public static bool IsKnown(string value) => value is Management or Work or Flow or Runtime;
}

public sealed record BffDelegationRequest(
    Guid PrincipalId,
    Guid TenantId,
    Guid WorkspaceId,
    string SessionId,
    string AuthenticationMethod,
    string Provider,
    string AuthenticationVersion,
    string Audience);

public sealed record BffDelegationResponse(string AccessToken, DateTimeOffset ExpiresAt);
