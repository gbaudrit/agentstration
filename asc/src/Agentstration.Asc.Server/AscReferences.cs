using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Asc.Abstractions;

namespace Agentstration.Asc.Server;

// Signing key lifetime is the server lifetime. Restart invalidates outstanding references.
internal sealed class AscReferences(TimeProvider clock)
{
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private sealed record Token(string Kind, string Scope, string Key, long Expires, string? Version = null, string? ETag = null);

    public string Seal(string kind, AscAccessContext context, string key, string? version = null, string? query = null, string? etag = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new Token(kind, Scope(context, query), key,
            clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds(), version, etag), AscContract.JsonOptions);
        return Convert.ToBase64String(body) + "." + Convert.ToBase64String(HMACSHA256.HashData(secret, body));
    }

    public (string Key, string? Version, string? ETag) Open(string token, string kind, AscAccessContext context, string? query = null)
    {
        try
        {
            AscValidation.Require(!string.IsNullOrEmpty(token) && token.Length <= 4096, "invalid_reference", "Invalid reference.");
            var parts = token.Split('.');
            AscValidation.Require(parts.Length == 2, "invalid_reference", "Invalid reference.");
            var body = Convert.FromBase64String(parts[0]);
            var signature = Convert.FromBase64String(parts[1]);
            AscValidation.Require(CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(secret, body)),
                "invalid_reference", "Invalid reference.");
            var value = JsonSerializer.Deserialize<Token>(body, AscContract.JsonOptions)!;
            AscValidation.Require(value.Kind == kind && value.Scope == Scope(context, query)
                && value.Expires > clock.GetUtcNow().ToUnixTimeSeconds(), "invalid_reference", "Reference scope or lifetime does not match.");
            AscValidation.DemandKey(context, value.Key);
            return (value.Key, value.Version, value.ETag);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or NullReferenceException)
        { throw new AscException("invalid_reference", "Invalid reference."); }
    }

    private static string Scope(AscAccessContext context, string? query) =>
        AscValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new { context, query }, AscContract.JsonOptions));
}
