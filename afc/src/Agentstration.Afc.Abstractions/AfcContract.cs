using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Afc.Abstractions;

public static class AfcContract
{
    public const string Id = "agentstration.forge.contracts.mcp.v1";
    public const string Version = "1.0";
    public const string DisplayName = "AFC.mcp.v1";
    public const int MaximumRequestBytes = 131_072;
    public const int MaximumResultBytes = 262_144;
    public static IReadOnlyList<string> Tools { get; } = Array.AsReadOnly(new[] { "describe", "resolve", "read", "create", "observe" });
    public static IReadOnlyList<string> Operations { get; } = Array.AsReadOnly(new[] { "resolve", "read", "create", "observe" });
    public static JsonSerializerOptions Json { get; } = CreateOptions();

    public static string Digest(AfcTargetRef target, AfcCreateIntent intent) => "sha256:" + Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { target, intent }, Json)));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            AllowOutOfOrderMetadataProperties = true,
            NumberHandling = JsonNumberHandling.Strict,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 24
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

// Provider identity names a configured authority, not a vendor family.
public sealed record AfcTargetRef(string ProviderId, string TargetId, string BindingId, string Revision);
public sealed record AfcItemRef(string ProviderId, string TargetId, string RepositoryId, string NativeId);
public sealed record AfcCommitRef(string Name, string ObjectId);
public sealed record AfcTrackingRef(string ProviderId, string TargetId, string ItemId);
public sealed record AfcRepository(string ProviderId, string TargetId, string NativeId, string Name, Uri WebUrl,
    string DefaultRef, IReadOnlyList<AfcCommitRef> Refs, Uri? CloneUrl = null);
public sealed record AfcChangeRequest(AfcItemRef Identity, string DisplayId, string Revision, Uri WebUrl,
    string Title, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Body,
    string NativeState, bool Draft, bool Merged, AfcCommitRef Head, AfcCommitRef Base,
    IReadOnlyList<AfcTrackingRef> Tracking);

public enum AfcConcurrency { None, Observation, AtomicRevision }
public enum AfcRecoveryScope { None, Process, Durable }
public sealed record AfcLimits(int MaximumRefs = 16, int MaximumObservations = 32,
    int MaximumResultBytes = AfcContract.MaximumResultBytes);
public sealed record AfcCapabilities(IReadOnlyList<string> Operations, AfcConcurrency CreationConcurrency,
    AfcRecoveryScope Recovery, int ReceiptWindowSeconds, bool Reviews, bool Checks, AfcLimits Limits);
public sealed record AfcDeclaration(string ContractId, string Version, string ToolPrefix,
    IReadOnlyDictionary<string, string> SchemaDigests);
public sealed record AfcDescription(AfcDeclaration Declaration, AfcTargetRef Target,
    string ImplementationId, string ImplementationVersion, AfcCapabilities Capabilities);
public sealed record AfcInvocation<T>(AfcTargetRef Target, T Arguments);
public sealed record AfcDescribeRequest(string RequestId);
public sealed record AfcResolveRequest(string RequestId, IReadOnlyList<string> Refs);
public sealed record AfcReadRequest(string RequestId, string RepositoryId, string NativeId);
public sealed record AfcCreateIntent(string RepositoryId, string Title, AfcCommitRef Head, AfcCommitRef Base,
    bool Draft = true, string? Body = null, IReadOnlyList<AfcTrackingRef>? Tracking = null);
public enum AfcMutationMode { Execute, Recover }
public sealed record AfcCreateRequest(string RequestId, string OperationKey, string IntentDigest,
    AfcCreateIntent Intent, AfcMutationMode Mode = AfcMutationMode.Execute);
public enum AfcReceiptStatus { Applied, Rejected, Uncertain, Missing }
public sealed record AfcReceipt(string OperationKey, string IntentDigest, AfcReceiptStatus Status,
    AfcChangeRequest? ChangeRequest = null, string? ErrorCode = null);

public enum AfcReviewState { Unknown, Pending, Approved, ChangesRequested }
public enum AfcCheckState { Unknown, Pending, Success, Failure, Skipped }
public enum AfcObservationStatus { Unknown, Fresh, Stale }
public sealed record AfcReview(string NativeId, string NativeState, AfcReviewState State, string? ObjectId = null);
public sealed record AfcCheck(string NativeId, string NativeState, AfcCheckState State, string? ObjectId = null);
public sealed record AfcObserveRequest(string RequestId, string RepositoryId, string NativeId,
    string ExpectedHead, string ExpectedBase, bool Reviews = true, bool Checks = true);
public sealed record AfcObservations(AfcItemRef Identity, string HeadObjectId, string BaseObjectId,
    AfcObservationStatus Status, DateTimeOffset ObservedAt, bool ReviewsComplete, bool ChecksComplete,
    IReadOnlyList<AfcReview> Reviews, IReadOnlyList<AfcCheck> Checks);
public sealed record AfcAccessContext(AfcTargetRef Target, string Subject, string AuthorizationRevision,
    IReadOnlyList<string> Operations);
public sealed record AfcError(string Code);
public sealed record AfcReply<T>(string ContractId, string Version, string RequestId, T? Data = default, AfcError? Error = null);
public sealed class AfcException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public interface IAfcProvider
{
    string ImplementationId { get; }
    string ImplementationVersion { get; }
    AfcCapabilities Capabilities { get; }
    ValueTask<AfcRepository> ResolveAsync(AfcAccessContext context, AfcResolveRequest request, CancellationToken cancellationToken);
    ValueTask<AfcChangeRequest> ReadAsync(AfcAccessContext context, AfcReadRequest request, CancellationToken cancellationToken);
    ValueTask<AfcReceipt> CreateAsync(AfcAccessContext context, AfcCreateRequest request, CancellationToken cancellationToken);
    ValueTask<AfcObservations> ObserveAsync(AfcAccessContext context, AfcObserveRequest request, CancellationToken cancellationToken);
}
