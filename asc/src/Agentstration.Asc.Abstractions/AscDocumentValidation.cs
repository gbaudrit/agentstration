using System.Security.Cryptography;
using System.Text.Json;

namespace Agentstration.Asc.Abstractions;

public static class AscDocumentValidation
{
    public static string Digest(JsonElement value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, AscContract.JsonOptions)));

    public static void Identity(string value)
    {
        AscValidation.Require(value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'),
            "invalid_request", "A bounded logical identity is required.");
    }

    public static void Policy(AscDocumentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Identity(policy.Collection);
        if (policy.DocumentIds is { } ids)
        {
            AscValidation.Require(ids.Count <= 100 && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count,
                "invalid_policy", "Document selection exceeds its bound or contains duplicates.");
            foreach (var id in ids) Identity(id);
            AscValidation.Require(!policy.Put && !policy.Delete && !policy.Publish, "invalid_policy", "A document subset is read-only.");
        }
        AscValidation.Require(policy.MaximumPageSize is > 0 and <= 100
            && policy.MaximumDocumentBytes is > 0 and <= AscDocuments.MaximumDocumentBytes
            && policy.MaximumResultBytes is >= AscDocuments.MaximumDocumentBytes and <= AscDocuments.MaximumResultBytes
            && policy.QueryProfiles is { Count: <= 8 } && (!policy.Query || policy.QueryProfiles.Count > 0)
            && policy.QueryProfiles.All(p => !string.IsNullOrWhiteSpace(p) && p.Length <= 128)
            && policy.QueryProfiles.Distinct(StringComparer.Ordinal).Count() == policy.QueryProfiles.Count,
            "invalid_policy", "Invalid document restrictions.");
    }

    public static AscDocumentPolicy Intersect(AscDocumentPolicy maximum, AscDocumentPolicy grant)
    {
        Policy(maximum); Policy(grant);
        AscValidation.Require(maximum.Collection == grant.Collection, "forbidden", "The collection is not authorized.");
        var profiles = maximum.QueryProfiles!.Intersect(grant.QueryProfiles!, StringComparer.Ordinal).ToArray();
        return maximum with
        {
            Get = maximum.Get && grant.Get,
            Put = maximum.Put && grant.Put,
            Delete = maximum.Delete && grant.Delete,
            Query = maximum.Query && grant.Query && profiles.Length > 0,
            Publish = maximum.Publish && grant.Publish,
            MaximumPageSize = Math.Min(maximum.MaximumPageSize, grant.MaximumPageSize),
            MaximumDocumentBytes = Math.Min(maximum.MaximumDocumentBytes, grant.MaximumDocumentBytes),
            MaximumResultBytes = Math.Min(maximum.MaximumResultBytes, grant.MaximumResultBytes),
            DocumentIds = maximum.DocumentIds is null ? grant.DocumentIds : grant.DocumentIds is null ? maximum.DocumentIds
                : maximum.DocumentIds.Intersect(grant.DocumentIds, StringComparer.Ordinal).ToArray(),
            QueryProfiles = profiles
        };
    }

    public static bool IsNarrower(AscDocumentPolicy candidate, AscDocumentPolicy maximum)
    {
        var effective = Intersect(maximum, candidate);
        return effective with { QueryProfiles = candidate.QueryProfiles, DocumentIds = candidate.DocumentIds } == candidate
            && (effective.DocumentIds is null ? candidate.DocumentIds is null : candidate.DocumentIds is not null && effective.DocumentIds.SequenceEqual(candidate.DocumentIds, StringComparer.Ordinal))
            && effective.QueryProfiles!.SequenceEqual(candidate.QueryProfiles!, StringComparer.Ordinal);
    }

    public static void Demand(AscDocumentContext context, string operation)
    {
        AscValidation.Store(context.Store);
        Identity(context.Selection.Collection);
        if (context.Selection.Publication is { } publication) Identity(publication);
        AscValidation.Require(context.Policy.Collection == context.Selection.Collection
            && !string.IsNullOrWhiteSpace(context.Subject) && !string.IsNullOrWhiteSpace(context.AuthorizationRevision),
            "forbidden", "The effective document target is not authorized.");
        var allowed = operation switch
        {
            "get" => context.Policy.Get,
            "put" => context.Policy.Put && context.Selection.Publication is null,
            "delete" => context.Policy.Delete && context.Selection.Publication is null,
            "query" => context.Policy.Query,
            "prepare" => context.Policy.Publish && context.Selection.Publication is null,
            "seal" or "retire" => context.Policy.Publish,
            "inspect" => context.Policy.Get || context.Policy.Query || context.Policy.Publish,
            "describe" => true,
            _ => false
        };
        AscValidation.Require(allowed, "forbidden", "The document operation is not authorized.");
    }

    public static void Publication(AscDocumentContext context, string publication)
    {
        Identity(publication);
        AscValidation.Require(context.Selection.Publication == publication, "forbidden", "The exact publication is not authorized.");
    }

    public static void Document(JsonElement value, int maximumBytes)
    {
        AscValidation.Require(value.ValueKind == JsonValueKind.Object
            && JsonSerializer.SerializeToUtf8Bytes(value).Length <= maximumBytes,
            "limit_exceeded", "A bounded JSON object document is required.");
        ValidateJson(value, 0);
    }

    public static void ValidateJson(JsonElement value, int depth = 0)
    {
        AscValidation.Require(depth <= 16, "limit_exceeded", "JSON nesting exceeds its limit.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                AscValidation.Require(names.Add(property.Name) && !property.Name.Contains('\0'), "invalid_request", "Invalid or duplicate JSON property.");
                ValidateJson(property.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            AscValidation.Require(value.GetArrayLength() <= 1024, "limit_exceeded", "The array exceeds its limit.");
            foreach (var item in value.EnumerateArray()) ValidateJson(item, depth + 1);
        }
        else if (value.ValueKind == JsonValueKind.String)
            AscValidation.Require(!value.GetString()!.Contains('\0'), "invalid_request", "NUL is not supported in document strings.");
        else if (value.ValueKind == JsonValueKind.Number)
            AscValidation.Require(value.TryGetDecimal(out var number) && JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(number)),
                "unsupported_value", "Numbers must be exactly representable in the profile decimal domain.");
    }
}
