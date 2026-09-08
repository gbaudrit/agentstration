using System.IO.Compression;
using System.Text;
using Agentstration.Management.Abstractions;

namespace Agentstration.Infrastructure.Sources;

public sealed class ZipSourceSnapshotContentReader(ISourceSnapshotArtifactStore artifacts) : ISourceSnapshotContentReader
{
    public async Task<ISourceSnapshotContent> OpenAsync(SourceSnapshotArtifactReference reference, CancellationToken cancellationToken)
    {
        if (!string.Equals(reference.MediaType, "application/zip", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(reference.MediaType, "application/x-zip-compressed", StringComparison.OrdinalIgnoreCase))
            throw new SourceValidationException("source_snapshot_media_type_unsupported", $"Snapshot media type '{reference.MediaType}' is not supported for catalog discovery.");
        var stream = await artifacts.OpenReadAsync(reference, cancellationToken);
        try { return new Content(stream, reference); }
        catch { await stream.DisposeAsync(); throw; }
    }

    private sealed class Content : ISourceSnapshotContent
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly Stream stream;
        private readonly ZipArchive archive;
        private readonly IReadOnlyDictionary<string, ZipArchiveEntry> entries;

        public Content(Stream stream, SourceSnapshotArtifactReference reference)
        {
            this.stream = stream;
            try { archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
            catch (InvalidDataException exception) { throw new SourceValidationException("source_snapshot_archive_invalid", $"Source snapshot is not a valid ZIP archive: {exception.Message}"); }
            var indexed = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            var files = 0;
            foreach (var entry in archive.Entries)
            {
                var rawPath = entry.FullName.EndsWith('/') ? entry.FullName[..^1] : entry.FullName;
                if (rawPath.Length == 0) continue;
                var path = SourceDescendantPath.Normalize(rawPath, $"Archive entry '{entry.FullName}'");
                if (IsLink(entry)) throw new SourceValidationException("source_snapshot_link_forbidden", $"Archive entry '{entry.FullName}' cannot be a symbolic link or reparse point.");
                if (entry.FullName.EndsWith('/')) continue;
                files++;
                expanded = checked(expanded + entry.Length);
                if (files > reference.EntryCount || expanded > reference.ExpandedBytes)
                    throw new SourceValidationException("source_snapshot_limits_invalid", "Source snapshot archive exceeds its validated entry or expanded-size metadata.");
                if (!indexed.TryAdd(path, entry))
                    throw new SourceValidationException("source_snapshot_path_duplicate", $"Archive path '{path}' is duplicated or differs only by case.");
            }
            entries = indexed;
            Paths = indexed.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        public IReadOnlyCollection<string> Paths { get; }

        public async Task<string> ReadTextAsync(string normalizedPath, int maximumBytes, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
            var path = SourceDescendantPath.Normalize(normalizedPath, "Snapshot content path");
            if (!entries.TryGetValue(path, out var entry))
                throw new SourceValidationException("source_content_missing", $"Snapshot content '{path}' does not exist.");
            if (entry.Length > maximumBytes)
                throw new SourceValidationException("source_content_size_limit", $"Snapshot content '{path}' exceeds {maximumBytes} bytes.");
            await using var input = entry.Open();
            using var output = new MemoryStream((int)entry.Length);
            var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (output.Length + read > maximumBytes)
                    throw new SourceValidationException("source_content_size_limit", $"Snapshot content '{path}' exceeds {maximumBytes} bytes.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            try { return StrictUtf8.GetString(output.GetBuffer(), 0, checked((int)output.Length)); }
            catch (DecoderFallbackException exception)
            {
                throw new SourceValidationException("source_content_encoding_invalid", $"Snapshot content '{path}' is not valid UTF-8: {exception.Message}");
            }
        }

        public ValueTask DisposeAsync()
        {
            archive.Dispose();
            return stream.DisposeAsync();
        }

        private static bool IsLink(ZipArchiveEntry entry) =>
            ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
            || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }
}
