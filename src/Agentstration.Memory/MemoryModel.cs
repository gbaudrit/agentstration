using Agentstration.Resources;

namespace Agentstration.Memory;

public readonly record struct MemoryRecordId(Guid Value)
{
    public static MemoryRecordId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public enum MemoryScopeKind { Agent, Shared }
public enum MemorySourceKind { Manual, Interaction, WorkItem, FlowRun, RuntimeRun }

public sealed record MemoryScope(MemoryScopeKind Kind, string Key)
{
    public static MemoryScope ForAgent(Guid agentUid) => new(MemoryScopeKind.Agent, agentUid.ToString("N"));
    public static MemoryScope Shared(string name) => new(MemoryScopeKind.Shared, name);
}

public sealed record MemoryProvenance(
    MemorySourceKind SourceKind,
    string? SourceId,
    string Reason,
    Guid CreatedByPrincipalId);

public sealed record MemoryRecord(
    MemoryRecordId Id,
    WorkspaceId WorkspaceId,
    MemoryScope Scope,
    string Content,
    IReadOnlyList<string> Tags,
    MemoryProvenance Provenance,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null);

public static class MemoryLimits
{
    public const int MaximumContentLength = 4_096;
    public const int MaximumRenderedContextLength = 16_384;
    public const int MaximumTags = 16;
    public const int MaximumTagLength = 64;
    public const int MaximumReasonLength = 512;
    public const int DefaultRetrievalCount = 10;
    public const int MaximumRetrievalCount = 20;
    public const int MaximumAdministrationPageSize = 100;
}

public sealed class MemoryValidationException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

public static class MemoryValidator
{
    public static MemoryRecord Validate(MemoryRecord record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Id.Value == Guid.Empty) throw Error("memory_id_required", "A Memory record identifier is required.");
        if (record.WorkspaceId.Value == Guid.Empty) throw Error("workspace_id_required", "A Workspace identifier is required.");
        ValidateScope(record.Scope);
        if (string.IsNullOrWhiteSpace(record.Content)) throw Error("memory_content_required", "Memory content is required.");
        if (record.Content.Length > MemoryLimits.MaximumContentLength) throw Error("memory_content_too_long", $"Memory content cannot exceed {MemoryLimits.MaximumContentLength} characters.");
        if (record.Tags.Count > MemoryLimits.MaximumTags) throw Error("memory_tags_too_many", $"Memory cannot have more than {MemoryLimits.MaximumTags} tags.");
        if (record.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > MemoryLimits.MaximumTagLength)) throw Error("memory_tag_invalid", $"Memory tags must be non-empty and at most {MemoryLimits.MaximumTagLength} characters.");
        if (record.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != record.Tags.Count) throw Error("memory_tag_duplicate", "Memory tags must be unique.");
        if (string.IsNullOrWhiteSpace(record.Provenance.Reason) || record.Provenance.Reason.Length > MemoryLimits.MaximumReasonLength) throw Error("memory_reason_invalid", $"A reason of at most {MemoryLimits.MaximumReasonLength} characters is required.");
        if (record.Provenance.CreatedByPrincipalId == Guid.Empty) throw Error("memory_creator_required", "The creating Principal is required.");
        if (record.Provenance.SourceKind == MemorySourceKind.Manual && record.Provenance.SourceId is not null) throw Error("memory_manual_source_invalid", "Manual Memory cannot declare a technical source identifier.");
        if (record.Provenance.SourceKind != MemorySourceKind.Manual && string.IsNullOrWhiteSpace(record.Provenance.SourceId)) throw Error("memory_source_required", "A non-manual Memory requires a source identifier.");
        if (record.Provenance.SourceId?.Length > 256) throw Error("memory_source_too_long", "A Memory source identifier cannot exceed 256 characters.");
        if (record.ExpiresAt is not null && record.ExpiresAt <= now) throw Error("memory_expiry_invalid", "Memory expiry must be in the future.");
        return record with
        {
            Content = record.Content.Trim(),
            Tags = record.Tags.Select(tag => tag.Trim()).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Provenance = record.Provenance with { Reason = record.Provenance.Reason.Trim(), SourceId = record.Provenance.SourceId?.Trim() }
        };
    }

    public static void ValidateScope(MemoryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!Enum.IsDefined(scope.Kind)) throw Error("memory_scope_kind_invalid", "The Memory scope kind is invalid.");
        if (string.IsNullOrWhiteSpace(scope.Key) || scope.Key.Length > 256) throw Error("memory_scope_key_invalid", "The Memory scope key must contain at most 256 characters.");
        if (scope.Kind == MemoryScopeKind.Agent && (!Guid.TryParseExact(scope.Key, "N", out var agentUid) || agentUid == Guid.Empty)) throw Error("memory_agent_scope_invalid", "An Agent Memory scope must use a non-empty stable Agent UID.");
        if (scope.Kind == MemoryScopeKind.Shared && !scope.Key.All(value => char.IsLetterOrDigit(value) || value is '-' or '_' or '.')) throw Error("memory_shared_scope_invalid", "A shared Memory scope name may contain letters, digits, '-', '_' and '.'.");
    }

    private static MemoryValidationException Error(string code, string message) => new(code, message);
}
