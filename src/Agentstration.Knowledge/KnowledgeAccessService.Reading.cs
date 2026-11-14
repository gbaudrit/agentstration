using System.Text;
using System.Text.RegularExpressions;
using Agentstration.Knowledge.Contracts;

namespace Agentstration.Knowledge;

public sealed partial class KnowledgeAccessService
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static bool IsText(string mediaType) => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("markdown", StringComparison.OrdinalIgnoreCase);
    private async Task<string> TextAsync(KnowledgeSnapshotResource snapshot, int document, CancellationToken ct)
    {
        var artifact = snapshot.Artifacts[document];
        if (!IsText(artifact.MediaType)) throw Error("knowledge_access_media_type_unsupported");
        if (artifact.Length is < 0 or > MaximumDocumentBytes) throw Error("knowledge_access_document_limit");
        var bytes = await content.ReadAsync(snapshot, artifact, ct);
        if (bytes.Length != artifact.Length) throw Error("knowledge_access_content_incomplete");
        try { return Utf8.GetString(bytes.Span); }
        catch (DecoderFallbackException) { throw Error("knowledge_access_encoding_invalid"); }
    }
    private static string Slice(Rune[] runes, int start, int count)
    {
        var result = new StringBuilder();
        foreach (var rune in runes.AsSpan(start, count)) result.Append(rune.ToString());
        return result.ToString();
    }
    private static int Document(KnowledgeSnapshotResource snapshot, string id)
    {
        for (var index = 0; index < snapshot.Artifacts.Count; index++)
            if (snapshot.Artifacts[index].ArtifactId == id) return index;
        throw Error("knowledge_access_document_outside_snapshot");
    }

    public async Task<KnowledgeAccessResult> ReadAsync(KnowledgeSourceId id, KnowledgeReadRequest request, CancellationToken ct)
    {
        if (request.Range is { } range && (range.Start < 0 || range.End < range.Start || range.End > MaximumDocumentBytes))
            throw Error("knowledge_access_range_invalid");
        if (request.MaxCharacters is < 1 or > MaximumCharacters) throw Error("knowledge_access_read_limit");
        var source = await SourceAsync(id, true, ct);
        AccessToken token;
        KnowledgeSnapshotResource snapshot;
        if (request.Cursor is not null)
        {
            if (request.SnapshotId is not null || request.DocumentId is not null || request.Range is not null || request.PreparationId is not null)
                throw Error("knowledge_access_cursor_parameters");
            token = Open(request.Cursor, "read", source);
            snapshot = await ContinueAsync(source, token, ct);
        }
        else
        {
            if (request.Range is not null && (request.DocumentId is null || request.SnapshotId is null))
                throw Error("knowledge_access_range_requires_snapshot");
            var selected = await SelectAsync(source, request.SnapshotId, request.PreparationId, ct);
            if (selected.Pending is not null) return selected.Pending;
            snapshot = selected.Snapshot!;
            var document = request.DocumentId is null ? 0 : Document(snapshot, request.DocumentId);
            token = await SessionAsync(source, snapshot, "read", ct);
            token = token with
            {
                Document = document,
                Position = request.Range?.Start ?? 0,
                End = request.Range?.End,
                SingleDocument = request.DocumentId is not null
            };
        }
        if (token.Document < 0 || token.Document > snapshot.Artifacts.Count || token.Position < 0)
            throw Error("knowledge_access_cursor_invalid");
        var chunks = new List<KnowledgeReadChunk>();
        var remaining = request.MaxCharacters;
        while (token.Document < snapshot.Artifacts.Count && remaining > 0)
        {
            var text = await TextAsync(snapshot, token.Document, ct);
            var runes = text.EnumerateRunes().ToArray();
            var stop = token.End ?? runes.Length;
            if (token.Position > runes.Length || stop < token.Position || stop > runes.Length)
                throw Error("knowledge_access_range_invalid");
            var count = (int)Math.Min(remaining, stop - token.Position);
            chunks.Add(new(snapshot.Artifacts[token.Document].ArtifactId, snapshot.Artifacts[token.Document].MediaType,
                new(token.Position, token.Position + count), Slice(runes, (int)token.Position, count), token.Position + count == runes.Length));
            remaining -= count;
            token = token with { Position = token.Position + count };
            if (token.Position == stop)
            {
                token = token with { Document = token.SingleDocument ? snapshot.Artifacts.Count : token.Document + 1, Position = 0, End = null };
            }
        }
        var ended = token.Document == snapshot.Artifacts.Count;
        return new()
        {
            State = "ready",
            SourceUid = source.Uid,
            SnapshotId = snapshot.Name,
            PublishedAt = snapshot.PublishedAt,
            Chunks = chunks,
            EndOfSelection = ended,
            NextCursor = ended ? null : Seal(token),
            CursorExpiresAt = token.Expires
        };
    }

    public async Task<KnowledgeAccessResult> SearchRegexAsync(KnowledgeSourceId id, KnowledgeRegexRequest request, CancellationToken ct)
    {
        if (request.Limit is < 1 or > 100 || request.ContextCharacters is < 0 or > 1_000)
            throw Error("knowledge_access_search_limit");
        var source = await SourceAsync(id, true, ct);
        AccessToken token;
        KnowledgeSnapshotResource snapshot;
        if (request.Cursor is not null)
        {
            if (request.Pattern is not null || request.SnapshotId is not null || request.PreparationId is not null
                || !request.IgnoreCase || request.ContextCharacters != 160) throw Error("knowledge_access_cursor_parameters");
            token = Open(request.Cursor, "regex", source);
            snapshot = await ContinueAsync(source, token, ct);
        }
        else
        {
            if (string.IsNullOrEmpty(request.Pattern) || request.Pattern.Length > 512) throw Error("knowledge_access_pattern_invalid");
            _ = Regex(request.Pattern, request.IgnoreCase);
            var selected = await SelectAsync(source, request.SnapshotId, request.PreparationId, ct);
            if (selected.Pending is not null) return selected.Pending;
            snapshot = selected.Snapshot!;
            token = (await SessionAsync(source, snapshot, "regex", ct)) with
            { Pattern = request.Pattern, IgnoreCase = request.IgnoreCase, ContextCharacters = request.ContextCharacters };
        }
        if (snapshot.Artifacts.Sum(value => value.Length) > 8 * 1024 * 1024) throw Error("knowledge_access_scan_limit");
        var regex = Regex(token.Pattern ?? throw Error("knowledge_access_cursor_invalid"), token.IgnoreCase);
        var matches = new List<KnowledgeRegexMatch>();
        var outputCharacters = 0;
        try
        {
            while (token.Document < snapshot.Artifacts.Count && matches.Count < request.Limit)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsText(snapshot.Artifacts[token.Document].MediaType))
                { token = token with { Document = token.Document + 1, Position = 0 }; continue; }
                var text = await TextAsync(snapshot, token.Document, ct);
                var boundaries = new List<int> { 0 };
                foreach (var rune in text.EnumerateRunes()) boundaries.Add(boundaries[^1] + rune.Utf16SequenceLength);
                if (token.Position > text.Length) { token = token with { Document = token.Document + 1, Position = 0 }; continue; }
                var match = regex.Match(text, (int)token.Position);
                if (!match.Success) { token = token with { Document = token.Document + 1, Position = 0 }; continue; }
                var start = boundaries.BinarySearch(match.Index);
                var end = boundaries.BinarySearch(match.Index + match.Length);
                if (start < 0 || end < 0) throw Error("knowledge_access_pattern_splits_unicode");
                var readStart = Math.Max(0, start - token.ContextCharacters);
                var readEnd = Math.Min(boundaries.Count - 1, end + token.ContextCharacters);
                var excerpt = text[boundaries[readStart]..boundaries[readEnd]];
                if (outputCharacters + excerpt.Length > MaximumCharacters && matches.Count > 0) break;
                if (excerpt.Length > MaximumCharacters) throw Error("knowledge_access_match_limit");
                matches.Add(new(snapshot.Artifacts[token.Document].ArtifactId, new(start, end), new(readStart, readEnd), excerpt));
                outputCharacters += excerpt.Length;
                // Advance by a complete scalar after an empty match, including at end-of-document.
                token = token with
                {
                    Position = match.Length > 0 ? match.Index + match.Length
                    : end + 1 < boundaries.Count ? boundaries[end + 1] : text.Length + 1L
                };
            }
        }
        catch (RegexMatchTimeoutException) { throw Error("knowledge_access_regex_timeout"); }
        var ended = token.Document >= snapshot.Artifacts.Count;
        return new()
        {
            State = "ready",
            SourceUid = source.Uid,
            SnapshotId = snapshot.Name,
            PublishedAt = snapshot.PublishedAt,
            Matches = matches,
            NextCursor = ended ? null : Seal(token),
            CursorExpiresAt = token.Expires,
            EndOfSelection = ended
        };
    }
    private static Regex Regex(string pattern, bool ignoreCase)
    {
        try
        {
            return new(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant
            | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(100));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { throw Error("knowledge_access_pattern_invalid"); }
    }
}
