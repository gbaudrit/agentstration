using Afc.Samples.LocalForge;
using Agentstration.Afc.Abstractions;

namespace Agentstration.Afc.Tests;

[TestClass]
public sealed class AfcGuaranteeTests
{
    [TestMethod]
    public async Task UnsupportedReviewCapabilityIsNotReplacedByAnEmptyApprovalSet()
    {
        await using var host = new AfcTestHost(provider: clock => new Provider(clock, caps => caps with { Reviews = false }));
        await host.StartAsync(); var item = await host.CreateAsync();
        var request = new AfcObserveRequest("observe", item.Identity.RepositoryId, item.Identity.NativeId, item.Head.ObjectId, item.Base.ObjectId);
        await AfcTestHost.Error("unsupported_operation", () => host.Client.ObserveAsync(request));
        var checks = await host.Client.ObserveAsync(request with { Reviews = false });
        Assert.IsEmpty(checks.Reviews); Assert.HasCount(1, checks.Checks);
    }

    [TestMethod]
    public async Task RecoveryUnavailableIsExplicitEvenWhenTheFixtureHasInternalReceipts()
    {
        await using var host = new AfcTestHost(provider: clock => new Provider(clock, caps => caps with { Recovery = AfcRecoveryScope.None, ReceiptWindowSeconds = 0 }));
        await host.StartAsync(); var request = host.Request(); await host.Client.CreateAsync(request);
        await AfcTestHost.Error("unsupported_operation", () => host.Client.CreateAsync(request with { Mode = AfcMutationMode.Recover }));
    }

    [TestMethod]
    public async Task PartialNativeEvidenceRemainsUncertainAndRecoveryIsNotAFreshObservation()
    {
        await using var host = new AfcTestHost(provider: clock => new Provider(clock,
            caps => caps with { CreationConcurrency = AfcConcurrency.Observation }, moveHeadAfterCreate: true));
        await host.StartAsync(); var request = host.Request();
        var partial = await host.Client.CreateAsync(request);
        Assert.AreEqual(AfcConcurrency.Observation, host.Client.Description.Capabilities.CreationConcurrency);
        Assert.AreEqual(AfcReceiptStatus.Uncertain, partial.Status);
        Assert.IsNotNull(partial.ChangeRequest);
        Assert.AreNotEqual(request.Intent.Head.ObjectId, partial.ChangeRequest.Head.ObjectId);
        var recovered = await host.Client.CreateAsync(request with { Mode = AfcMutationMode.Recover });
        Assert.AreEqual(AfcReceiptStatus.Applied, recovered.Status);
        Assert.AreEqual(partial.ChangeRequest.Identity, recovered.ChangeRequest!.Identity);
        var current = await host.Client.ReadAsync(new("current", recovered.ChangeRequest.Identity.RepositoryId, recovered.ChangeRequest.Identity.NativeId));
        Assert.AreNotEqual(recovered.ChangeRequest.Head.ObjectId, current.Head.ObjectId);
    }

    [TestMethod]
    public async Task UnknownReviewStateIsRetainedWithoutClaimingFreshAcceptance()
    {
        await using var host = new AfcTestHost(provider: clock => new Provider(clock, caps => caps, unknownObservations: true));
        await host.StartAsync(); var item = await host.CreateAsync();
        var result = await host.Client.ObserveAsync(new("observe", item.Identity.RepositoryId, item.Identity.NativeId, item.Head.ObjectId, item.Base.ObjectId));
        Assert.AreEqual(AfcObservationStatus.Unknown, result.Status);
        Assert.AreEqual(AfcReviewState.Unknown, result.Reviews.Single().State);
        Assert.AreEqual("unmapped-native-state", result.Reviews.Single().NativeState);
    }

    private sealed class Provider(TimeProvider clock, Func<AfcCapabilities, AfcCapabilities> capabilities,
        bool moveHeadAfterCreate = false, bool unknownObservations = false) : IAfcProvider
    {
        private readonly MemoryForgeProvider inner = new(clock);
        public string ImplementationId => inner.ImplementationId;
        public string ImplementationVersion => inner.ImplementationVersion;
        public AfcCapabilities Capabilities => capabilities(inner.Capabilities);
        public ValueTask<AfcRepository> ResolveAsync(AfcAccessContext context, AfcResolveRequest request, CancellationToken ct) => inner.ResolveAsync(context, request, ct);
        public ValueTask<AfcChangeRequest> ReadAsync(AfcAccessContext context, AfcReadRequest request, CancellationToken ct) => inner.ReadAsync(context, request, ct);
        public async ValueTask<AfcReceipt> CreateAsync(AfcAccessContext context, AfcCreateRequest request, CancellationToken ct)
        {
            var receipt = await inner.CreateAsync(context, request, ct);
            if (!moveHeadAfterCreate || request.Mode != AfcMutationMode.Execute || receipt.ChangeRequest is not { } item) return receipt;
            var changedHead = new string('c', 40);
            inner.AdvanceRef(context.Target, item.Head.Name, changedHead);
            return receipt with { Status = AfcReceiptStatus.Uncertain, ErrorCode = "native_head_changed", ChangeRequest = item with { Head = item.Head with { ObjectId = changedHead } } };
        }
        public async ValueTask<AfcObservations> ObserveAsync(AfcAccessContext context, AfcObserveRequest request, CancellationToken ct)
        {
            var result = await inner.ObserveAsync(context, request, ct);
            return unknownObservations ? result with
            {
                Status = AfcObservationStatus.Unknown,
                ReviewsComplete = false,
                Reviews = [new("review-1", "unmapped-native-state", AfcReviewState.Unknown)]
            } : result;
        }
    }
}
