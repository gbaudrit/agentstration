using System.Net;
using System.Text.Json;
using Afc.Samples.LocalForge;
using Agentstration.Afc.Abstractions;
using Agentstration.Afc.Client;

namespace Agentstration.Afc.Tests;

[TestClass]
public sealed class AfcConformanceTests
{
    [TestMethod]
    public async Task ResolveCreateAndReadUseExactRepositoryAndRevisionEvidenceWithoutATracker()
    {
        await using var host = new AfcTestHost(); await host.StartAsync();
        var repository = await host.Client.ResolveAsync(new("resolve", ["main", "feature"]));
        Assert.AreEqual(MemoryForgeProvider.RepositoryId, repository.NativeId);
        Assert.AreEqual(MemoryForgeProvider.BaseObjectId, repository.Refs.Single(r => r.Name == "main").ObjectId);
        var item = await host.CreateAsync();
        var read = await host.Client.ReadAsync(new("read", repository.NativeId, item.Identity.NativeId));
        Assert.AreEqual(item.Identity, read.Identity); Assert.AreEqual(item.Head, read.Head);
        Assert.IsTrue(item.Draft); Assert.IsFalse(item.Merged); Assert.IsEmpty(item.Tracking);
        Assert.AreEqual(MemoryForgeProvider.HeadObjectId, read.Head.ObjectId);
        Assert.AreEqual(AfcConcurrency.AtomicRevision, host.Client.Description.Capabilities.CreationConcurrency);
    }

    [TestMethod]
    public async Task IndependentTrackerIdentityIsAnAssociationAndGrantsNoTrackingAuthority()
    {
        await using var host = new AfcTestHost(); await host.StartAsync();
        var reference = new AfcTrackingRef("another-tracker", "team-without-repository", "issue-42");
        var receipt = await host.Client.CreateAsync(host.Request(host.Intent() with { Tracking = [reference] }));
        Assert.AreEqual(AfcReceiptStatus.Applied, receipt.Status);
        Assert.AreNotEqual(reference.ProviderId, host.Target.ProviderId);
        Assert.AreEqual(reference, receipt.ChangeRequest!.Tracking.Single());
        var read = await host.Client.ReadAsync(new("read", MemoryForgeProvider.RepositoryId, receipt.ChangeRequest.Identity.NativeId));
        Assert.AreEqual(reference, read.Tracking.Single());
    }

    [TestMethod]
    public async Task ResolveDefaultAndUnknownRefsNeverSubstituteAnotherRevision()
    {
        await using var host = new AfcTestHost(); await host.StartAsync();
        var repository = await host.Client.ResolveAsync(new("default", []));
        Assert.AreEqual("main", repository.Refs.Single().Name);
        await AfcTestHost.Error("ref_not_found", () => host.Client.ResolveAsync(new("unknown", ["missing"])));
    }

    [TestMethod]
    [DataRow("feature", "head_revision_changed")]
    [DataRow("main", "base_revision_changed")]
    public async Task StaleHeadOrBaseRejectsCreationWithoutEffects(string name, string code)
    {
        var memory = new MemoryForgeProvider(TimeProvider.System);
        await using var host = new AfcTestHost(provider: _ => memory); await host.StartAsync();
        memory.AdvanceRef(host.Target, name, new string('c', 40));
        var result = await host.Client.CreateAsync(host.Request());
        Assert.AreEqual(AfcReceiptStatus.Rejected, result.Status);
        Assert.AreEqual(code, result.ErrorCode); Assert.AreEqual(0, memory.Count);
    }

    [TestMethod]
    public async Task ChangedRepositoryIdentityRejectsCreation()
    {
        await using var host = new AfcTestHost(); await host.StartAsync();
        var response = await host.Client.CreateAsync(host.Request(host.Intent() with { RepositoryId = "replaced-native-id" }));
        Assert.AreEqual(AfcReceiptStatus.Rejected, response.Status);
        Assert.AreEqual("repository_changed", response.ErrorCode);
        Assert.IsNull(response.ChangeRequest);
    }

    [TestMethod]
    public async Task ReplayRecoveryAndConflictRetainTheOriginalIntent()
    {
        await using var host = new AfcTestHost(); await host.StartAsync();
        var request = host.Request(key: "stable-key");
        var original = await host.Client.CreateAsync(request);
        var replay = await host.Client.CreateAsync(request with { RequestId = "another-attempt" });
        var recovery = await host.Client.CreateAsync(request with { Mode = AfcMutationMode.Recover });
        Assert.AreEqual(original.ChangeRequest!.Identity, replay.ChangeRequest!.Identity);
        Assert.AreEqual(original.ChangeRequest.Identity, recovery.ChangeRequest!.Identity);
        await AfcTestHost.Error("operation_conflict", () => host.Client.CreateAsync(host.Request(host.Intent("different"), "stable-key")));
        var missing = await host.Client.CreateAsync(host.Request(key: "never-dispatched", mode: AfcMutationMode.Recover));
        Assert.AreEqual(AfcReceiptStatus.Missing, missing.Status);
    }

    [TestMethod]
    public async Task ConcurrentReplayCreatesOneArtifact()
    {
        var memory = new MemoryForgeProvider(TimeProvider.System);
        await using var host = new AfcTestHost(provider: _ => memory); await host.StartAsync();
        var request = host.Request();
        var receipts = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => host.Client.CreateAsync(request with { RequestId = "parallel-" + index })));
        Assert.HasCount(1, receipts.Select(r => r.ChangeRequest!.Identity).Distinct().ToArray());
        Assert.AreEqual(1, memory.Count);
    }

    [TestMethod]
    public async Task ExpiredReceiptsRemainUncertainAndNeverBecomeProofOfAbsence()
    {
        await using var host = new AfcTestHost(); await host.StartAsync();
        var request = host.Request(); await host.Client.CreateAsync(request);
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        foreach (var mode in new[] { AfcMutationMode.Execute, AfcMutationMode.Recover })
        {
            var receipt = await host.Client.CreateAsync(request with { Mode = mode });
            Assert.AreEqual(AfcReceiptStatus.Uncertain, receipt.Status);
            Assert.AreEqual("receipt_expired", receipt.ErrorCode);
        }
    }

    [TestMethod]
    public async Task NativeReviewAndCheckObservationsAreBoundToHeadAndBase()
    {
        var memory = new MemoryForgeProvider(TimeProvider.System);
        await using var host = new AfcTestHost(provider: _ => memory); await host.StartAsync();
        var item = await host.CreateAsync();
        var request = new AfcObserveRequest("observe", item.Identity.RepositoryId, item.Identity.NativeId, item.Head.ObjectId, item.Base.ObjectId);
        var fresh = await host.Client.ObserveAsync(request);
        Assert.AreEqual(AfcObservationStatus.Fresh, fresh.Status);
        Assert.AreEqual(AfcReviewState.Approved, fresh.Reviews.Single().State);
        Assert.AreEqual("APPROVED", fresh.Reviews.Single().NativeState);
        Assert.AreEqual(item.Head.ObjectId, fresh.Reviews.Single().ObjectId);
        Assert.AreEqual(AfcCheckState.Success, fresh.Checks.Single().State);
        Assert.IsTrue((await host.Client.ReadAsync(new("still-draft", item.Identity.RepositoryId, item.Identity.NativeId))).Draft);
        memory.AdvanceRef(host.Target, "feature", new string('c', 40));
        var stale = await host.Client.ObserveAsync(request);
        Assert.AreEqual(AfcObservationStatus.Stale, stale.Status);
        Assert.AreNotEqual(request.ExpectedHead, stale.HeadObjectId);
    }

    [TestMethod]
    public async Task TargetsAndTransportSubjectsRemainIndependent()
    {
        await using var host = new AfcTestHost(); await host.StartAsync();
        var item = await host.CreateAsync();
        var other = await AfcClient.ConnectAsync(host.Mcp, host.Access.Other);
        await AfcTestHost.Error("change_not_found", () => other.ReadAsync(new("foreign", item.Identity.RepositoryId, item.Identity.NativeId)));
        await using var stranger = new AfcTestHost();
        await AfcTestHost.Error("forbidden", () => stranger.StartAsync("repository", other: true));
    }

    [TestMethod]
    public async Task CurrentAuthorizationIsRecheckedForReadsAndCachedRecovery()
    {
        await using var host = new AfcTestHost(); await host.StartAsync();
        var request = host.Request(); var item = (await host.Client.CreateAsync(request)).ChangeRequest!;
        host.Access.Enabled = false;
        await AfcTestHost.Error("forbidden", () => host.Client.ReadAsync(new("read", item.Identity.RepositoryId, item.Identity.NativeId)));
        await AfcTestHost.Error("forbidden", () => host.Client.CreateAsync(request with { Mode = AfcMutationMode.Recover }));
        await AfcTestHost.Error("forbidden", () => AfcClient.ConnectAsync(host.Mcp, host.Target));
    }

    [TestMethod]
    public async Task RevokedWriteGrantCannotReuseCachedCapabilities()
    {
        await using var host = new AfcTestHost(); await host.StartAsync();
        host.Access.CanWrite = false;
        await AfcTestHost.Error("unsupported_operation", () => host.Client.CreateAsync(host.Request()));
        var current = await AfcClient.ConnectAsync(host.Mcp, host.Target);
        Assert.IsFalse(current.Description.Capabilities.Operations.Contains("create"));
    }

    [TestMethod]
    public async Task UnauthenticatedHttpRequestsAreRefused()
    {
        await using var host = new AfcTestHost();
        using var http = host.Factory.CreateClient();
        using var response = await http.PostAsync("/mcp", new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task RestartFencesOldTargetGenerationRatherThanReturningMissing()
    {
        AfcTargetRef old;
        await using (var previous = new AfcTestHost())
        {
            await previous.StartAsync(); old = previous.Target; await previous.CreateAsync();
        }
        await using var current = new AfcTestHost(); await current.StartAsync();
        var response = await current.RawAsync("describe", new AfcDescribeRequest("old-generation"), old);
        Assert.AreEqual("forbidden", AfcValidation.Parse<AfcReply<AfcDescription>>(response.StructuredContent!.Value).Error!.Code);
    }

    [TestMethod]
    public async Task PreCancelledCreationHasNoNativeEffect()
    {
        var memory = new MemoryForgeProvider(TimeProvider.System);
        await using var host = new AfcTestHost(provider: _ => memory); await host.StartAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var request = host.Request();
        await Assert.ThrowsAsync<OperationCanceledException>(() => host.Client.CreateAsync(request, cancellation.Token));
        Assert.AreEqual(0, memory.Count);
        Assert.AreEqual(AfcReceiptStatus.Missing, (await host.Client.CreateAsync(request with { Mode = AfcMutationMode.Recover })).Status);
    }

    [TestMethod]
    public async Task InvalidIntentAndOversizedRequestsFailBeforeEffects()
    {
        await using var host = new AfcTestHost(); await host.StartAsync();
        var request = host.Request();
        await AfcTestHost.Error("intent_conflict", () => host.Client.CreateAsync(request with { IntentDigest = "wrong" }));
        await AfcTestHost.Error("invalid_request", () => host.Client.CreateAsync(host.Request(host.Intent() with { Body = new string('x', 32769) })));
        var tooLarge = await host.RawAsync("describe", new { requestId = "large", padding = new string('x', AfcContract.MaximumRequestBytes) });
        Assert.IsTrue(tooLarge.IsError);
    }
}
