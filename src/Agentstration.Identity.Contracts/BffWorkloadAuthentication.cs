using System.Security.Cryptography;
using System.Text;

namespace Agentstration.Identity.Contracts;

public static class BffWorkloadAuthentication
{
    public const string WorkloadHeader = "X-Agentstration-Bff-Workload";
    public const string CredentialHeader = "X-Agentstration-Bff-Credential";
    public const string InstanceHeader = "X-Agentstration-Bff-Instance";
    public const string TimestampHeader = "X-Agentstration-Bff-Timestamp";
    public const string NonceHeader = "X-Agentstration-Bff-Nonce";
    public const string ContentHashHeader = "X-Agentstration-Bff-Content-Sha256";
    public const string SignatureHeader = "X-Agentstration-Bff-Signature";
    public const string WorkloadClaim = "agentstration:bff:workload";
    public const string CredentialClaim = "agentstration:bff:credential";

    public static string Canonicalize(
        string method,
        string pathAndQuery,
        string instanceId,
        long timestamp,
        string nonce,
        string contentHash) =>
        string.Join('\n', method.ToUpperInvariant(), pathAndQuery, instanceId, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture), nonce, contentHash);

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
