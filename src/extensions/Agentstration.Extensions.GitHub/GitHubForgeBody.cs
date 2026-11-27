using System.Text;
using System.Text.Json;
using Agentstration.Afc.Abstractions;

namespace Agentstration.Extensions.GitHub;

internal static class GitHubForgeBody
{
    private const string Prefix = "\n\n<!-- agentstration-afc:";
    private const string Operation = "\n\n<!-- agentstration-operation:";
    internal sealed record Metadata(bool NullBody, IReadOnlyList<AfcTrackingRef> Tracking);
    internal static string Encode(string? body, IReadOnlyList<AfcTrackingRef> tracking)
    {
        var result = (body ?? "") + Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Metadata(body is null, tracking), AfcContract.Json)) + " -->";
        if (result.Length > 32_600) throw new GitHubProviderException("github_body_limit");
        return result;
    }
    internal static (string? Body, IReadOnlyList<AfcTrackingRef> Tracking) Decode(string? body)
    {
        if (body is null) return (null, []);
        var marker = body.LastIndexOf(Operation, StringComparison.Ordinal);
        if (marker < 0 || !body.EndsWith(" -->", StringComparison.Ordinal)) return (body, []);
        var key = body.AsSpan(marker + Operation.Length, body.Length - marker - Operation.Length - 4);
        if (key.Length != 64 || !key.ToArray().All(char.IsAsciiHexDigitLower)) return (body, []);
        var content = body[..marker]; var metadata = content.LastIndexOf(Prefix, StringComparison.Ordinal);
        if (metadata < 0 || !content.EndsWith(" -->", StringComparison.Ordinal)) return (body, []);
        var encoded = content[(metadata + Prefix.Length)..^4];
        if (encoded.Length > 16_384) throw new GitHubProviderException("github_response_invalid");
        try
        {
            var value = JsonSerializer.Deserialize<Metadata>(Convert.FromBase64String(encoded), AfcContract.Json) ?? throw new GitHubProviderException("github_response_invalid");
            if (value.Tracking is null || value.Tracking.Count > 16 || value.NullBody && metadata != 0) throw new GitHubProviderException("github_response_invalid");
            return (value.NullBody ? null : content[..metadata], value.Tracking);
        }
        catch (FormatException) { throw new GitHubProviderException("github_response_invalid"); }
    }
}
