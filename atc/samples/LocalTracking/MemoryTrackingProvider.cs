using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Atc.Abstractions;

namespace Atc.Samples.LocalTracking;

// Deterministic, bounded sample. Receipts are process-local, not durable storage.
public sealed class MemoryTrackingProvider(TimeProvider clock) : IAtcProvider
{
    private readonly object gate = new();
    private readonly Dictionary<AtcItemRef, AtcItem> items = [];
    private readonly Dictionary<(AtcTargetRef Target, string Subject, string Key), Entry> receipts = [];
    private readonly byte[] cursorKey = RandomNumberGenerator.GetBytes(32);
    private long sequence;
    public string ImplementationId => "memory-tracking-sample";
    public string ImplementationVersion => "1.0.0";
    public AtcCapabilities Capabilities { get; } = new(AtcContract.Operations, ["title", "body", "classifications.add"],
        Enum.GetValues<AtcRelationKind>(), AtcConcurrency.AtomicRevision, AtcRecoveryScope.Process, 300, new());

    public ValueTask<AtcItem> ReadAsync(AtcAccessContext context, AtcReadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return ValueTask.FromResult(Get(context, request.ItemId));
    }

    public ValueTask<AtcPage> ListAsync(AtcAccessContext context, AtcListRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var after = request.Cursor is null ? null : OpenCursor(context, request.Cursor, request.Limit);
            var selected = items.Values.Where(i => i.Identity.ProviderId == context.Target.ProviderId
                && i.Identity.TargetId == context.Target.TargetId && (after is null || string.CompareOrdinal(i.Identity.ItemId, after) > 0))
                .OrderBy(i => i.Identity.ItemId, StringComparer.Ordinal).Take(request.Limit + 1).ToArray();
            return ValueTask.FromResult(new AtcPage(selected.Take(request.Limit).ToArray(), selected.Length <= request.Limit
                ? null : SealCursor(context, selected[request.Limit - 1].Identity.ItemId, request.Limit)));
        }
    }

    public ValueTask<AtcReceipt> MutateAsync(AtcAccessContext context, AtcMutationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var key = (context.Target, context.Subject, request.OperationKey);
            if (receipts.TryGetValue(key, out var entry))
            {
                AtcValidation.Require(entry.Receipt.IntentDigest == request.IntentDigest, "operation_conflict");
                return ValueTask.FromResult(entry.ExpiresAt <= clock.GetUtcNow()
                    ? new AtcReceipt(request.OperationKey, request.IntentDigest, AtcMutationStatus.Uncertain, ErrorCode: "receipt_expired") : entry.Receipt);
            }
            if (request.Mode == AtcMutationMode.Recover)
                return ValueTask.FromResult(new AtcReceipt(request.OperationKey, request.IntentDigest, AtcMutationStatus.Missing));
            AtcValidation.Require(receipts.Count < 256, "receipt_limit");
            AtcValidation.Require(items.Count < 1000 || request.Change is not AtcCreate, "item_limit");
            AtcReceipt receipt;
            try
            {
                var selected = AtcValidation.Selection(request.Change);
                if (selected.ItemId is not null)
                    AtcValidation.Require(Get(context, selected.ItemId).Revision == selected.Revision, "revision_conflict");
                var item = Apply(context, request.Change);
                receipt = new(request.OperationKey, request.IntentDigest, AtcMutationStatus.Applied, item);
            }
            catch (AtcException error) { receipt = new(request.OperationKey, request.IntentDigest, AtcMutationStatus.Rejected, ErrorCode: error.Code); }
            receipts.Add(key, new(receipt, clock.GetUtcNow().AddSeconds(Capabilities.ReceiptWindowSeconds)));
            return ValueTask.FromResult(receipt);
        }
    }

    private AtcItem Apply(AtcAccessContext context, AtcChange change)
    {
        if (change is AtcCreate create)
        {
            var identity = new AtcItemRef(context.Target.ProviderId, context.Target.TargetId, (++sequence).ToString("D8", CultureInfo.InvariantCulture));
            var item = new AtcItem(identity, "1", create.Title, create.Body, new("open"), [], [], true, [],
                [new("finish", "closed"), new("abandon", "closed")]);
            items.Add(identity, item); return item;
        }
        var selected = AtcValidation.Selection(change);
        var existing = Get(context, selected.ItemId!);
        var next = existing with { Revision = NextRevision(existing) };
        switch (change)
        {
            case AtcUpdate update:
                var classifications = existing.Classifications.Union(update.AddClassifications ?? [], StringComparer.Ordinal).ToArray();
                AtcValidation.Require(classifications.Length <= 32, "limit_exceeded");
                next = next with { Title = update.Title ?? existing.Title, Body = update.Body ?? existing.Body, Classifications = classifications }; break;
            case AtcAddComment comment:
                AtcValidation.Require(existing.Comments.Count < 100, "limit_exceeded");
                next = next with { Comments = [.. existing.Comments, new(next.Revision, comment.Text)] }; break;
            case AtcApplyTransition transition:
                AtcValidation.Require(existing.Transitions.Any(t => t.Id == transition.TransitionId), "transition_unavailable");
                next = next with { State = new("closed", transition.TransitionId == "finish" ? "done" : "abandoned"), Transitions = [] }; break;
            case AtcAddRelation relation:
                AtcValidation.Require(relation.Relation.Item.ProviderId == context.Target.ProviderId
                    && (relation.Relation.Item.TargetId == context.Target.TargetId || context.RelatedTargetIds.Contains(relation.Relation.Item.TargetId)), "forbidden");
                AtcValidation.Require(items.TryGetValue(relation.Relation.Item, out var other), "item_not_found");
                AtcValidation.Require(other.Identity != existing.Identity, "relation_conflict");
                if (relation.Relation.Kind is AtcRelationKind.Parent or AtcRelationKind.Child)
                {
                    var child = relation.Relation.Kind == AtcRelationKind.Parent ? existing : other;
                    var parentItem = relation.Relation.Kind == AtcRelationKind.Parent ? other : existing;
                    AtcValidation.Require(!child.Relations.Any(r => r.Kind == AtcRelationKind.Parent && r.Item != parentItem.Identity), "relation_conflict");
                    var ancestor = parentItem;
                    var seen = new HashSet<AtcItemRef> { child.Identity };
                    while (true)
                    {
                        AtcValidation.Require(seen.Add(ancestor.Identity), "relation_conflict");
                        var parent = ancestor.Relations.SingleOrDefault(r => r.Kind == AtcRelationKind.Parent);
                        if (parent is null) break;
                        ancestor = items[parent.Item];
                    }
                }
                AtcValidation.Require(existing.Relations.Count < 100 && other.Relations.Count < 100, "limit_exceeded");
                next = next with { Relations = existing.Relations.Append(relation.Relation).Distinct().ToArray() };
                var inverse = relation.Relation.Kind switch
                {
                    AtcRelationKind.Parent => AtcRelationKind.Child,
                    AtcRelationKind.Child => AtcRelationKind.Parent,
                    AtcRelationKind.Blocks => AtcRelationKind.BlockedBy,
                    AtcRelationKind.BlockedBy => AtcRelationKind.Blocks,
                    _ => AtcRelationKind.Related
                };
                items[other.Identity] = other with { Revision = NextRevision(other), Relations = other.Relations.Append(new AtcRelation(inverse, existing.Identity)).Distinct().ToArray() };
                break;
            default: throw new AtcException("unsupported_operation");
        }
        items[existing.Identity] = next; return next;
    }

    private AtcItem Get(AtcAccessContext context, string itemId)
    {
        AtcValidation.Require(items.TryGetValue(new(context.Target.ProviderId, context.Target.TargetId, itemId), out var item), "item_not_found");
        return item;
    }
    private static string NextRevision(AtcItem item) => (long.Parse(item.Revision, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);

    private string SealCursor(AtcAccessContext context, string after, int limit)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Cursor(context.Target, context.Subject, context.AuthorizationRevision,
            after, limit, clock.GetUtcNow().AddMinutes(5)), AtcContract.Json);
        return Convert.ToBase64String(payload) + "." + Convert.ToHexStringLower(HMACSHA256.HashData(cursorKey, payload));
    }
    private string OpenCursor(AtcAccessContext context, string cursor, int limit)
    {
        try
        {
            var parts = cursor.Split('.'); AtcValidation.Require(parts.Length == 2, "invalid_cursor");
            var bytes = Convert.FromBase64String(parts[0]); var signature = Convert.FromHexString(parts[1]);
            AtcValidation.Require(CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(cursorKey, bytes)), "invalid_cursor");
            var value = JsonSerializer.Deserialize<Cursor>(bytes, AtcContract.Json);
            AtcValidation.Require(value is not null && value.Target == context.Target && value.Subject == context.Subject
                && value.AuthorizationRevision == context.AuthorizationRevision && value.Limit == limit && value.ExpiresAt > clock.GetUtcNow(), "invalid_cursor");
            return value.After;
        }
        catch (FormatException) { throw new AtcException("invalid_cursor"); }
        catch (JsonException) { throw new AtcException("invalid_cursor"); }
    }
    private sealed record Cursor(AtcTargetRef Target, string Subject, string AuthorizationRevision, string After, int Limit, DateTimeOffset ExpiresAt);
    private sealed record Entry(AtcReceipt Receipt, DateTimeOffset ExpiresAt);
}
