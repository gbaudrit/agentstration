using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Asc.Abstractions;

public static class AscContract
{
    public const string Identity = "agentstration.storage.contracts.mcp.v1";
    public const string DisplayName = "ASC.mcp.v1";
    public const string McpProtocolVersion = "2026-07-28";
    public const string Version = "1.0";
    public const string Profile = "asc.content/1";
    public const int MaxContentBytes = 65_536;
    public const int MaxPageSize = 100;
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            MaxDepth = 32,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

// Callers select a logical target; only the extension resolves and authorizes it.
public sealed record AscStoreRef(string StoreId, string BindingId, string Revision);
public sealed record AscInvocation<T>(AscStoreRef Store, T Arguments);
public sealed record AscAccessPolicy(
    bool CanList, bool CanStat, bool CanRead, bool CanWrite,
    string Prefix, int MaxReadBytes, int MaxWriteBytes, int MaxPageSize,
    string? KeyFrom = null, string? KeyBefore = null);
public sealed record AscAccessContext(AscStoreRef Store, string Subject, string AuthorizationRevision, AscAccessPolicy Policy);
public sealed record AscCapabilities(bool List, bool Stat, bool Read, bool Write, bool Delete, bool StableVersions, bool Retention);
public sealed record AscDescription(
    string AscVersion, string Profile, string ProviderId, string ProviderVersion,
    AscStoreRef Store, AscCapabilities Capabilities, AscAccessPolicy Policy, JsonElement ConfigurationSchema, AscTransferCapabilities Transfers, AscSessionLimits Limits);
public sealed record AscObject(string Key, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version, long Length, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Sha256, string Reference, string? ETag = null, AscContentMetadata? Metadata = null);
public sealed record AscListRequest(string RequestId, string Prefix, int Limit, string? Cursor = null);
public sealed record AscReferenceRequest(string RequestId, string Reference);
public sealed record AscDescribeRequest(string RequestId);
public sealed record AscListResult(IReadOnlyList<AscObject> Objects, string? Cursor = null);
public sealed record AscError(string Code, string Message, string? SessionRef = null);
public sealed record AscReply<T>(string AscVersion, string RequestId, T? Data = default, AscError? Error = null);

public sealed class AscException(string code, string message, string? sessionRef = null) : Exception(message)
{
    public string? SchemaPath { get; init; }
    public string? SchemaKeyword { get; init; }
    public string Code { get; } = code;
    public string? SessionRef { get; } = sessionRef;
}

// Provider contracts carry logical identity and effective restrictions on every call.
// Implementations MUST enforce this context even when several stores share physical placement.
public sealed record AscProviderObject(string Key, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version, long Length, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Sha256, string? ETag = null, AscContentMetadata? Metadata = null);
public sealed record AscProviderPage(IReadOnlyList<AscProviderObject> Objects, string? NextKey);
public sealed record AscSessionLimits(int MaximumSessions = 64, int MaximumReceipts = 8192,
    long MaximumReplayBytes = 64 * 1024 * 1024, int ReceiptWindowSeconds = 30)
{
    public int MaximumSessionSeconds => 300;
    public int MaximumMetadataBytes => 8192;
    public int MaximumArgumentCharacters => 100_000;
    public int MaximumResultCharacters => 200_000;
}

public sealed record AscTransferCapabilities(
    bool ProgressiveRead = true, bool ProgressiveWrite = true, bool Replace = false, bool Append = false,
    bool VersionCondition = false, bool ETagConditions = false, bool Metadata = false,
    bool AtomicPublication = false, long MaximumObjectBytes = 1_073_741_824, int DefaultChunkSizeBytes = 16_384, bool CreateOnly = true, bool EmptyWrite = true);
public sealed record AscByteRange(long Offset, long? Length = null);
public sealed record AscReadSelection(string Reference, AscByteRange? Range = null, int? PreferredChunkSizeBytes = null,
    string? Encoding = null);
public sealed record AscContentMetadata(string? ContentType = null, string? ContentEncoding = null,
    string? ContentDisposition = null, string? CacheControl = null, IReadOnlyDictionary<string, string>? UserMetadata = null);
public sealed record AscWriteOptions(string Key, string Mode = "create", string? ExpectedVersion = null,
    string? IfMatch = null, string? IfNoneMatch = null, AscContentMetadata? Metadata = null,
    long? ExpectedLength = null, string? ExpectedSha256 = null);
public sealed record AscReadBlobRequest(string RequestId, long Sequence = 0, string? SessionRef = null,
    AscReadSelection? Selection = null, DateTimeOffset? NotAfter = null);
public sealed record AscReadTextRequest(string RequestId, long Sequence = 0, string? SessionRef = null,
    AscReadSelection? Selection = null, DateTimeOffset? NotAfter = null);
public sealed record AscWriteBlobRequest(string RequestId, long Sequence, bool Final, string ContentBase64,
    string? SessionRef = null, AscWriteOptions? Options = null, DateTimeOffset? NotAfter = null);
public sealed record AscWriteTextRequest(string RequestId, long Sequence, bool Final, string Content,
    string? SessionRef = null, AscWriteOptions? Options = null, DateTimeOffset? NotAfter = null, string? Encoding = null);
public sealed record AscReadBlobResult(string SessionRef, long Sequence, bool Final, AscObject Item, long Offset,
    int ByteLength, long SelectedOffset, long SelectedLength, string ContentBase64);
public sealed record AscReadTextResult(string SessionRef, long Sequence, bool Final, AscObject Item, long Offset,
    int ByteLength, long SelectedOffset, long SelectedLength, string Content);
public sealed record AscWriteReceipt(AscObject Item, long WrittenBytes, string Sha256);
public sealed record AscWriteResult(string SessionRef, long Sequence, bool Final, long WrittenBytes, AscWriteReceipt? Receipt = null);
public sealed record AscSessionRequest(string RequestId, string SessionRef);
public sealed record AscSessionResult(string SessionRef, string State, AscWriteReceipt? Receipt = null);

/// <summary>The selection has already been authorized; provider must independently enforce context and identity.</summary>
public sealed class AscReadHandle(AscProviderObject item, Stream content, long offset, long length) : IAsyncDisposable
{
    public AscProviderObject Item { get; } = item;
    public Stream Content { get; } = content;
    public long Offset { get; } = offset;
    public long Length { get; } = length;
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
public interface IAscWriteHandle : IAsyncDisposable
{
    Stream Content { get; }
    ValueTask<AscProviderObject> CompleteAsync(CancellationToken cancellationToken);
    /// <summary>Releases work without promising rollback or absence of publication.</summary>
    ValueTask AbortAsync(CancellationToken cancellationToken);
}
public interface IAscObjectReader
{
    string ProviderId { get; }
    string ProviderVersion { get; }
    JsonElement ConfigurationSchema { get; }
    AscTransferCapabilities Transfers { get; }
    ValueTask<AscProviderPage> ListAsync(AscAccessContext context, string prefix, string? afterKey, int limit, CancellationToken cancellationToken);
    ValueTask<AscProviderObject> StatAsync(AscAccessContext context, string key, string? version, string? etag, CancellationToken cancellationToken);
    ValueTask<AscReadHandle> OpenReadAsync(AscAccessContext context, string key, string? version, string? etag, AscByteRange? range, CancellationToken cancellationToken);
}
public interface IAscObjectWriter
{
    ValueTask<IAscWriteHandle> OpenWriteAsync(AscAccessContext context, AscWriteOptions options, CancellationToken cancellationToken);
}
