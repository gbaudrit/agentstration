using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Agentstration.Afc.Abstractions;

public static class AfcValidation
{
    public static void Require([DoesNotReturnIf(false)] bool condition, string code)
    {
        if (!condition) throw new AfcException(code);
    }

    public static void Text(string? value, int maximum = 128) => Require(
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl), "invalid_request");

    public static void Target(AfcTargetRef? target)
    {
        Require(target is not null, "invalid_request");
        Text(target.ProviderId); Text(target.TargetId); Text(target.BindingId); Text(target.Revision);
    }

    public static void Prefix(string? prefix) => Require(prefix is { Length: > 0 and <= 64 }
        && prefix.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'), "incompatible_mapping");

    public static void Revision(string? version) => Require(version is not null && version.StartsWith("1.", StringComparison.Ordinal)
        && version.Length <= 16 && int.TryParse(version.AsSpan(2), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var minor) && minor >= 0
        && version == $"1.{minor}", "incompatible_contract");

    public static T Parse<T>(JsonElement value)
    {
        try
        {
            UniqueProperties(value);
            return value.Deserialize<T>(AfcContract.Json) ?? throw new AfcException("invalid_request");
        }
        catch (JsonException) { throw new AfcException("invalid_request"); }
    }

    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "invalid_request");
                UniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueProperties(item);
    }

    public static void Capabilities(AfcCapabilities? value)
    {
        Require(value is not null && value.Operations is { Count: > 0 and <= 4 }
            && value.Operations.All(AfcContract.Operations.Contains)
            && value.Operations.Distinct(StringComparer.Ordinal).Count() == value.Operations.Count
            && Enum.IsDefined(value.CreationConcurrency) && Enum.IsDefined(value.Recovery), "invalid_capabilities");
        Require(value.ReceiptWindowSeconds is >= 0 and <= 86_400
            && (value.Recovery == AfcRecoveryScope.None ? value.ReceiptWindowSeconds == 0 : value.ReceiptWindowSeconds > 0)
            && (value.Operations.Contains("observe") == (value.Reviews || value.Checks))
            && value.Limits is
            {
                MaximumRefs: > 0 and <= 16, MaximumObservations: > 0 and <= 32,
                MaximumResultBytes: >= 4096 and <= AfcContract.MaximumResultBytes
            }, "invalid_capabilities");
    }

    public static void Context(AfcAccessContext context)
    {
        Target(context.Target); Text(context.Subject); Text(context.AuthorizationRevision);
        Require(context.Operations is { Count: <= 4 } && context.Operations.All(AfcContract.Operations.Contains)
            && context.Operations.Distinct(StringComparer.Ordinal).Count() == context.Operations.Count, "forbidden");
    }

    public static void Hash(string? value) => Require(value is { Length: 40 or 64 }
        && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'), "invalid_revision");

    public static void Commit(AfcCommitRef? value)
    {
        Require(value is not null, "invalid_request");
        Text(value.Name, 256); Hash(value.ObjectId);
    }

    public static void ResolveRequest(AfcResolveRequest request, AfcCapabilities caps)
    {
        Text(request.RequestId);
        Require(request.Refs is not null && request.Refs.Count <= caps.Limits.MaximumRefs
            && request.Refs.Distinct(StringComparer.Ordinal).Count() == request.Refs.Count, "invalid_request");
        foreach (var name in request.Refs) Text(name, 256);
    }

    public static void ReadRequest(AfcReadRequest request)
    {
        Text(request.RequestId); Text(request.RepositoryId); Text(request.NativeId);
    }

    public static void CreateRequest(AfcTargetRef target, AfcCreateRequest request, AfcCapabilities caps)
    {
        Text(request.RequestId); Text(request.OperationKey);
        Require(Enum.IsDefined(request.Mode) && request.Intent is not null, "invalid_request");
        Require(caps.Operations.Contains("create") && (request.Mode != AfcMutationMode.Recover || caps.Recovery != AfcRecoveryScope.None), "unsupported_operation");
        var intent = request.Intent;
        Text(intent.RepositoryId); Text(intent.Title, 512); Commit(intent.Head); Commit(intent.Base);
        Require(intent.Head.Name != intent.Base.Name && (intent.Body is null || intent.Body.Length <= 32_768), "invalid_request");
        Tracking(intent.Tracking ?? []);
        Require(request.IntentDigest == AfcContract.Digest(target, intent), "intent_conflict");
    }

    public static void ObserveRequest(AfcObserveRequest request, AfcCapabilities caps)
    {
        Text(request.RequestId); Text(request.RepositoryId); Text(request.NativeId);
        Hash(request.ExpectedHead); Hash(request.ExpectedBase);
        Require((request.Reviews || request.Checks) && (!request.Reviews || caps.Reviews)
            && (!request.Checks || caps.Checks) && caps.Operations.Contains("observe"), "unsupported_operation");
    }

    private static void Identity(AfcItemRef? value, AfcTargetRef target, string repository, string? nativeId = null)
    {
        Require(value is not null && value.ProviderId == target.ProviderId && value.TargetId == target.TargetId
            && value.RepositoryId == repository && (nativeId is null || value.NativeId == nativeId), "invalid_result");
        Text(value.ProviderId); Text(value.TargetId); Text(value.RepositoryId); Text(value.NativeId);
    }

    private static void Tracking(IReadOnlyList<AfcTrackingRef> values)
    {
        Require(values.Count <= 16 && values.Distinct().Count() == values.Count, "limit_exceeded");
        foreach (var value in values)
        {
            Require(value is not null, "invalid_request");
            Text(value.ProviderId); Text(value.TargetId); Text(value.ItemId);
        }
    }

    private static void Url(Uri? value, bool clone = false) => Require(value is { IsAbsoluteUri: true }
        && value.OriginalString.Length <= 2048 && value.Fragment.Length == 0 && value.Query.Length == 0
        && (value.Scheme is "https" or "http" && value.UserInfo.Length == 0
            || clone && value.Scheme == "ssh" && !value.UserInfo.Contains(':', StringComparison.Ordinal)), "invalid_result");

    public static void Repository(AfcRepository? value, AfcTargetRef target, AfcResolveRequest request, AfcCapabilities caps)
    {
        Require(value is not null && value.ProviderId == target.ProviderId && value.TargetId == target.TargetId, "invalid_result");
        Text(value.NativeId); Text(value.Name, 512); Text(value.DefaultRef, 256); Url(value.WebUrl);
        if (value.CloneUrl is not null) Url(value.CloneUrl, clone: true);
        Require(value.Refs is { Count: > 0 } && value.Refs.Count <= caps.Limits.MaximumRefs
            && value.Refs.All(r => r is not null)
            && value.Refs.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() == value.Refs.Count, "invalid_result");
        foreach (var commit in value.Refs) Commit(commit);
        IReadOnlyList<string> expected = request.Refs.Count == 0 ? [value.DefaultRef] : request.Refs;
        Require(value.Refs.Select(r => r.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected), "incomplete_observation");
    }

    public static void ChangeRequest(AfcChangeRequest? value, AfcTargetRef target, string repository, string? nativeId = null)
    {
        Require(value is not null, "invalid_result"); Identity(value.Identity, target, repository, nativeId);
        Text(value.DisplayId); Text(value.Revision); Url(value.WebUrl); Text(value.Title, 512); Text(value.NativeState);
        Commit(value.Head); Commit(value.Base);
        Require(value.Body is null || value.Body.Length <= 32_768, "invalid_result");
        Require(!value.Merged || !value.Draft, "invalid_result");
        Require(value.Tracking is not null, "invalid_result"); Tracking(value.Tracking);
    }

    public static void Receipt(AfcReceipt? value, AfcTargetRef target, AfcCreateRequest request)
    {
        Require(value is not null && Enum.IsDefined(value.Status) && value.OperationKey == request.OperationKey
            && value.IntentDigest == request.IntentDigest, "invalid_result");
        Require(value.Status switch
        {
            AfcReceiptStatus.Applied => value.ChangeRequest is not null && value.ErrorCode is null,
            AfcReceiptStatus.Rejected => value.ChangeRequest is null && Code(value.ErrorCode),
            AfcReceiptStatus.Uncertain => Code(value.ErrorCode),
            AfcReceiptStatus.Missing => request.Mode == AfcMutationMode.Recover && value.ChangeRequest is null && value.ErrorCode is null,
            _ => false
        }, "invalid_result");
        if (value.ChangeRequest is not { } item) return;
        ChangeRequest(item, target, request.Intent.RepositoryId);
        if (value.Status != AfcReceiptStatus.Applied) return;
        Require(item.Head == request.Intent.Head && item.Base == request.Intent.Base
            && item.Title == request.Intent.Title && item.Body == request.Intent.Body && item.Draft == request.Intent.Draft
            && !item.Merged && item.Tracking.SequenceEqual(request.Intent.Tracking ?? []), "invalid_result");
    }

    public static void Observations(AfcObservations? value, AfcTargetRef target, AfcObserveRequest request, AfcCapabilities caps)
    {
        Require(value is not null && Enum.IsDefined(value.Status), "invalid_result");
        Identity(value.Identity, target, request.RepositoryId, request.NativeId);
        Hash(value.HeadObjectId); Hash(value.BaseObjectId);
        Require(value.ObservedAt.Offset == TimeSpan.Zero && value.ObservedAt != default
            && value.Reviews is not null && value.Checks is not null
            && value.Reviews.Count <= caps.Limits.MaximumObservations && value.Checks.Count <= caps.Limits.MaximumObservations,
            "invalid_result");
        Require((request.Reviews || !value.ReviewsComplete && value.Reviews.Count == 0)
            && (request.Checks || !value.ChecksComplete && value.Checks.Count == 0), "invalid_result");
        if (value.Status == AfcObservationStatus.Fresh)
            Require(value.HeadObjectId == request.ExpectedHead && value.BaseObjectId == request.ExpectedBase
                && (!request.Reviews || value.ReviewsComplete) && (!request.Checks || value.ChecksComplete), "incomplete_observation");
        if (value.Status == AfcObservationStatus.Stale)
            Require(value.HeadObjectId != request.ExpectedHead || value.BaseObjectId != request.ExpectedBase, "invalid_result");
        foreach (var review in value.Reviews)
        {
            Require(review is not null && Enum.IsDefined(review.State), "invalid_result");
            Text(review.NativeId); Text(review.NativeState);
            if (review.ObjectId is not null) Hash(review.ObjectId);
        }
        foreach (var check in value.Checks)
        {
            Require(check is not null && Enum.IsDefined(check.State), "invalid_result");
            Text(check.NativeId); Text(check.NativeState);
            if (check.ObjectId is not null) Hash(check.ObjectId);
        }
        if (value.Status == AfcObservationStatus.Fresh)
            Require(value.Reviews.All(r => r.ObjectId == request.ExpectedHead)
                && value.Checks.All(c => c.ObjectId == request.ExpectedHead), "incomplete_observation");
        Require(value.Reviews.Select(r => r.NativeId).Distinct(StringComparer.Ordinal).Count() == value.Reviews.Count
            && value.Checks.Select(c => c.NativeId).Distinct(StringComparer.Ordinal).Count() == value.Checks.Count, "invalid_result");
    }

    public static bool Code(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
    public static void Bytes(JsonElement value, int limit) => Require(Encoding.UTF8.GetByteCount(value.GetRawText()) <= limit, "limit_exceeded");
}
