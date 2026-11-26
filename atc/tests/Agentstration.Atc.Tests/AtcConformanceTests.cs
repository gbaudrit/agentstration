using System.Text.Json;
using Agentstration.Atc.Abstractions;
using Agentstration.Atc.Client;

namespace Agentstration.Atc.Tests;

[TestClass]
public sealed class AtcConformanceTests
{
    [TestMethod]
    public async Task MutationDiscriminatorCanFollowTheOtherJsonProperties()
    {
        await using var host = new AtcTestHost();
        await host.StartAsync();
        var request = host.Mutation(new AtcCreate("Property order is not significant"));
        var arguments = JsonSerializer.SerializeToNode(request, AtcContract.Json)!;
        var change = arguments["change"]!.AsObject();
        var operation = change["operation"]!.DeepClone();
        change.Remove("operation");
        change.Add("operation", operation);

        var result = await host.RawAsync("mutate", arguments);
        var reply = AtcValidation.Parse<AtcReply<AtcReceipt>>(result.StructuredContent!.Value);

        Assert.IsNull(reply.Error);
        Assert.AreEqual(AtcMutationStatus.Applied, reply.Data!.Status);
        Assert.HasCount(1, (await host.Client.ListAsync(new("observe"))).Items);
    }

    [TestMethod]
    [DataRow("team")]
    [DataRow("repository")]
    public async Task DeterministicClientCreatesReadsAndListsIndependentTargets(string target)
    {
        await using var host = new AtcTestHost(); await host.StartAsync(target);
        Assert.AreEqual(AtcContract.Id, host.Client.Description.Declaration.ContractId);
        var item = await host.CreateAsync("é🌍 native item");
        var read = await host.Client.ReadAsync(new("read", item.Identity.ItemId));
        Assert.AreEqual(item.Identity, read.Identity); Assert.AreEqual(item.Title, read.Title);
        Assert.AreEqual("open", read.State.Id);
        var page = await host.Client.ListAsync(new("list")); Assert.HasCount(1, page.Items);
        Assert.AreEqual(target, page.Items[0].Identity.TargetId);
        var otherTarget = target == "team" ? host.Access.Repository : host.Access.Team;
        var other = await AtcClient.ConnectAsync(host.Mcp, otherTarget);
        await AtcTestHost.Error("item_not_found", () => other.ReadAsync(new("foreign", item.Identity.ItemId)));
    }

    [TestMethod]
    public async Task ReplayAndRecoveryReturnTheOriginalReceiptAndRejectChangedIntent()
    {
        await using var host = new AtcTestHost(); await host.StartAsync();
        var request = host.Mutation(new AtcCreate("original"), "stable-key");
        var original = await host.Client.MutateAsync(request);
        var replay = await host.Client.MutateAsync(request with { RequestId = "second-attempt" });
        var recovered = await host.Client.MutateAsync(request with { RequestId = "recover", Mode = AtcMutationMode.Recover });
        Assert.AreEqual(original.Item!.Identity, replay.Item!.Identity); Assert.AreEqual(original.Item.Identity, recovered.Item!.Identity);
        Assert.HasCount(1, (await host.Client.ListAsync(new("list"))).Items);
        await AtcTestHost.Error("operation_conflict", () => host.Client.MutateAsync(host.Mutation(new AtcCreate("different"), "stable-key")));
        var missing = await host.Client.MutateAsync(host.Mutation(new AtcCreate("unsent"), "never-sent", AtcMutationMode.Recover));
        Assert.AreEqual(AtcMutationStatus.Missing, missing.Status);
    }

    [TestMethod]
    public async Task ExpiredReceiptRemainsUncertainAndCannotCauseAnotherCreate()
    {
        await using var host = new AtcTestHost(); await host.StartAsync();
        var request = host.Mutation(new AtcCreate("once")); await host.Client.MutateAsync(request);
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        foreach (var mode in new[] { AtcMutationMode.Execute, AtcMutationMode.Recover })
        {
            var result = await host.Client.MutateAsync(request with { Mode = mode });
            Assert.AreEqual(AtcMutationStatus.Uncertain, result.Status); Assert.AreEqual("receipt_expired", result.ErrorCode);
        }
        Assert.HasCount(1, (await host.Client.ListAsync(new("list"))).Items);
    }

    [TestMethod]
    public async Task ConditionalMutationsAndNativeTransitionsPreserveConflictEvidence()
    {
        await using var host = new AtcTestHost(); await host.StartAsync();
        var item = await host.CreateAsync();
        var updated = await host.Client.MutateAsync(host.Mutation(new AtcUpdate(item.Identity.ItemId, item.Revision, Title: "changed", AddClassifications: ["managed:a"])));
        Assert.AreEqual("changed", updated.Item!.Title); CollectionAssert.Contains(updated.Item.Classifications.ToArray(), "managed:a");
        var stale = await host.Client.MutateAsync(host.Mutation(new AtcUpdate(item.Identity.ItemId, item.Revision, Title: "stale")));
        Assert.AreEqual(AtcMutationStatus.Rejected, stale.Status); Assert.AreEqual("revision_conflict", stale.ErrorCode);
        var comment = await host.Client.MutateAsync(host.Mutation(new AtcAddComment(item.Identity.ItemId, updated.Item.Revision, "note")));
        Assert.AreEqual("note", comment.Item!.Comments.Single().Text);
        var closed = await host.Client.MutateAsync(host.Mutation(new AtcApplyTransition(item.Identity.ItemId, comment.Item.Revision, "abandon")));
        Assert.AreEqual("closed", closed.Item!.State.Id); Assert.AreEqual("abandoned", closed.Item.State.CompletionReason);
        var unavailable = await host.Client.MutateAsync(host.Mutation(new AtcApplyTransition(item.Identity.ItemId, closed.Item.Revision, "finish")));
        Assert.AreEqual("transition_unavailable", unavailable.ErrorCode);
    }

    [TestMethod]
    [DataRow(AtcRelationKind.Parent, AtcRelationKind.Child)]
    [DataRow(AtcRelationKind.Child, AtcRelationKind.Parent)]
    [DataRow(AtcRelationKind.Blocks, AtcRelationKind.BlockedBy)]
    [DataRow(AtcRelationKind.BlockedBy, AtcRelationKind.Blocks)]
    [DataRow(AtcRelationKind.Related, AtcRelationKind.Related)]
    public async Task RelationsHaveDistinctNativeMeaningsAndInverseEvidence(AtcRelationKind kind, AtcRelationKind inverse)
    {
        await using var host = new AtcTestHost(); await host.StartAsync();
        var first = await host.CreateAsync("first"); var second = await host.CreateAsync("second");
        var relation = new AtcRelation(kind, second.Identity);
        var result = await host.Client.MutateAsync(host.Mutation(new AtcAddRelation(first.Identity.ItemId, first.Revision, relation)));
        Assert.AreEqual(relation, result.Item!.Relations.Single());
        var other = await host.Client.ReadAsync(new("other", second.Identity.ItemId));
        Assert.AreEqual(new AtcRelation(inverse, first.Identity), other.Relations.Single());
        Assert.IsTrue(other.RelationsComplete);
    }

    [TestMethod]
    public async Task HierarchyCyclesAndParentReplacementAreRejected()
    {
        await using var host = new AtcTestHost(); await host.StartAsync();
        var first = await host.CreateAsync(); var second = await host.CreateAsync(); var third = await host.CreateAsync();
        var result = await host.Client.MutateAsync(host.Mutation(new AtcAddRelation(first.Identity.ItemId, first.Revision, new(AtcRelationKind.Parent, second.Identity))));
        var other = await host.Client.ReadAsync(new("read", second.Identity.ItemId));
        var cycle = await host.Client.MutateAsync(host.Mutation(new AtcAddRelation(other.Identity.ItemId, other.Revision, new(AtcRelationKind.Parent, first.Identity))));
        Assert.AreEqual("relation_conflict", cycle.ErrorCode);
        var replace = await host.Client.MutateAsync(host.Mutation(new AtcAddRelation(first.Identity.ItemId, result.Item!.Revision, new(AtcRelationKind.Parent, third.Identity))));
        Assert.AreEqual("relation_conflict", replace.ErrorCode);
    }

    [TestMethod]
    public async Task PaginationCursorsBindSubjectTargetGrantPageSizeAndExpiry()
    {
        await using var host = new AtcTestHost(); await host.StartAsync();
        for (var i = 0; i < 3; i++) await host.CreateAsync();
        var page = await host.Client.ListAsync(new("first", 1)); Assert.IsNotNull(page.Cursor);
        var next = await host.Client.ListAsync(new("next", 1, page.Cursor));
        Assert.AreNotEqual(page.Items[0].Identity, next.Items[0].Identity);
        await AtcTestHost.Error("invalid_cursor", () => host.Client.ListAsync(new("size", 2, page.Cursor)));
        var otherTarget = await AtcClient.ConnectAsync(host.Mcp, host.Access.Repository);
        await AtcTestHost.Error("invalid_cursor", () => otherTarget.ListAsync(new("target", 1, page.Cursor)));
        host.Http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", host.Access.OtherToken);
        await AtcTestHost.Error("invalid_cursor", () => host.Client.ListAsync(new("subject", 1, page.Cursor)));
        host.Http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", host.Access.Token);
        host.Access.AuthorizationRevision = "grant-2";
        await AtcTestHost.Error("invalid_cursor", () => host.Client.ListAsync(new("grant", 1, page.Cursor)));
        host.Access.AuthorizationRevision = "grant-1"; host.Clock.Advance(TimeSpan.FromMinutes(6));
        await AtcTestHost.Error("invalid_cursor", () => host.Client.ListAsync(new("expired", 1, page.Cursor)));
    }

    [TestMethod]
    public async Task RevocationAppliesToReadDescribeAndRecovery()
    {
        await using var host = new AtcTestHost(); await host.StartAsync();
        var request = host.Mutation(new AtcCreate("once")); var receipt = await host.Client.MutateAsync(request);
        host.Access.Enabled = false;
        await AtcTestHost.Error("forbidden", () => host.Client.ReadAsync(new("read", receipt.Item!.Identity.ItemId)));
        await AtcTestHost.Error("forbidden", () => host.Client.MutateAsync(request with { Mode = AtcMutationMode.Recover }));
        await AtcTestHost.Error("forbidden", () => AtcClient.ConnectAsync(host.Mcp, host.Target));
    }

    [TestMethod]
    public async Task ServerRechecksWriteGrantRatherThanTrustingCachedDescription()
    {
        await using var host = new AtcTestHost(); await host.StartAsync(); host.Access.CanWrite = false;
        await AtcTestHost.Error("unsupported_operation", () => host.CreateAsync());
        var readonlyClient = await AtcClient.ConnectAsync(host.Mcp, host.Access.ReadOnly);
        Assert.IsFalse(readonlyClient.Description.Capabilities.Operations.Contains("create"));
    }

    [TestMethod]
    public async Task ForeignTargetsAndForgedContextAreDenied()
    {
        await using var host = new AtcTestHost(); await host.StartAsync(other: true);
        await AtcTestHost.Error("forbidden", () => AtcClient.ConnectAsync(host.Mcp, host.Access.Repository));
        await AtcTestHost.Error("forbidden", () => AtcClient.ConnectAsync(host.Mcp, host.Target with { Revision = "different-generation" }));
        var result = await host.RawAsync("describe", new { requestId = "forged", subject = "sample-subject" });
        Assert.IsTrue(result.IsError); Assert.AreEqual("invalid_request", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task InvalidDigestCrossTargetRelationsAndBoundsCannotReachEffects()
    {
        await using var host = new AtcTestHost(); await host.StartAsync(); var first = await host.CreateAsync();
        await AtcTestHost.Error("intent_conflict", () => host.Client.MutateAsync(host.Mutation(new AtcCreate("bad")) with { IntentDigest = "forged" }));
        await AtcTestHost.Error("limit_exceeded", () => host.Client.ListAsync(new("large", 101)));
        await AtcTestHost.Error("forbidden", () => host.Client.MutateAsync(host.Mutation(new AtcAddRelation(first.Identity.ItemId, first.Revision,
            new(AtcRelationKind.BlockedBy, new(host.Target.ProviderId, "repository", "foreign"))))));
        await AtcTestHost.Error("limit_exceeded", () => host.Client.MutateAsync(host.Mutation(new AtcCreate("large", new string('é', 32769)))));
    }

    [TestMethod]
    public async Task ConcurrentReplayCreatesOnceAndRevisionRaceHasOneWinner()
    {
        await using var host = new AtcTestHost(); await host.StartAsync();
        var create = host.Mutation(new AtcCreate("concurrent"));
        var receipts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => host.Client.MutateAsync(create with { RequestId = "attempt-" + i })));
        Assert.HasCount(1, receipts.Select(r => r.Item!.Identity).Distinct());
        var item = receipts[0].Item!;
        var race = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => host.Client.MutateAsync(host.Mutation(
            new AtcUpdate(item.Identity.ItemId, item.Revision, Title: "revision-" + i)))));
        Assert.AreEqual(1, race.Count(r => r.Status == AtcMutationStatus.Applied));
        Assert.AreEqual(1, race.Count(r => r.ErrorCode == "revision_conflict"));
    }

    [TestMethod]
    public async Task RestartFencesThePreviousProcessRecoveryAuthority()
    {
        AtcTargetRef previous;
        await using (var first = new AtcTestHost())
        {
            await first.StartAsync(); previous = first.Target; await first.CreateAsync();
        }
        await using var restarted = new AtcTestHost(); await restarted.StartAsync();
        Assert.AreNotEqual(previous.Revision, restarted.Target.Revision);
        await AtcTestHost.Error("forbidden", () => AtcClient.ConnectAsync(restarted.Mcp, previous));
        Assert.IsEmpty((await restarted.Client.ListAsync(new("new-process"))).Items);
    }

    [TestMethod]
    public async Task AlreadyCancelledAttemptDoesNotDispatchAnEffect()
    {
        await using var host = new AtcTestHost(); await host.StartAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => host.Client.MutateAsync(host.Mutation(new AtcCreate("cancelled")), cancellation.Token));
        Assert.IsEmpty((await host.Client.ListAsync(new("unchanged"))).Items);
    }
}
