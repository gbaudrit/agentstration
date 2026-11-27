using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Atc.Abstractions;

namespace Agentstration.Extensions.GitHub;

// Execution-only protected HTTP envelope. Never a business Tool argument or a journal entry.
public sealed record GitHubServiceEnvelope(string TypeId, string Version, string SchemaDigest, JsonElement Options,
    IReadOnlyDictionary<string, string> Inputs)
{
    public override string ToString() => "[REDACTED]";
}
public sealed record GitHubServiceOptions(string ApiBaseUrl, string Repository, string ManagedLabelPrefix);

public static class GitHubServiceContract
{
    public const string Header = "X-Agentstration-Connected-Service";
    public const string Metadata = "agentstration.work.connected-service.http.v1";
    public const string TypeId = "github";
    public const string Version = "1.0.0";
    public static IReadOnlyList<string> StandardInputs { get; } = Array.AsReadOnly(new[] { "providerId", "targetId", "bindingId", "revision", "subjectId" });
    public static AepConnectedServiceTypeVersionDescriptor TypeVersion { get; } = AepConnectedServiceTypeVersionDescriptor.Create(Version,
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "apiBaseUrl", "repository", "managedLabelPrefix" },
            properties = new { apiBaseUrl = new { type = "string", maxLength = 512 }, repository = new { type = "string", maxLength = 201 }, managedLabelPrefix = new { type = "string", minLength = 1, maxLength = 64 } }
        }),
        [.. StandardInputs.Select(id => new AepConnectedServiceInputDescriptor(id, true)),
            new("credential", true, Protection: AepValueProtection.Secured)],
        StandardInputs.Select(id => new AepConnectedServiceExportDescriptor(id) { SourceInputId = id }).ToArray());
    public static JsonElement TransportDeclaration { get; } = JsonSerializer.SerializeToElement(new
    {
        version = "1.0",
        serviceTypeId = TypeId,
        serviceTypeVersion = Version,
        schemaDigest = TypeVersion.SchemaDigest,
        inputContract = TypeVersion.Inputs.OrderBy(value => value.Id, StringComparer.Ordinal).Select(value => new
        { id = value.Id, required = value.Required, type = value.Type.ToString(), protection = value.Protection.ToString(), format = value.Format }).ToArray(),
        exportContract = TypeVersion.Exports.OrderBy(value => value.Id, StringComparer.Ordinal).Select(value => new
        { id = value.Id, type = value.Type.ToString(), protection = value.Protection.ToString(), format = value.Format, sourceInputId = value.SourceInputId }).ToArray()
    });
    public static string Authority(string apiBaseUrl) => "github." + Hash(new Uri(apiBaseUrl.TrimEnd('/') + "/").AbsoluteUri);
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static GitHubServiceEnvelope Read(HttpRequest request)
    {
        try
        {
            var header = request.Headers[Header];
            if (header.Count != 1 || header[0] is not { Length: > 0 and <= 24_576 } encoded) throw new GitHubProviderException("github_service_required");
            var envelope = JsonSerializer.Deserialize<GitHubServiceEnvelope>(Convert.FromBase64String(encoded), GitHubConnection.Json)
                ?? throw new GitHubProviderException("github_service_invalid");
            if (envelope.TypeId != TypeId || envelope.Version != Version || envelope.SchemaDigest != TypeVersion.SchemaDigest
                || envelope.Options.ValueKind != JsonValueKind.Object || envelope.Inputs is null || envelope.Inputs.Count != 6
                || !StandardInputs.Append("credential").All(id => envelope.Inputs.TryGetValue(id, out var value) && !string.IsNullOrWhiteSpace(value))
                || StandardInputs.Any(id => !AtcValidationText(envelope.Inputs[id]))) throw new GitHubProviderException("github_service_invalid");
            return envelope;
        }
        catch (Exception error) when (error is FormatException or JsonException) { throw new GitHubProviderException("github_service_invalid"); }
    }
    private static bool AtcValidationText(string value) => value.Length <= 128 && !value.Any(char.IsControl);
}
