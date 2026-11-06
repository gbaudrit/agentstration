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

// These identities are resolved by the host, never accepted in Tool business arguments.
public sealed record AspStoreRef(string StoreId, string BindingId, string Revision);
public sealed record AspAccessPolicy(
    bool CanList, bool CanStat, bool CanRead, bool CanWrite,
    string Prefix, int MaxReadBytes, int MaxWriteBytes, int MaxPageSize,
    string? KeyFrom = null, string? KeyBefore = null);
public sealed record AspAccessContext(AspStoreRef Store, string Subject, string AuthorizationRevision, AspAccessPolicy Policy);
public sealed record AspCapabilities(bool List, bool Stat, bool Read, bool Write, bool Delete, bool StableVersions, bool Retention);
public sealed record AspDescription(
    string AspVersion, string Profile, string ProviderId, string ProviderVersion,
    AspStoreRef Store, AspCapabilities Capabilities, AspAccessPolicy Policy, JsonElement ConfigurationSchema);
public sealed record AspObject(string Key, string Version, long Length, string Sha256, string Reference);
public sealed record AspListRequest(string RequestId, string Prefix, int Limit, string? Cursor = null);
public sealed record AspReferenceRequest(string RequestId, string Reference);
public sealed record AspReadRequest(string RequestId, string Reference, long Offset, int MaxBytes);
public sealed record AspWriteRequest(string RequestId, DateTimeOffset NotAfter, string Key, string ContentBase64, string? ExpectedVersion = null);
public sealed record AspDescribeRequest(string RequestId);
public sealed record AspListResult(IReadOnlyList<AspObject> Objects, string? Cursor = null);
public sealed record AspReadResult(AspObject Item, long Offset, string ContentBase64, bool EndOfObject);
public sealed record AspWriteResult(AspObject Item);
public sealed record AspError(string Code, string Message);
public sealed record AspReply<T>(string AspVersion, string RequestId, T? Data = default, AspError? Error = null);

public sealed class AspException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Provider contracts carry logical identity and effective restrictions on every call.
// Implementations MUST enforce this context even when several stores share physical placement.
public sealed record AspProviderObject(string Key, string Version, long Length, string Sha256);
public sealed record AspProviderPage(IReadOnlyList<AspProviderObject> Objects, string? NextKey);
public sealed record AspProviderRead(AspProviderObject Item, byte[] Content, bool EndOfObject);
public sealed record AspProviderWrite(string RequestId, DateTimeOffset NotAfter, string Key, byte[] Content, string? ExpectedVersion);

public interface IAspObjectReader
{
    string ProviderId { get; }
    string ProviderVersion { get; }
    JsonElement ConfigurationSchema { get; }
    ValueTask<AspProviderPage> ListAsync(AspAccessContext context, string prefix, string? afterKey, int limit, CancellationToken cancellationToken);
    ValueTask<AspProviderObject> StatAsync(AspAccessContext context, string key, string version, CancellationToken cancellationToken);
    ValueTask<AspProviderRead> ReadAsync(AspAccessContext context, string key, string version, long offset, int maxBytes, CancellationToken cancellationToken);
}

// Atomic conditional writes and a bounded idempotency ledger are provider responsibilities.
// Implement this interface only when the guarantee can be upheld by the backend.
public interface IAspObjectWriter
{
    ValueTask<AspProviderObject> WriteAsync(AspAccessContext context, AspProviderWrite request, CancellationToken cancellationToken);
}
