using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Agentstration.Identity.Contracts;

public static class AwpWorkerAuthentication
{
    public const string WorkerHeader = "X-Agentstration-Awp-Worker";
    public const string SessionHeader = "X-Agentstration-Awp-Session";
    public const string CredentialHeader = "X-Agentstration-Awp-Credential";
    public const string InstanceHeader = "X-Agentstration-Awp-Instance";
    public const string TimestampHeader = "X-Agentstration-Awp-Timestamp";
    public const string NonceHeader = "X-Agentstration-Awp-Nonce";
    public const string ContentHashHeader = "X-Agentstration-Awp-Content-Sha256";
    public const string SignatureHeader = "X-Agentstration-Awp-Signature";
    public const string WorkerClaim = "agentstration:awp:worker";
    public const string SessionClaim = "agentstration:awp:session";
    public const string CredentialClaim = "agentstration:awp:credential";
    public const string ProtocolClaim = "agentstration:awp:protocol";
    public const string ProtocolVersion = "1";

    public static string Canonicalize(
        string method,
        string pathAndQuery,
        string instanceId,
        Guid workerId,
        Guid sessionId,
        long timestamp,
        string nonce,
        string contentHash) =>
        string.Join('\n', method.ToUpperInvariant(), pathAndQuery, instanceId, workerId.ToString("D"),
            sessionId.ToString("D"), timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture), nonce, contentHash);

    public static string HashContent(ReadOnlySpan<byte> content) => Base64Url(SHA256.HashData(content));

    public static string Sign(ReadOnlySpan<byte> key, string canonicalRequest) =>
        Base64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonicalRequest)));

    public static bool Verify(ReadOnlySpan<byte> key, string canonicalRequest, string signature)
    {
        Span<byte> supplied = stackalloc byte[32];
        if (!TryDecode(signature, supplied, out var written) || written != supplied.Length) return false;
        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonicalRequest));
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    public static string CreateNonce() => Base64Url(RandomNumberGenerator.GetBytes(16));

    public static bool TryGetIdentity(ClaimsPrincipal principal, out AwpWorkerRequestIdentity identity)
    {
        identity = default;
        if (!Guid.TryParse(principal.FindFirst(WorkerClaim)?.Value, out var workerId) || workerId == Guid.Empty
            || !Guid.TryParse(principal.FindFirst(SessionClaim)?.Value, out var sessionId) || sessionId == Guid.Empty
            || !Guid.TryParse(principal.FindFirst(CredentialClaim)?.Value, out var credentialId) || credentialId == Guid.Empty
            || !string.Equals(principal.FindFirst(ProtocolClaim)?.Value, ProtocolVersion, StringComparison.Ordinal))
            return false;
        identity = new(workerId, sessionId, credentialId, ProtocolVersion);
        return true;
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string value, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.TryFromBase64String(normalized, destination, out bytesWritten);
    }
}

public readonly record struct AwpWorkerRequestIdentity(
    Guid WorkerId,
    Guid WorkerSessionId,
    Guid CredentialId,
    string ProtocolVersion);

public enum AwpWorkerEnrollmentState
{
    Pending,
    PairingCodeIssued,
    Active,
    Revoked
}

public sealed record AnnounceAwpWorkerRequest(Guid WorkerId, string DisplayName, string ProtocolVersion);
public sealed record AwpWorkerEnrollmentView(
    Guid WorkerId,
    string DisplayName,
    string ProtocolVersion,
    AwpWorkerEnrollmentState State,
    DateTimeOffset AnnouncedAt,
    DateTimeOffset? CredentialIssuedAt,
    Guid? ActiveSessionId = null,
    DateTimeOffset? ActiveSessionStartedAt = null);
public sealed record AwpWorkerPairingCode(Guid WorkerId, string Code, DateTimeOffset ExpiresAt);
public sealed record ClaimAwpWorkerRequest(Guid WorkerId, string Code);
public sealed record AwpWorkerCredential(Guid WorkerId, Guid CredentialId, string InstanceId, string Secret);
public sealed record AwpWorkerIdentityView(Guid WorkerId, Guid WorkerSessionId, string ProtocolVersion);

