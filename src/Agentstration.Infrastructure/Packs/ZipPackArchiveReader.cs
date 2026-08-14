using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using YamlDotNet.Core;

namespace Agentstration.Infrastructure.Packs;

public sealed class ZipPackArchiveReader : IPackArchiveReader
{
    private const int MaximumEntries = 128;
    private const long MaximumFileBytes = 4 * 1024 * 1024;
    private const long MaximumExpandedBytes = 16 * 1024 * 1024;

    public async Task<PackArchive> ReadAsync(Stream archive, string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (!archive.CanRead) throw new PackValidationException("pack_archive_unreadable", "The Pack archive stream is not readable.");
        await using var buffered = new MemoryStream();
        await archive.CopyToAsync(buffered, cancellationToken);
        var content = buffered.ToArray();
        using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
        var files = zip.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (files.Length == 0) throw new PackValidationException("pack_archive_empty", "The Pack archive is empty.");
        if (files.Length > MaximumEntries) throw new PackValidationException("pack_archive_entry_limit", $"A Pack archive can contain at most {MaximumEntries} files.");
        if (files.Any(IsSymbolicLink)) throw new PackValidationException("pack_archive_link_forbidden", "Pack archives cannot contain symbolic links.");
        if (files.Any(entry => entry.Length > MaximumFileBytes) || files.Sum(entry => entry.Length) > MaximumExpandedBytes)
            throw new PackValidationException("pack_archive_size_limit", "The expanded Pack archive exceeds the configured size limit.");

        var byPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in files)
        {
            var path = NormalizePath(entry.FullName);
            if (!byPath.TryAdd(path, entry)) throw new PackValidationException("pack_archive_path_duplicate", $"Archive path '{path}' is duplicated.");
        }

        if (!byPath.TryGetValue("pack.yaml", out var manifestEntry) && !byPath.TryGetValue("pack.yml", out manifestEntry) && !byPath.TryGetValue("pack.json", out manifestEntry))
            throw new PackValidationException("pack_manifest_missing", "The Pack archive must contain pack.yaml, pack.yml, or pack.json at its root.");
        var manifestText = await ReadTextAsync(manifestEntry, cancellationToken);
        var manifest = Parse<PackManifest>(manifestEntry, manifestText);

        var documents = new List<PackResourceDocument>(manifest.Spec.Resources.Count);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaredPath in manifest.Spec.Resources)
        {
            var path = NormalizePath(declaredPath);
            if (!listed.Add(path)) throw new PackValidationException("pack_resource_path_duplicate", $"Resource path '{path}' is listed more than once.");
            if (!byPath.TryGetValue(path, out var entry)) throw new PackValidationException("pack_resource_missing", $"Resource file '{path}' was not found in the archive.");
            if (path.Equals("pack.yaml", StringComparison.OrdinalIgnoreCase) || path.Equals("pack.yml", StringComparison.OrdinalIgnoreCase) || path.Equals("pack.json", StringComparison.OrdinalIgnoreCase))
                throw new PackValidationException("pack_resource_manifest_self_reference", "The Pack manifest cannot be installed as a contained resource.");
            var text = await ReadTextAsync(entry, cancellationToken);
            var json = Parse<JsonElement>(entry, text);
            documents.Add(ReadDocument(path, json));
        }

        var safeSource = string.IsNullOrWhiteSpace(source) ? "local-archive" : Path.GetFileName(source.Trim());
        if (safeSource.Length > 256) safeSource = safeSource[..256];
        return new PackArchive(manifest, documents, safeSource, content);
    }

    private static PackResourceDocument ReadDocument(string path, JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object) throw new PackValidationException("pack_resource_invalid", $"Resource '{path}' must be a JSON or YAML object.");
        var apiVersion = RequiredString(manifest, "apiVersion", path);
        var kind = RequiredString(manifest, "kind", path);
        if (!manifest.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
            throw new PackValidationException("pack_resource_metadata_invalid", $"Resource '{path}' requires metadata.");
        var name = RequiredString(metadata, "name", path);
        return new PackResourceDocument(path, apiVersion, kind, name, manifest.Clone());
    }

    private static string RequiredString(JsonElement value, string property, string path)
    {
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            throw new PackValidationException("pack_resource_identity_invalid", $"Resource '{path}' requires a non-empty '{property}'.");
        return element.GetString()!;
    }

    private static async Task<string> ReadTextAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PackValidationException("pack_file_encoding_invalid", $"Pack file '{entry.FullName}' is not valid UTF-8: {exception.Message}");
        }
    }

    private static T Parse<T>(ZipArchiveEntry entry, string text)
    {
        try
        {
            return IsJson(entry.Name)
                ? ResourceManifestSerializer.FromJson<T>(text)
                : ResourceManifestSerializer.FromYaml<T>(text);
        }
        catch (JsonException exception)
        {
            throw new PackValidationException("pack_file_json_invalid", $"Pack file '{entry.FullName}' contains invalid JSON: {exception.Message}");
        }
        catch (YamlException exception)
        {
            throw new PackValidationException("pack_file_yaml_invalid", $"Pack file '{entry.FullName}' contains invalid YAML: {exception.Message}");
        }
    }

    private static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new PackValidationException("pack_archive_path_invalid", "Pack archive paths cannot be empty.");
        var path = value.Replace('\\', '/').Trim();
        if (path.StartsWith('/') || Path.IsPathRooted(path)) throw new PackValidationException("pack_archive_path_invalid", $"Archive path '{value}' must be relative.");
        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment is "" or "." or "..")) throw new PackValidationException("pack_archive_path_invalid", $"Archive path '{value}' is not safe.");
        return string.Join('/', segments);
    }

    private static bool IsJson(string name) => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    private static bool IsSymbolicLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
}
