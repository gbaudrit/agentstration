using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Atc.Abstractions;

public static class AtcContract
{
    public const string Id = "agentstration.tracking.contracts.mcp.v1";
    public const string Version = "1.0";
    public const int MaximumRequestBytes = 131_072;
    public const int MaximumResultBytes = 262_144;
    public static IReadOnlyList<string> Tools { get; } = Array.AsReadOnly(new[] { "describe", "list", "read", "mutate" });
    public static IReadOnlyList<string> Operations { get; } = Array.AsReadOnly(new[] { "list", "read", "create", "update", "comment", "relate", "transition" });
    public static JsonSerializerOptions Json { get; } = CreateOptions();

    public static string Digest(AtcTargetRef target, AtcChange change) => "sha256:" + Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { target, change }, Json)));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = false,
            AllowOutOfOrderMetadataProperties = true,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            NumberHandling = JsonNumberHandling.Strict,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 24
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

// ProviderId identifies a configured provider authority, not a vendor family.
public sealed record AtcTargetRef(string ProviderId, string TargetId, string BindingId, string Revision);
public sealed record AtcItemRef(string ProviderId, string TargetId, string ItemId);
public sealed record AtcInvocation<T>(AtcTargetRef Target, T Arguments);
public sealed record AtcDescribeRequest(string RequestId);
public sealed record AtcReadRequest(string RequestId, string ItemId);
public sealed record AtcListRequest(string RequestId, int Limit = 20, string? Cursor = null);
public sealed record AtcPage(IReadOnlyList<AtcItem> Items, string? Cursor = null);
public sealed record AtcNativeState(string Id, string? CompletionReason = null);
public enum AtcRelationKind { Parent, Child, Related, Blocks, BlockedBy }
public sealed record AtcRelation(AtcRelationKind Kind, AtcItemRef Item);
public sealed record AtcComment(string Id, string Text);
public sealed record AtcTransition(string Id, string DestinationState);
public sealed record AtcItem(AtcItemRef Identity, string Revision, string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Body,
    AtcNativeState State, IReadOnlyList<string> Classifications, IReadOnlyList<AtcRelation> Relations,
    bool RelationsComplete, IReadOnlyList<AtcComment> Comments, IReadOnlyList<AtcTransition> Transitions);

public enum AtcConcurrency { None, Observation, AtomicRevision }
public enum AtcRecoveryScope { None, Process, Durable }
public sealed record AtcLimits(int MaximumPageSize = 100, int MaximumResultBytes = AtcContract.MaximumResultBytes);
public sealed record AtcCapabilities(IReadOnlyList<string> Operations, IReadOnlyList<string> UpdateFields,
    IReadOnlyList<AtcRelationKind> RelationKinds, AtcConcurrency Concurrency, AtcRecoveryScope Recovery,
    int ReceiptWindowSeconds, AtcLimits Limits);
public sealed record AtcDeclaration(string ContractId, string Version, string ToolPrefix,
    IReadOnlyDictionary<string, string> SchemaDigests);
public sealed record AtcDescription(AtcDeclaration Declaration, AtcTargetRef Target,
    string ImplementationId, string ImplementationVersion, AtcCapabilities Capabilities);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(AtcCreate), "create")]
[JsonDerivedType(typeof(AtcUpdate), "update")]
[JsonDerivedType(typeof(AtcAddComment), "comment")]
[JsonDerivedType(typeof(AtcAddRelation), "relate")]
[JsonDerivedType(typeof(AtcApplyTransition), "transition")]
public abstract record AtcChange;
public sealed record AtcCreate(string Title, string? Body = null) : AtcChange;
public sealed record AtcUpdate(string ItemId, string ExpectedRevision, string? Title = null,
    string? Body = null, IReadOnlyList<string>? AddClassifications = null) : AtcChange;
public sealed record AtcAddComment(string ItemId, string ExpectedRevision, string Text) : AtcChange;
public sealed record AtcAddRelation(string ItemId, string ExpectedRevision, AtcRelation Relation) : AtcChange;
public sealed record AtcApplyTransition(string ItemId, string ExpectedRevision, string TransitionId) : AtcChange;
public enum AtcMutationMode { Execute, Recover }
public sealed record AtcMutationRequest(string RequestId, string OperationKey, string IntentDigest,
    AtcChange Change, AtcMutationMode Mode = AtcMutationMode.Execute);
public enum AtcMutationStatus { Applied, Rejected, Uncertain, Missing }
public sealed record AtcReceipt(string OperationKey, string IntentDigest, AtcMutationStatus Status,
    AtcItem? Item = null, string? ErrorCode = null);
public sealed record AtcError(string Code);
public sealed record AtcReply<T>(string ContractId, string Version, string RequestId, T? Data = default, AtcError? Error = null);
public sealed class AtcException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

// Authenticated host context is never accepted from Tool business arguments.
public sealed record AtcAccessContext(AtcTargetRef Target, string Subject, string AuthorizationRevision,
    IReadOnlyList<string> Operations, IReadOnlyList<string> RelatedTargetIds);

public interface IAtcProvider
{
    string ImplementationId { get; }
    string ImplementationVersion { get; }
    AtcCapabilities Capabilities { get; }
    ValueTask<AtcItem> ReadAsync(AtcAccessContext context, AtcReadRequest request, CancellationToken cancellationToken);
    ValueTask<AtcPage> ListAsync(AtcAccessContext context, AtcListRequest request, CancellationToken cancellationToken);
    ValueTask<AtcReceipt> MutateAsync(AtcAccessContext context, AtcMutationRequest request, CancellationToken cancellationToken);
}
