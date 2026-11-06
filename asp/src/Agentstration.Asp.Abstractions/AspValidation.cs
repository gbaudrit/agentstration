using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Agentstration.Asp.Abstractions;

public static class AspValidation
{
    public static void Require([DoesNotReturnIf(false)] bool condition, string code, string message)
    {
        if (!condition) throw new AspException(code, message);
    }

    public static void RequestId(string value) => Require(
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl),
        "invalid_request", "A bounded request identity is required.");

    public static void Policy(AspAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Prefix(policy.Prefix);
        Require(policy.MaxReadBytes is > 0 and <= AspProtocol.MaxContentBytes
            && policy.MaxWriteBytes is > 0 and <= AspProtocol.MaxContentBytes
            && policy.MaxPageSize is > 0 and <= AspProtocol.MaxPageSize,
            "invalid_policy", "Policy limits exceed the content profile.");
        if (policy.KeyFrom is not null) Key(policy.KeyFrom);
        if (policy.KeyBefore is not null) Key(policy.KeyBefore);
        Require(policy.KeyFrom is null || policy.KeyBefore is null
            || string.CompareOrdinal(policy.KeyFrom, policy.KeyBefore) < 0, "invalid_policy", "Invalid key range.");
    }

    public static void Store(AspStoreRef store)
    {
        Require(store is not null, "invalid_request", "A logical storage target is required.");
        RequestId(store.StoreId);
        RequestId(store.BindingId);
        RequestId(store.Revision);
    }

    public static void Context(AspAccessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Store(context.Store);
        RequestId(context.Subject);
        RequestId(context.AuthorizationRevision);
        Policy(context.Policy);
    }

    public static void Key(string key)
    {
        Require(!string.IsNullOrEmpty(key) && key.Length <= 256 && !key.StartsWith('/')
            && !key.EndsWith('/') && !key.Contains('\\') && !key.Any(char.IsControl)
            && key.Split('/').All(part => part.Length > 0 && part is not "." and not ".."),
            "invalid_request", "Keys must be bounded relative logical names.");
    }

    public static void Prefix(string prefix)
    {
        Require(prefix is not null && prefix.Length <= 256, "invalid_request", "Invalid prefix.");
        if (prefix.Length > 0) Key(prefix.EndsWith('/') ? prefix[..^1] : prefix);
    }

    public static bool Allows(AspAccessPolicy policy, string key) => key.StartsWith(policy.Prefix, StringComparison.Ordinal)
        && (policy.KeyFrom is null || string.CompareOrdinal(key, policy.KeyFrom) >= 0)
        && (policy.KeyBefore is null || string.CompareOrdinal(key, policy.KeyBefore) < 0);

    public static void DemandKey(AspAccessContext context, string key)
    {
        Key(key);
        Require(Allows(context.Policy, key), "forbidden", "The key is outside the authorized store scope.");
    }

    public static void Item(AspProviderObject value)
    {
        Require(value is not null, "invalid_result", "Missing object metadata.");
        Key(value.Key);
        RequestId(value.Version);
        Require(value.Length >= 0 && value.Sha256 is { Length: 64 } && value.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "invalid_result", "Invalid object metadata.");
    }

    public static byte[] Content(string content, int limit)
    {
        Require(content is not null && content.Length <= ((limit + 2) / 3) * 4, "limit_exceeded", "Content exceeds the payload limit.");
        try
        {
            var bytes = Convert.FromBase64String(content);
            Require(bytes.Length <= limit && Convert.ToBase64String(bytes) == content, "limit_exceeded", "Invalid or excessive content.");
            return bytes;
        }
        catch (FormatException) { throw new AspException("invalid_request", "Content must use canonical base64."); }
    }

    public static void Write(AspWriteRequest request, AspAccessContext context, TimeProvider clock)
    {
        RequestId(request.RequestId);
        DemandKey(context, request.Key);
        var now = clock.GetUtcNow();
        Require(request.NotAfter > now && request.NotAfter <= now.AddMinutes(5), "request_expired", "Write validity must be within the next five minutes.");
        if (request.ExpectedVersion is not null) RequestId(request.ExpectedVersion);
    }

    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static T Parse<T>(JsonElement json)
    {
        try { return json.Deserialize<T>(AspProtocol.JsonOptions) ?? throw new AspException("invalid_request", "A JSON object is required."); }
        catch (JsonException) { throw new AspException("invalid_request", "The payload does not match the ASP schema."); }
    }
}
