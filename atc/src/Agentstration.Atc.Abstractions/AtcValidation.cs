using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Agentstration.Atc.Abstractions;

public static class AtcValidation
{
    public static void Require([DoesNotReturnIf(false)] bool condition, string code)
    {
        if (!condition) throw new AtcException(code);
    }

    public static void Text(string? value, int maximum = 128) => Require(
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl), "invalid_request");

    public static void Target(AtcTargetRef? target)
    {
        Require(target is not null, "invalid_request");
        Text(target.ProviderId); Text(target.TargetId); Text(target.BindingId); Text(target.Revision);
    }

    public static void Prefix(string prefix) => Require(prefix is { Length: > 0 and <= 64 }
        && prefix.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'), "invalid_mapping");

    public static void Revision(string version) => Require(version is not null && version.StartsWith("1.", StringComparison.Ordinal)
        && version.Length <= 16 && int.TryParse(version.AsSpan(2), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var minor) && minor >= 0
        && version == $"1.{minor}", "incompatible_contract");

    public static T Parse<T>(JsonElement json)
    {
        try
        {
            NoDuplicateProperties(json);
            return json.Deserialize<T>(AtcContract.Json) ?? throw new AtcException("invalid_request");
        }
        catch (JsonException) { throw new AtcException("invalid_request"); }
    }

    private static void NoDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "invalid_request");
                NoDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) NoDuplicateProperties(item);
    }

    public static void Capabilities(AtcCapabilities? value)
    {
        Require(value is not null && value.Operations is { Count: > 0 and <= 7 }
            && value.Operations.Distinct(StringComparer.Ordinal).Count() == value.Operations.Count
            && value.Operations.All(AtcContract.Operations.Contains), "invalid_capabilities");
        Require(value.UpdateFields is { Count: <= 3 } && value.UpdateFields.Distinct(StringComparer.Ordinal).Count() == value.UpdateFields.Count
            && value.UpdateFields.All(f => f is "title" or "body" or "classifications.add"), "invalid_capabilities");
        Require(value.RelationKinds is { Count: <= 5 } && value.RelationKinds.All(Enum.IsDefined)
            && value.RelationKinds.Distinct().Count() == value.RelationKinds.Count
            && Enum.IsDefined(value.Concurrency) && Enum.IsDefined(value.Recovery), "invalid_capabilities");
        Require(value.ReceiptWindowSeconds is >= 0 and <= 86_400
            && (value.Recovery == AtcRecoveryScope.None ? value.ReceiptWindowSeconds == 0 : value.ReceiptWindowSeconds > 0), "invalid_capabilities");
        Require(value.Limits is { MaximumPageSize: > 0 and <= 100, MaximumResultBytes: >= 4096 and <= AtcContract.MaximumResultBytes }, "invalid_capabilities");
        Require((value.UpdateFields.Count > 0) == value.Operations.Contains("update")
            && (value.RelationKinds.Count == 0 || value.Operations.Contains("read"))
            && (!value.Operations.Contains("relate") || value.RelationKinds.Count > 0), "invalid_capabilities");
    }

    public static void Context(AtcAccessContext context)
    {
        Target(context.Target); Text(context.Subject); Text(context.AuthorizationRevision);
        Require(context.Operations is { Count: <= 7 } && context.Operations.All(AtcContract.Operations.Contains)
            && context.Operations.Distinct(StringComparer.Ordinal).Count() == context.Operations.Count
            && context.RelatedTargetIds is { Count: <= 32 }, "forbidden");
        foreach (var id in context.RelatedTargetIds) Text(id);
    }

    public static string Operation(AtcChange change) => change switch
    {
        AtcCreate => "create",
        AtcUpdate => "update",
        AtcAddComment => "comment",
        AtcAddRelation => "relate",
        AtcApplyTransition => "transition",
        _ => throw new AtcException("unsupported_operation")
    };

    public static (string? ItemId, string? Revision) Selection(AtcChange change) => change switch
    {
        AtcCreate => (null, null),
        AtcUpdate c => (c.ItemId, c.ExpectedRevision),
        AtcAddComment c => (c.ItemId, c.ExpectedRevision),
        AtcAddRelation c => (c.ItemId, c.ExpectedRevision),
        AtcApplyTransition c => (c.ItemId, c.ExpectedRevision),
        _ => throw new AtcException("unsupported_operation")
    };

    public static void Mutation(AtcTargetRef target, AtcMutationRequest request, AtcCapabilities caps)
    {
        Text(request.RequestId); Text(request.OperationKey);
        Require(Enum.IsDefined(request.Mode) && request.Change is not null, "invalid_request");
        Require(request.IntentDigest == AtcContract.Digest(target, request.Change), "intent_conflict");
        Require(caps.Operations.Contains(Operation(request.Change)), "unsupported_operation");
        Require(request.Mode != AtcMutationMode.Recover || caps.Recovery != AtcRecoveryScope.None, "unsupported_operation");
        var selection = Selection(request.Change);
        if (selection.ItemId is not null) { Text(selection.ItemId); Text(selection.Revision); }
        switch (request.Change)
        {
            case AtcCreate c: Text(c.Title, 512); Body(c.Body); break;
            case AtcUpdate c:
                Require(c.Title is not null || c.Body is not null || c.AddClassifications is { Count: > 0 }, "invalid_request");
                if (c.Title is not null) { Require(caps.UpdateFields.Contains("title"), "unsupported_operation"); Text(c.Title, 512); }
                if (c.Body is not null) { Require(caps.UpdateFields.Contains("body"), "unsupported_operation"); Body(c.Body); }
                if (c.AddClassifications is not null)
                { Require(caps.UpdateFields.Contains("classifications.add"), "unsupported_operation"); Classifications(c.AddClassifications); }
                break;
            case AtcAddComment c: Require(!string.IsNullOrWhiteSpace(c.Text), "invalid_request"); Body(c.Text); break;
            case AtcAddRelation c:
                Require(c.Relation is not null && Enum.IsDefined(c.Relation.Kind) && caps.RelationKinds.Contains(c.Relation.Kind), "unsupported_operation");
                Identity(c.Relation.Item); Require(c.Relation.Item.ProviderId == target.ProviderId, "unsupported_operation"); break;
            case AtcApplyTransition c: Text(c.TransitionId); break;
        }
    }

    private static void Body(string? body) => Require(body is null || body.Length <= 32_768, "limit_exceeded");
    private static void Classifications(IReadOnlyList<string>? values)
    {
        Require(values is { Count: <= 32 } && values.Distinct(StringComparer.Ordinal).Count() == values.Count, "invalid_result");
        foreach (var value in values) Text(value);
    }
    private static void Identity(AtcItemRef? item)
    { Require(item is not null, "invalid_result"); Text(item.ProviderId); Text(item.TargetId); Text(item.ItemId); }

    public static void Item(AtcItem? item, AtcTargetRef target, string? expectedId = null)
    {
        Require(item is not null, "invalid_result"); Identity(item.Identity);
        Require(item.Identity.ProviderId == target.ProviderId && item.Identity.TargetId == target.TargetId
            && (expectedId is null || item.Identity.ItemId == expectedId), "invalid_result");
        Text(item.Revision); Text(item.Title, 512); Body(item.Body);
        Require(item.State is not null, "invalid_result"); Text(item.State.Id);
        if (item.State.CompletionReason is not null) Text(item.State.CompletionReason);
        Classifications(item.Classifications);
        Require(item.RelationsComplete, "incomplete_observation");
        Require(item.Relations is { Count: <= 100 } && item.Comments is { Count: <= 100 }
            && item.Transitions is { Count: <= 32 }, "invalid_result");
        foreach (var relation in item.Relations)
        { Require(relation is not null && Enum.IsDefined(relation.Kind), "invalid_result"); Identity(relation.Item); }
        foreach (var comment in item.Comments)
        { Require(comment is not null, "invalid_result"); Text(comment.Id); Body(comment.Text); }
        foreach (var transition in item.Transitions)
        { Require(transition is not null, "invalid_result"); Text(transition.Id); Text(transition.DestinationState); }
        Require(item.Comments.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == item.Comments.Count
            && item.Transitions.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() == item.Transitions.Count, "invalid_result");
    }

    public static void Page(AtcPage page, AtcTargetRef target, int limit)
    {
        Require(page is not null && page.Items is not null && page.Items.Count <= limit && page.Items.All(i => i is not null), "invalid_result");
        Require(page.Cursor is null || page.Items.Count > 0, "invalid_result");
        Cursor(page.Cursor);
        Require(page.Items.Select(i => i.Identity).Distinct().Count() == page.Items.Count, "invalid_result");
        foreach (var item in page.Items) Item(item, target);
    }
    public static void RelationAccess(AtcItem item, AtcAccessContext context) => Require(item.Relations.All(relation =>
        relation.Item.ProviderId == context.Target.ProviderId && (relation.Item.TargetId == context.Target.TargetId
            || context.RelatedTargetIds.Contains(relation.Item.TargetId))), "forbidden");
    public static void Cursor(string? cursor)
    { if (cursor is not null) Text(cursor, 4096); }

    public static void Receipt(AtcReceipt receipt, AtcTargetRef target, AtcMutationRequest request)
    {
        Require(receipt is not null && Enum.IsDefined(receipt.Status) && receipt.OperationKey == request.OperationKey
            && receipt.IntentDigest == request.IntentDigest, "invalid_result");
        Require(receipt.Status != AtcMutationStatus.Missing || request.Mode == AtcMutationMode.Recover, "invalid_result");
        Require(receipt.Status == AtcMutationStatus.Applied ? receipt.Item is not null && receipt.ErrorCode is null
            : receipt.Item is null && (receipt.Status == AtcMutationStatus.Missing ? receipt.ErrorCode is null : Code(receipt.ErrorCode)), "invalid_result");
        if (receipt.Item is not null) Item(receipt.Item, target, Selection(request.Change).ItemId);
    }

    public static bool Code(string? code) => code is { Length: > 0 and <= 64 } && code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
    public static void Bytes(JsonElement value, int maximum) => Require(Encoding.UTF8.GetByteCount(value.GetRawText()) <= maximum, "limit_exceeded");
}
