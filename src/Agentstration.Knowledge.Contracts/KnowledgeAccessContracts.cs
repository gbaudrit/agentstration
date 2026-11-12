using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Knowledge.Contracts;

public sealed record KnowledgeCharacterRange(long Start, long End);

public sealed record KnowledgeReadRequest
{
    public string? SnapshotId { get; init; }
    public string? DocumentId { get; init; }
    public KnowledgeCharacterRange? Range { get; init; }
    public string? Cursor { get; init; }
    public string? PreparationId { get; init; }
    public int MaxCharacters { get; init; } = 4_000;
}

public sealed record KnowledgeRegexRequest
{
    public string? Pattern { get; init; }
    public bool IgnoreCase { get; init; } = true;
    public string? SnapshotId { get; init; }
    public string? Cursor { get; init; }
    public string? PreparationId { get; init; }
    public int Limit { get; init; } = 20;
    public int ContextCharacters { get; init; } = 160;
}

public sealed record KnowledgeDocumentQueryRequest
{
    public string? SnapshotId { get; init; }
    public string? Cursor { get; init; }
    public string? PreparationId { get; init; }
    public StorageQueryProfilePin? Profile { get; init; }
    public JsonElement? Query { get; init; }
    public int? PageSize { get; init; }
}

public sealed record KnowledgeStatusRequest(string? SnapshotId = null);
public sealed record KnowledgeAccessDocument(string DocumentId, string MediaType, long Bytes, bool TextReadable);
public sealed record KnowledgeReadChunk(string DocumentId, string MediaType, KnowledgeCharacterRange Range, string Content, bool EndOfDocument);
public sealed record KnowledgeRegexMatch(string DocumentId, KnowledgeCharacterRange MatchRange, KnowledgeCharacterRange ReadRange, string Excerpt);
public sealed record KnowledgeInputStatus(string BindingName, string? AcquisitionId, DateTimeOffset? AcquiredAt,
    bool Fresh, bool RefreshRequired, bool Required)
{
    public bool Direct { get; init; }
    public TimeSpan? AcquisitionTimeToLive { get; init; }
    public string? RefreshErrorCode { get; init; }
}

public sealed record KnowledgeAccessResult
{
    public required string State { get; init; }
    public required Guid SourceUid { get; init; }
    public string? SnapshotId { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? PreparationId { get; init; }
    public int? RetryAfterMilliseconds { get; init; }
    public string? ProjectionId { get; init; }
    public string? ErrorCode { get; init; }
    public IReadOnlyList<KnowledgeReadChunk> Chunks { get; init; } = [];
    public IReadOnlyList<KnowledgeRegexMatch> Matches { get; init; } = [];
    public string? NextCursor { get; init; }
    public DateTimeOffset? CursorExpiresAt { get; init; }
    public bool EndOfSelection { get; init; }
    public StorageDocumentPage? DocumentPage { get; init; }
    public StorageQueryProfilePin? QueryProfile { get; init; }
}

public sealed record KnowledgeAccessStatus
{
    public required Guid SourceUid { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
    public DateTimeOffset? ActiveExpiresAt { get; init; }
    public bool AllowExpiredOnRefreshFailure { get; init; }
    public string? ActiveSnapshotId { get; init; }
    public DateTimeOffset? ActivePublishedAt { get; init; }
    public string? LatestPublishedSnapshotId { get; init; }
    public DateTimeOffset? LatestPublishedAt { get; init; }
    public string? ComparedSnapshotId { get; init; }
    public bool? IsActive { get; init; }
    public bool? IsLatestPublished { get; init; }
    public bool PreparationRequired { get; init; }
    public string? ProjectionId { get; init; }
    public string? ErrorCode { get; init; }
    public IReadOnlyList<KnowledgeInputStatus> Inputs { get; init; } = [];
    public IReadOnlyList<KnowledgeAccessDocument> Documents { get; init; } = [];
}

public sealed record KnowledgeReadSessionResource : Resource
{
    public bool Ready { get; init; } = true;
    public required Guid SourceUid { get; init; }
    public required string SnapshotName { get; init; }
    public required Guid SnapshotUid { get; init; }
    public required Guid PrincipalId { get; init; }
    public required Guid TenantId { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
