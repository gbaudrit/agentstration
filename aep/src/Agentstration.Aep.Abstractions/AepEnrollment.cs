namespace Agentstration.Aep.Abstractions;

public static class AepEnrollmentProtocol
{
    public const string AnnouncementPath = "/api/aep/enrollments/announce";
    public const string ClaimPath = "/api/aep/enrollments/claim";
    public const string ReadyPath = "/api/aep/enrollments/ready";
    public const string PairingPath = "/aep/enrollment/pair";
}

public sealed record AepEnrollmentAnnouncement(
    Guid InstanceId,
    Guid TenantId,
    Guid WorkspaceId,
    AepExtensionIdentity Extension,
    Uri Endpoint,
    Uri PairingUri);

public sealed record AepEnrollmentAnnouncementResponse(Guid RequestId, string State);
public sealed record AepEnrollmentClaim(Guid RequestId, Guid InstanceId, Guid WorkspaceId, string Code);
public sealed record AepEnrollmentCredential(string ClientId, string AccessToken, string CompletionToken);
public sealed record AepEnrollmentReady(Guid RequestId, Guid InstanceId, Guid WorkspaceId, string CompletionToken);
public sealed record AepEnrollmentReadyResponse(string State);
public sealed record AepEnrollmentError(string Code, string Message);
