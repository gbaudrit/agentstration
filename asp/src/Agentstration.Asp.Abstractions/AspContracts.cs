using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Asp.Abstractions;

public static class AspProtocol
{
    public const string Version = "1.0";
    public const string Profile = "asp.content/1";
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
public sealed record AspStoreRef(string StoreId, string BindingId, string Revision);
public sealed record AspInvocation<T>(AspStoreRef Store, T Arguments);
public sealed record AspAccessPolicy(
    bool CanList, bool CanStat, bool CanRead, bool CanWrite,
    string Prefix, int MaxReadBytes, int MaxWriteBytes, int MaxPageSize,
    string? KeyFrom = null, string? KeyBefore = null);
public sealed record AspAccessContext(AspStoreRef Store, string Subject, string AuthorizationRevision, AspAccessPolicy Policy);
public sealed record AspCapabilities(bool List, bool Stat, bool Read, bool Write, bool Delete, bool StableVersions, bool Retention);
public sealed record AspDescription(
    string AspVersion, string Profile, string ProviderId, string ProviderVersion,
    AspStoreRef Store, AspCapabilities Capabilities, AspAccessPolicy Policy, JsonElement ConfigurationSchema, AspTransferCapabilities Transfers, AspSessionLimits Limits);
public sealed record AspObject(string Key, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version, long Length, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Sha256, string Reference, string? ETag = null, AspContentMetadata? Metadata = null);
public sealed record AspListRequest(string RequestId, string Prefix, int Limit, string? Cursor = null);
public sealed record AspReferenceRequest(string RequestId, string Reference);
public sealed record AspDescribeRequest(string RequestId);
public sealed record AspListResult(IReadOnlyList<AspObject> Objects, string? Cursor = null);
public sealed record AspError(string Code, string Message, string? SessionRef = null);
public sealed record AspReply<T>(string AspVersion, string RequestId, T? Data = default, AspError? Error = null);

public sealed class AspException(string code, string message, string? sessionRef = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? SessionRef { get; } = sessionRef;
}

// Provider contracts carry logical identity and effective restrictions on every call.
// Implementations MUST enforce this context even when several stores share physical placement.
public sealed record AspProviderObject(string Key, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version, long Length, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Sha256, string? ETag = null, AspContentMetadata? Metadata = null);
public sealed record AspProviderPage(IReadOnlyList<AspProviderObject> Objects, string? NextKey);
public sealed record AspSessionLimits(int MaximumSessions = 64, int MaximumReceipts = 8192,
    long MaximumReplayBytes = 64 * 1024 * 1024, int ReceiptWindowSeconds = 30)
{
    public int MaximumSessionSeconds => 300;
    public int MaximumMetadataBytes => 8192;
    public int MaximumArgumentCharacters => 100_000;
    public int MaximumResultCharacters => 200_000;
}

public sealed record AspTransferCapabilities(
    bool ProgressiveRead = true, bool ProgressiveWrite = true, bool Replace = false, bool Append = false,
    bool VersionCondition = false, bool ETagConditions = false, bool Metadata = false,
    bool AtomicPublication = false, long MaximumObjectBytes = 1_073_741_824, int DefaultChunkSizeBytes = 16_384, bool CreateOnly = true, bool EmptyWrite = true);
public sealed record AspByteRange(long Offset, long? Length = null);
public sealed record AspReadSelection(string Reference, AspByteRange? Range = null, int? PreferredChunkSizeBytes = null,
    string? Encoding = null);
public sealed record AspContentMetadata(string? ContentType = null, string? ContentEncoding = null,
    string? ContentDisposition = null, string? CacheControl = null, IReadOnlyDictionary<string, string>? UserMetadata = null);
public sealed record AspWriteOptions(string Key, string Mode = "create", string? ExpectedVersion = null,
    string? IfMatch = null, string? IfNoneMatch = null, AspContentMetadata? Metadata = null,
    long? ExpectedLength = null, string? ExpectedSha256 = null);
public sealed record AspReadBlobRequest(string RequestId, long Sequence = 0, string? SessionRef = null,
    AspReadSelection? Selection = null, DateTimeOffset? NotAfter = null);
public sealed record AspReadTextRequest(string RequestId, long Sequence = 0, string? SessionRef = null,
    AspReadSelection? Selection = null, DateTimeOffset? NotAfter = null);
public sealed record AspWriteBlobRequest(string RequestId, long Sequence, bool Final, string ContentBase64,
    string? SessionRef = null, AspWriteOptions? Options = null, DateTimeOffset? NotAfter = null);
public sealed record AspWriteTextRequest(string RequestId, long Sequence, bool Final, string Content,
    string? SessionRef = null, AspWriteOptions? Options = null, DateTimeOffset? NotAfter = null, string? Encoding = null);
public sealed record AspReadBlobResult(string SessionRef, long Sequence, bool Final, AspObject Item, long Offset,
    int ByteLength, long SelectedOffset, long SelectedLength, string ContentBase64);
public sealed record AspReadTextResult(string SessionRef, long Sequence, bool Final, AspObject Item, long Offset,
    int ByteLength, long SelectedOffset, long SelectedLength, string Content);
public sealed record AspWriteReceipt(AspObject Item, long WrittenBytes, string Sha256);
public sealed record AspWriteResult(string SessionRef, long Sequence, bool Final, long WrittenBytes, AspWriteReceipt? Receipt = null);
public sealed record AspSessionRequest(string RequestId, string SessionRef);
public sealed record AspSessionResult(string SessionRef, string State, AspWriteReceipt? Receipt = null);

/// <summary>The selection has already been authorized; provider must independently enforce context and identity.</summary>
public sealed class AspReadHandle(AspProviderObject item, Stream content, long offset, long length) : IAsyncDisposable
{
    public AspProviderObject Item { get; } = item;
    public Stream Content { get; } = content;
    public long Offset { get; } = offset;
    public long Length { get; } = length;
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
public interface IAspWriteHandle : IAsyncDisposable
{
    Stream Content { get; }
    ValueTask<AspProviderObject> CompleteAsync(CancellationToken cancellationToken);
    /// <summary>Releases work without promising rollback or absence of publication.</summary>
    ValueTask AbortAsync(CancellationToken cancellationToken);
}
public interface IAspObjectReader
{
    string ProviderId { get; }
    string ProviderVersion { get; }
    JsonElement ConfigurationSchema { get; }
    AspTransferCapabilities Transfers { get; }
    ValueTask<AspProviderPage> ListAsync(AspAccessContext context, string prefix, string? afterKey, int limit, CancellationToken cancellationToken);
    ValueTask<AspProviderObject> StatAsync(AspAccessContext context, string key, string? version, string? etag, CancellationToken cancellationToken);
    ValueTask<AspReadHandle> OpenReadAsync(AspAccessContext context, string key, string? version, string? etag, AspByteRange? range, CancellationToken cancellationToken);
}
public interface IAspObjectWriter
{
    ValueTask<IAspWriteHandle> OpenWriteAsync(AspAccessContext context, AspWriteOptions options, CancellationToken cancellationToken);
}
