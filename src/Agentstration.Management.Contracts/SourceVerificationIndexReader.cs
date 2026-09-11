using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;
using Agentstration.ResourceManagement;

namespace Agentstration.Management.Contracts;

public sealed partial class SourceVerificationIndexReader : ISourceVerificationIndexReader
{
    public const int MaximumIndexBytes = 1024 * 1024;
    public const int MaximumEntries = 10_000;
    private static readonly HashSet<string> EnvelopeProperties = new(StringComparer.Ordinal)
    {
        "apiVersion", "kind", "metadata", "definition"
    };

    public VerifiedSourceIndexManifest Read(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Encoding.UTF8.GetByteCount(content) > MaximumIndexBytes)
            throw Invalid("source_verification_index_size_limit", $"Verified Source indexes cannot exceed {MaximumIndexBytes} UTF-8 bytes.");

        try
        {
            var documents = ResourceManifestSerializer.FromYamlDocuments<JsonElement>(content);
            if (documents.Count != 1 || documents[0].ValueKind != JsonValueKind.Object)
                throw Invalid("source_verification_index_invalid", "A verified Source index must contain exactly one YAML object.");
            foreach (var property in documents[0].EnumerateObject())
            {
                if (!EnvelopeProperties.Contains(property.Name))
                    throw Invalid("source_verification_index_envelope_invalid", $"Top-level property '{property.Name}' is not part of the Agentstration resource envelope.");
            }

            var index = ResourceManifestSerializer.FromJsonStrict<VerifiedSourceIndexManifest>(documents[0].GetRawText());
            Validate(index);
            return index;
        }
        catch (SourceValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            throw Invalid("source_verification_index_invalid", $"The verified Source index is invalid: {exception.Message}");
        }
    }

    private static void Validate(VerifiedSourceIndexManifest index)
    {
        if (!string.Equals(index.ApiVersion, ManagementApiVersions.CoreV1, StringComparison.Ordinal))
            throw Invalid("source_verification_index_api_version_invalid", $"Verified Source indexes require apiVersion '{ManagementApiVersions.CoreV1}'.");
        if (!string.Equals(index.Kind, SourceVerificationKinds.VerifiedSourceIndex, StringComparison.Ordinal))
            throw Invalid("source_verification_index_kind_invalid", $"Verified Source index kind must be '{SourceVerificationKinds.VerifiedSourceIndex}'.");
        if (index.Definition?.Sources is null)
            throw Invalid("source_verification_index_definition_missing", "A verified Source index requires definition.sources.");
        if (index.Definition.Sources.Count > MaximumEntries)
            throw Invalid("source_verification_index_entry_limit", $"Verified Source indexes cannot contain more than {MaximumEntries} entries.");

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in index.Definition.Sources)
        {
            var source = entry.Source
                ?? throw Invalid("source_verification_identity_invalid", "source is required.");
            ValidatePortableName(source.Publisher, "source.publisher");
            ValidatePortableName(source.Name, "source.name");
            Required(entry.Version, "version");
            ValidateDigest(entry.ManifestDigest, "manifestDigest");
            if (entry.Publisher is null || !string.Equals(entry.Publisher.Name, source.Publisher, StringComparison.Ordinal))
                throw Invalid("source_verification_publisher_mismatch", "publisher.name must equal source.publisher.");
            if (entry.ManifestLocations is null || entry.ManifestLocations.Count == 0)
                throw Invalid("source_verification_manifest_location_missing", "At least one manifest location is required.");
            foreach (var location in entry.ManifestLocations)
            {
                if (!Uri.TryCreate(location.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    throw Invalid("source_verification_manifest_location_invalid", "Manifest locations must be absolute HTTP(S) URLs.");
            }
            ValidateEvidence(entry.Evidence);
            var channelKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var channel in entry.Channels ?? [])
            {
                Required(channel.Name, "channels.name");
                Required(channel.Revision, "channels.revision");
                ValidateDigest(channel.SnapshotDigest, "channels.snapshotDigest");
                ValidateEvidence(channel.Evidence);
                if (!channelKeys.Add($"{channel.Name}#{channel.Revision}#{channel.SnapshotDigest}"))
                    throw Invalid("source_verification_channel_duplicate", $"Channel verification '{channel.Name}' is declared more than once for the same revision and digest.");
            }

            var key = $"{source.Publisher}/{source.Name}@{entry.Version}#{entry.ManifestDigest}";
            if (!identities.Add(key))
                throw Invalid("source_verification_index_duplicate", $"Verified Source entry '{key}' is declared more than once.");
        }
    }

    private static void ValidateEvidence(SourceVerificationEvidence? evidence)
    {
        if (evidence is null)
            throw Invalid("source_verification_evidence_missing", "Verification evidence is required.");
        Required(evidence.Type, "evidence.type");
        Required(evidence.Authority, "evidence.authority");
    }

    private static void ValidatePortableName(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !PortableNameRegex().IsMatch(value))
            throw Invalid("source_verification_identity_invalid", $"{field} must be a portable lowercase Source identity.");
    }

    private static void ValidateDigest(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !Sha256Regex().IsMatch(value))
            throw Invalid("source_verification_digest_invalid", $"{field} must be a lowercase sha256 digest.");
    }

    private static void Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw Invalid("source_verification_value_invalid", $"{field} is required and cannot contain surrounding whitespace.");
    }

    private static SourceValidationException Invalid(string code, string message) => new(code, message);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,59}$", RegexOptions.CultureInvariant)]
    private static partial Regex PortableNameRegex();

    [GeneratedRegex("^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
