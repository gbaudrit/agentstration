using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Aep.Abstractions;

public static class AepEnrollmentProtocol
{
    public const string AnnouncementPath = "/api/aep/enrollments/announce";
    public const string ClaimPath = "/api/aep/enrollments/claim";
    public const string ReadyPath = "/api/aep/enrollments/ready";
    public const string PairingPath = "/aep/enrollment/pair";
    public const string CredentialRotationPath = "/aep/enrollment/credentials/rotate";
    public const string PreviousCredentialRevocationPath = "/aep/enrollment/credentials/revoke-previous";
    public const string CredentialRevocationPath = "/aep/enrollment/credentials/revoke";
    public const string UnenrollmentPath = "/aep/enrollment/unenroll";
}

[JsonConverter(typeof(JsonStringEnumConverter<AepEnrollmentMethod>))]
public enum AepEnrollmentMethod { PairingCode, SharedKeyFile }

public sealed record AepEnrollmentAnnouncement(
    Guid InstanceId,
    AepExtensionIdentity Extension,
    Uri Endpoint,
    Uri? PairingUri,
    AepEnrollmentMethod Method = AepEnrollmentMethod.PairingCode);
public sealed record AepEnrollmentProof(long Timestamp, string Signature);

public static class AepEnrollmentProofs
{
    public static string Sign(AepEnrollmentAnnouncement announcement, long timestamp, string sharedKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedKey);
        var key = Encoding.UTF8.GetBytes(sharedKey);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ProofPayload(timestamp, announcement), AepProtocol.JsonOptions);
        try
        {
            var digest = HMACSHA256.HashData(key, payload);
            try { return Convert.ToBase64String(digest); }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static bool Verify(AepEnrollmentAnnouncement announcement, AepEnrollmentProof proof, string sharedKey)
    {
        if (proof.Signature.Length is < 40 or > 128) return false;
        try
        {
            var expected = Convert.FromBase64String(Sign(announcement, proof.Timestamp, sharedKey));
            var supplied = Convert.FromBase64String(proof.Signature);
            try { return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied); }
            finally { CryptographicOperations.ZeroMemory(expected); CryptographicOperations.ZeroMemory(supplied); }
        }
        catch (FormatException) { return false; }
    }

    private sealed record ProofPayload(long Timestamp, AepEnrollmentAnnouncement Announcement);
}

public sealed record AepEnrollmentAnnouncementResponse(Guid RequestId, string State);
public sealed record AepEnrollmentClaim(Guid RequestId, Guid InstanceId, string Code);
public sealed record AepEnrollmentCredential(string ClientId, string AccessToken, string CompletionToken);
public sealed record AepEnrollmentReady(Guid RequestId, Guid InstanceId, string CompletionToken);
public sealed record AepEnrollmentReadyResponse(string State);
public sealed record AepEnrollmentError(string Code, string Message);
public sealed record AepCredentialRotation(Guid InstanceId, string ClientId, string AccessToken);
public sealed record AepCredentialLifecycleResponse(string State);
