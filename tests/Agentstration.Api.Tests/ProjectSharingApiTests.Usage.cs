using System.Net;
using System.Net.Http.Json;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ProjectSharingApiTests
{
    [TestMethod]
    public async Task TypedUsageSurvivesNativeGitLostResponseAndSecondInstanceRollupWithImmutableCorrection()
    {
        await using var native = await AuthenticatedGitFixture.CreateAsync();
        await using var left = await ConnectedHostAsync(native, "usage-left"); await using var right = await ConnectedHostAsync(native, "usage-right");
        var portable = new WorkSharingProjectId(Guid.NewGuid()); var participant = new WorkParticipantId(Guid.NewGuid());
        var lp = await CreateProjectAsync(left, portable, participant); var rp = await CreateProjectAsync(right, portable, new(Guid.NewGuid()));
        var id = new WorkUsageId(Guid.NewGuid()); var total = FlowRunTokenUsageAggregation.Invocation(13, 7, 23);
        var record = new WorkUsageRecord(id, id, 1, WorkPlanId.New(), 1, new string('a', 64), new(Guid.NewGuid()), 1, new string('b', 64),
            new(WorkUsageSourceKind.CodingCli, "coding-cli/1.0", Guid.NewGuid(), Guid.NewGuid(), "fixture-run", WorkPlanAttemptStatus.Failed)
            { AssignmentId = Guid.NewGuid(), AssignmentAttemptId = Guid.NewGuid(), InvocationId = Guid.NewGuid() },
            WorkUsageMeasurement.Measured, new(total, total) { CachedInputTokens = 3, ReasoningOutputTokens = 2 }, DateTimeOffset.UnixEpoch)
        { ProjectId = portable, ParticipantId = participant };
        using (left.Scope())
        {
            var repository = left.Host.Services.GetRequiredService<IWorkUsageRepository>();
            Assert.AreEqual(WorkUsageSaveStatus.Saved, await repository.CaptureAsync([new(new(left.Context.WorkspaceId), left.Context.PrincipalId,
                new(lp.Id), record, WorkUsageFormat.Digest(record), DateTimeOffset.UnixEpoch)], default));
        }
        var url = left.Root(lp.Id) + $"/usage/{id}/publication";
        native.LoseNextWriteResponse = true;
        using var lost = await left.Http.PostAsync(url, null);
        Assert.AreEqual(HttpStatusCode.Accepted, lost.StatusCode, await lost.Content.ReadAsStringAsync());
        Assert.AreEqual(WorkSharingOperationStatus.Uncertain, (await lost.Content.ReadFromJsonAsync<WorkSharingOperationResponse>())!.Status);
        using (left.Scope())
        {
            var projects = left.Host.Services.GetRequiredService<WorkProjectService>();
            var owned = (await projects.GetAsync(new(left.Context.WorkspaceId), new(lp.Id), default)).Value!;
            var edited = await projects.UpdateAsync(owned.WorkspaceId, owned.Id, new(Guid.NewGuid(), owned.Version,
                owned.Definition with { DisplayName = "Unrelated private Project edit" }), default);
            Assert.IsTrue(edited.IsSuccess, edited.ErrorCode);
        }
        native.Unavailable = false; await left.RestartAsync();
        using var recovered = await left.Http.PostAsync(url, null);
        Assert.AreEqual(HttpStatusCode.Accepted, recovered.StatusCode, await recovered.Content.ReadAsStringAsync());
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, (await recovered.Content.ReadFromJsonAsync<WorkSharingOperationResponse>())!.Status);
        using (left.Scope()) Assert.AreEqual("Unrelated private Project edit", (await left.Host.Services.GetRequiredService<WorkProjectService>().GetAsync(new(left.Context.WorkspaceId), new(lp.Id), default)).Value!.Definition.DisplayName);
        var revision = (await native.GitAsync(native.RepositoryPath, "rev-parse", "main")).Trim();
        using var replay = await left.Http.PostAsync(url, null); Assert.AreEqual(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.AreEqual(revision, (await native.GitAsync(native.RepositoryPath, "rev-parse", "main")).Trim());
        await PullAsync(right, rp);
        using var localSummary = await left.Http.PostAsJsonAsync(left.Root(lp.Id) + "/usage/summary", new WorkUsageSummaryQuery([]));
        using var sharedSummary = await right.Http.PostAsJsonAsync(right.Root(rp.Id) + "/usage/summary", new WorkUsageSummaryQuery([], Source: WorkUsageReadSource.Shared));
        Assert.AreEqual(HttpStatusCode.OK, sharedSummary.StatusCode, await sharedSummary.Content.ReadAsStringAsync());
        var local = (await localSummary.Content.ReadFromJsonAsync<WorkUsageSummary>())!; var shared = (await sharedSummary.Content.ReadFromJsonAsync<WorkUsageSummary>())!;
        Assert.AreEqual(local.Measured, shared.Measured); Assert.AreEqual(23L, shared.Measured.Inclusive.TotalTokens);
        Assert.AreEqual(1, shared.Measured.Inclusive.InvocationCount); Assert.AreEqual(3L, shared.Measured.CachedInputTokens);
        var adjusted = FlowRunTokenUsageAggregation.Invocation(14, 7, null);
        using var correction = await left.Http.PostAsJsonAsync(left.Root(lp.Id) + $"/usage/{id}/corrections", new CorrectWorkUsageRequest(Guid.NewGuid(), id.Value,
            WorkUsageFormat.Digest(record), WorkUsageMeasurement.Estimated, new(adjusted, adjusted), "provider_adjustment", "receipt:fixture"));
        Assert.AreEqual(HttpStatusCode.OK, correction.StatusCode, await correction.Content.ReadAsStringAsync());
        var corrected = (await correction.Content.ReadFromJsonAsync<WorkUsageEntry>())!;
        using var publication = await left.Http.PostAsync(left.Root(lp.Id) + $"/usage/{corrected.Record.Id}/publication", null);
        Assert.AreEqual(HttpStatusCode.Accepted, publication.StatusCode, await publication.Content.ReadAsStringAsync());
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, (await publication.Content.ReadFromJsonAsync<WorkSharingOperationResponse>())!.Status);
        var state = await PullAsync(right, rp); Assert.HasCount(2, state.Records);
        using var after = await right.Http.PostAsJsonAsync(right.Root(rp.Id) + "/usage/summary", new WorkUsageSummaryQuery([], Source: WorkUsageReadSource.Shared));
        var result = (await after.Content.ReadFromJsonAsync<WorkUsageSummary>())!;
        Assert.AreEqual(1, result.OriginalRecords); Assert.AreEqual(1, result.Corrections); Assert.IsNull(result.Measured.Inclusive.TotalTokens);
        Assert.AreEqual(14L, result.Estimated.Inclusive.InputTokens); Assert.IsNull(result.Estimated.Inclusive.TotalTokens);
        using var foreign = await right.Http.GetAsync(right.Root(rp.Id) + $"/usage/{id}"); Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
    }
}
