using System.Text.Json;
using Agentstration.Aep.Abstractions;

namespace Agentstration.Extensions.Git;

public sealed record GitServiceOptions(string Repository, string Authentication, string Username,
    IReadOnlyList<string> AllowedRefPrefixes, IReadOnlyList<string> AllowedPathPrefixes, string? RefName = null);
public sealed record GitServiceEnvelope(string TypeId, string Version, string SchemaDigest, JsonElement Options, IReadOnlyDictionary<string, string> Inputs)
{
    public override string ToString() => "[REDACTED]";
}
public static class GitServiceContract
{
    public const string Header = "X-Agentstration-Connected-Service";
    public const string Metadata = "agentstration.work.connected-service.http.v1";
    public const string TypeId = "git";
    public const string Version = "1.0.0";
    public static AepConnectedServiceTypeVersionDescriptor TypeVersion { get; } = AepConnectedServiceTypeVersionDescriptor.Create(Version,
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "repository", "authentication", "username", "allowedRefPrefixes", "allowedPathPrefixes" },
            properties = new
            {
                repository = new { type = "string", minLength = 1, maxLength = 2048 },
                authentication = new { type = "string", @enum = new[] { "Basic", "Bearer" } },
                username = new { type = "string", maxLength = 128 },
                allowedRefPrefixes = new { type = "array", minItems = 1, maxItems = 16, items = new { type = "string", minLength = 1, maxLength = 256 } },
                allowedPathPrefixes = new { type = "array", minItems = 1, maxItems = 16, items = new { type = "string", minLength = 1, maxLength = 256 } },
                refName = new { type = "string", minLength = 1, maxLength = 256 }
            }
        }), [new("credential", true, Protection: AepValueProtection.Secured)],
        [new("credential", Protection: AepValueProtection.Secured) { SourceInputId = "credential" }]);
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
    public static (GitServiceOptions Options, string Credential) Read(HttpRequest request)
    {
        try
        {
            var values = request.Headers[Header];
            if (values.Count != 1 || values[0] is not { Length: > 0 and <= 24_576 } header) throw new GitOperationException("git_service_invalid");
            var envelope = JsonSerializer.Deserialize<GitServiceEnvelope>(Convert.FromBase64String(header), JsonSerializerOptions.Web)
                ?? throw new GitOperationException("git_service_invalid");
            if (envelope.TypeId != TypeId || envelope.Version != Version || envelope.SchemaDigest != TypeVersion.SchemaDigest
                || envelope.Inputs is not { Count: 1 } || !envelope.Inputs.TryGetValue("credential", out var credential) || string.IsNullOrWhiteSpace(credential))
                throw new GitOperationException("git_service_invalid");
            var options = envelope.Options.Deserialize<GitServiceOptions>(JsonSerializerOptions.Web) ?? throw new GitOperationException("git_service_invalid");
            return (options, credential);
        }
        catch (Exception error) when (error is JsonException or FormatException) { throw new GitOperationException("git_service_invalid"); }
    }
}
