using System.Globalization;
using Agentstration.Afc.Abstractions;

namespace Afc.Samples.LocalForge;

// Bounded example; process-local recovery is explicitly not production durability.
public sealed class MemoryForgeProvider(TimeProvider clock) : IAfcProvider
{
    public const string RepositoryId = "native-repository-42";
    public static string HeadObjectId => new('b', 40);
    public static string BaseObjectId => new('a', 40);
    private readonly object gate = new();
    private readonly Dictionary<AfcItemRef, AfcChangeRequest> items = [];
    private readonly Dictionary<(AfcTargetRef Target, string Subject, string Key), Entry> receipts = [];
    private readonly Dictionary<(AfcTargetRef Target, string Ref), string> refs = [];
    private long sequence;
    public string ImplementationId => "memory-forge-sample";
    public string ImplementationVersion => "1.0.0";
    public AfcCapabilities Capabilities { get; } = new(AfcContract.Operations, AfcConcurrency.AtomicRevision,
        AfcRecoveryScope.Process, 300, true, true, new());
    public int Count { get { lock (gate) return items.Count; } }

    public void AdvanceRef(AfcTargetRef target, string name, string objectId)
    {
        AfcValidation.Text(name, 256); AfcValidation.Hash(objectId);
        lock (gate) refs[(target, name)] = objectId;
    }

    public ValueTask<AfcRepository> ResolveAsync(AfcAccessContext context, AfcResolveRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            IReadOnlyList<string> names = request.Refs.Count == 0 ? ["main"] : request.Refs;
            return ValueTask.FromResult(new AfcRepository(context.Target.ProviderId, context.Target.TargetId,
                RepositoryId, "fixture/source", new("https://forge.fixture/source"), "main",
                names.Select(name => new AfcCommitRef(name, ResolveRef(context.Target, name))).ToArray(),
                new("https://forge.fixture/source.git")));
        }
    }

    public ValueTask<AfcChangeRequest> ReadAsync(AfcAccessContext context, AfcReadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return ValueTask.FromResult(Current(Get(context, request.RepositoryId, request.NativeId), context.Target));
    }

    public ValueTask<AfcReceipt> CreateAsync(AfcAccessContext context, AfcCreateRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var key = (context.Target, context.Subject, request.OperationKey);
            if (receipts.TryGetValue(key, out var entry))
            {
                AfcValidation.Require(entry.Receipt.IntentDigest == request.IntentDigest, "operation_conflict");
                return ValueTask.FromResult(entry.ExpiresAt <= clock.GetUtcNow()
                    ? new AfcReceipt(request.OperationKey, request.IntentDigest, AfcReceiptStatus.Uncertain, ErrorCode: "receipt_expired") : entry.Receipt);
            }
            if (request.Mode == AfcMutationMode.Recover)
                return ValueTask.FromResult(new AfcReceipt(request.OperationKey, request.IntentDigest, AfcReceiptStatus.Missing));
            AfcValidation.Require(receipts.Count < 256, "receipt_limit");
            AfcValidation.Require(items.Count < 1000, "item_limit");
            AfcReceipt receipt;
            try
            {
                var intent = request.Intent;
                AfcValidation.Require(intent.RepositoryId == RepositoryId, "repository_changed");
                AfcValidation.Require(ResolveRef(context.Target, intent.Head.Name) == intent.Head.ObjectId, "head_revision_changed");
                AfcValidation.Require(ResolveRef(context.Target, intent.Base.Name) == intent.Base.ObjectId, "base_revision_changed");
                var id = (++sequence).ToString(CultureInfo.InvariantCulture);
                var item = new AfcChangeRequest(new(context.Target.ProviderId, context.Target.TargetId, RepositoryId, "change-" + id),
                    id, "revision-1", new("https://forge.fixture/source/changes/" + id), intent.Title, intent.Body, "open",
                    intent.Draft, false, intent.Head, intent.Base, [.. intent.Tracking ?? []]);
                items.Add(item.Identity, item);
                receipt = new(request.OperationKey, request.IntentDigest, AfcReceiptStatus.Applied, item);
            }
            catch (AfcException error) { receipt = new(request.OperationKey, request.IntentDigest, AfcReceiptStatus.Rejected, ErrorCode: error.Code); }
            receipts.Add(key, new(receipt, clock.GetUtcNow().AddSeconds(Capabilities.ReceiptWindowSeconds)));
            return ValueTask.FromResult(receipt);
        }
    }

    public ValueTask<AfcObservations> ObserveAsync(AfcAccessContext context, AfcObserveRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var item = Current(Get(context, request.RepositoryId, request.NativeId), context.Target);
            var status = item.Head.ObjectId == request.ExpectedHead && item.Base.ObjectId == request.ExpectedBase
                ? AfcObservationStatus.Fresh : AfcObservationStatus.Stale;
            return ValueTask.FromResult(new AfcObservations(item.Identity, item.Head.ObjectId, item.Base.ObjectId,
                status, clock.GetUtcNow(), request.Reviews, request.Checks,
                request.Reviews ? [new("review-1", "APPROVED", AfcReviewState.Approved, item.Head.ObjectId)] : [],
                request.Checks ? [new("check-1", "completed:success", AfcCheckState.Success, item.Head.ObjectId)] : []));
        }
    }

    private AfcChangeRequest Get(AfcAccessContext context, string repositoryId, string nativeId)
    {
        AfcValidation.Require(repositoryId == RepositoryId, "repository_changed");
        AfcValidation.Require(items.TryGetValue(new(context.Target.ProviderId, context.Target.TargetId, repositoryId, nativeId), out var item), "change_not_found");
        return item;
    }

    private AfcChangeRequest Current(AfcChangeRequest item, AfcTargetRef target) => item with
    {
        Head = item.Head with { ObjectId = ResolveRef(target, item.Head.Name) },
        Base = item.Base with { ObjectId = ResolveRef(target, item.Base.Name) }
    };

    private string ResolveRef(AfcTargetRef target, string name)
    {
        if (refs.TryGetValue((target, name), out var value)) return value;
        return name switch { "main" => BaseObjectId, "feature" => HeadObjectId, _ => throw new AfcException("ref_not_found") };
    }

    private sealed record Entry(AfcReceipt Receipt, DateTimeOffset ExpiresAt);
}
