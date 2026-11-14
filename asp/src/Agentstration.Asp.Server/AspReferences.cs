using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Asp.Abstractions;

namespace Agentstration.Asp.Server;

// Signing key lifetime is the server lifetime. Restart invalidates outstanding references.
internal sealed class AspReferences(TimeProvider clock)
{
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private sealed record Token(string Kind, string Scope, string Key, long Expires, string? Version = null, string? ETag = null);

    public string Seal(string kind, AspAccessContext context, string key, string? version = null, string? query = null, string? etag = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new Token(kind, Scope(context, query), key,
            clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds(), version, etag), AspProtocol.JsonOptions);
        return Convert.ToBase64String(body) + "." + Convert.ToBase64String(HMACSHA256.HashData(secret, body));
    }

    public (string Key, string? Version, string? ETag) Open(string token, string kind, AspAccessContext context, string? query = null)
    {
        try
        {
            AspValidation.Require(!string.IsNullOrEmpty(token) && token.Length <= 4096, "invalid_reference", "Invalid reference.");
            var parts = token.Split('.');
            AspValidation.Require(parts.Length == 2, "invalid_reference", "Invalid reference.");
            var body = Convert.FromBase64String(parts[0]);
            var signature = Convert.FromBase64String(parts[1]);
            AspValidation.Require(CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(secret, body)),
                "invalid_reference", "Invalid reference.");
            var value = JsonSerializer.Deserialize<Token>(body, AspProtocol.JsonOptions)!;
            AspValidation.Require(value.Kind == kind && value.Scope == Scope(context, query)
                && value.Expires > clock.GetUtcNow().ToUnixTimeSeconds(), "invalid_reference", "Reference scope or lifetime does not match.");
            AspValidation.DemandKey(context, value.Key);
            return (value.Key, value.Version, value.ETag);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or NullReferenceException)
        { throw new AspException("invalid_reference", "Invalid reference."); }
    }

    private static string Scope(AspAccessContext context, string? query) =>
        AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new { context, query }, AspProtocol.JsonOptions));
}
