using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Tests;

public sealed partial class RuntimeWorkerAssignmentTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ExplicitDurableFlowRecoveryRetainsAssignmentAndFencesTheFailedOwner(bool leaseLost)
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var runId = $"flowrun-{Guid.NewGuid():N}";
        var created = await fixture.Service.CreateAsync(Workspace, Guid.NewGuid(), RuntimeAssignmentTargetKind.FlowRun,
            runId, "microsoft-agent-framework", "1.0", RuntimeExecutionMaterialVersions.DurableGraph, "material", "sha256:flow", default);
        async Task<ClaimedRuntimeWorkerAssignment> ClaimAsync() => await fixture.Service.ClaimNextAsync(
            new(Guid.NewGuid()), new(Guid.NewGuid()), "microsoft-agent-framework", new HashSet<string> { "1.0" },
            new HashSet<string> { RuntimeExecutionMaterialVersions.DurableGraph }, 1, default)
            ?? throw new AssertFailedException("Expected a durable assignment claim.");
        var first = await ClaimAsync();
        if (leaseLost)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(46));
            Assert.HasCount(1, await fixture.Service.ExpireLeasesAsync(10, default));
        }
        else await fixture.Service.CompleteAsync(first.Ownership,
            new(RuntimeAssignmentTerminalOutcome.Failed, Guid.NewGuid(), ErrorCode: "worker_execution_failed"), default);
        await Assert.ThrowsAsync<RuntimeAssignmentException>(() => fixture.Service.RequeueAsync(Workspace, created.Value.Id, default));
        await Assert.ThrowsAsync<RuntimeAssignmentException>(() => fixture.Service.RequeueInterruptedFlowExactAsync(
            ResourceScopeRef.Workspace(Guid.NewGuid()), created.Value.Id, default));
        var pending = await fixture.Service.RequeueInterruptedFlowExactAsync(ResourceScopeRef.Workspace(Workspace.Value), created.Value.Id, default);
        var second = await ClaimAsync();
        Assert.AreEqual(created.Value.Id, second.Assignment.Id);
        Assert.AreEqual(RuntimeAssignmentState.Pending, pending.Value.State);
        Assert.HasCount(2, second.Assignment.Attempts);
        Assert.AreEqual(first.Ownership.FencingGeneration + 1, second.Ownership.FencingGeneration);
        var stale = await Assert.ThrowsAsync<RuntimeAssignmentException>(() => fixture.Service.HeartbeatAsync(first.Ownership, default));
        Assert.AreEqual(RuntimeAssignmentErrorCodes.NotOwned, stale.Code);
    }

    [TestMethod]
    [DataRow("1.0", false)]
    [DataRow("1.2", true)]
    public async Task InterruptedFlowRecoveryRefusesOldMaterialAndExplicitCancellation(string material, bool cancelled)
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(Workspace, Guid.NewGuid(), RuntimeAssignmentTargetKind.FlowRun,
            $"flowrun-{Guid.NewGuid():N}", "microsoft-agent-framework", "1.0", material, "material", "sha256:flow", default);
        var claim = (await fixture.Service.ClaimNextAsync(new(Guid.NewGuid()), new(Guid.NewGuid()), "microsoft-agent-framework",
            new HashSet<string> { "1.0" }, new HashSet<string> { material }, 1, default))!;
        if (cancelled) await fixture.Service.RequestCancellationAsync(Workspace, created.Value.Id, default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(46));
        Assert.HasCount(1, await fixture.Service.ExpireLeasesAsync(10, default));
        var rejected = await Assert.ThrowsAsync<RuntimeAssignmentException>(() => fixture.Service.RequeueInterruptedFlowExactAsync(
            ResourceScopeRef.Workspace(Workspace.Value), created.Value.Id, default));
        Assert.AreEqual(RuntimeAssignmentErrorCodes.TerminalConflict, rejected.Code);
        Assert.AreEqual(cancelled ? RuntimeAssignmentState.Cancelled : RuntimeAssignmentState.Failed,
            (await fixture.Assignments.GetAsync(Workspace, created.Value.Id, default))!.Value.State);
        Assert.IsNotNull(claim);
    }
}
