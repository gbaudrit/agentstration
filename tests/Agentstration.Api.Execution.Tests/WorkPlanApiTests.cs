using System.Net;
using System.Net.Http.Json;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.Api.Tests;

[TestClass]
public sealed class WorkPlanApiTests
{
    [TestMethod]
    public async Task WorkApiCreatesValidatesActivatesAndRevisesPlansWithImmutableEvidence()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var root = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid(); var planId = Guid.NewGuid();
        using var tree = await client.PostAsJsonAsync("/api/workspaces/default/tasks/tree", new CreateWorkTaskTreeRequest(root,
            [new(root, WorkTaskKind.Composite, "Delivery", "Deliver reviewed work"),
             new(first, WorkTaskKind.Atomic, "Collect", "Collect evidence", root),
             new(second, WorkTaskKind.Atomic, "Summarize", "Summarize accepted evidence", root)]));
        Assert.AreEqual(HttpStatusCode.Created, tree.StatusCode);
        var request = new CreateWorkPlanRequest(planId, Guid.NewGuid(), new("Collect before summarizing", [first, second], [new(second, first)]));
        using var created = await client.PostAsJsonAsync("/api/workspaces/default/work-plans", request);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var draft = (await created.Content.ReadFromJsonAsync<WorkPlanResponse>())!;
        Assert.AreEqual($"/api/workspaces/{draft.WorkspaceId}/work-plans/{planId}", created.Headers.Location!.ToString());
        Assert.AreEqual(planId, (await client.GetFromJsonAsync<WorkPlanResponse>(created.Headers.Location))!.Id);
        Assert.AreEqual(WorkPlanStatus.Draft, draft.Status);
        Assert.IsTrue(draft.Tasks.All(task => task.Readiness == WorkPlanTaskReadiness.Blocked));
        var prefix = $"/api/workspaces/default/work-plans/{planId}";
        using var unvalidated = await client.PostAsJsonAsync(prefix + "/activate", Transition(draft));
        Assert.AreEqual(HttpStatusCode.Conflict, unvalidated.StatusCode);
        var validateCommand = Transition(draft);
        using var validatedResponse = await client.PostAsJsonAsync(prefix + "/validate", validateCommand);
        Assert.AreEqual(HttpStatusCode.OK, validatedResponse.StatusCode);
        var validated = (await validatedResponse.Content.ReadFromJsonAsync<WorkPlanResponse>())!;
        var activation = Transition(validated);
        using var activeResponse = await client.PostAsJsonAsync(prefix + "/activate", activation);
        Assert.AreEqual(HttpStatusCode.OK, activeResponse.StatusCode);
        var active = (await activeResponse.Content.ReadFromJsonAsync<WorkPlanResponse>())!;
        Assert.AreEqual(WorkPlanTaskReadiness.Ready, active.Tasks.Single(task => task.TaskId == first).Readiness);
        Assert.AreEqual(WorkPlanTaskReadiness.Blocked, active.Tasks.Single(task => task.TaskId == second).Readiness);
        CollectionAssert.Contains(active.Tasks.Single(task => task.TaskId == second).UnmetDependencies.ToArray(), first);
        Assert.IsTrue(active.Tasks.All(task => task.Version == 1));
        using var replay = await client.PostAsJsonAsync(prefix + "/activate", activation);
        Assert.AreEqual(active.Version, (await replay.Content.ReadFromJsonAsync<WorkPlanResponse>())!.Version);
        using var stale = await client.PostAsJsonAsync(prefix + "/deactivate", activation with { CommandId = Guid.NewGuid() });
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        using var accepted = await client.PostAsJsonAsync($"/api/workspaces/default/tasks/{first}/outcome",
            new RecordWorkTaskOutcomeRequest(Guid.NewGuid(), 1, WorkTaskOutcome.Accepted, "Reviewed source evidence"));
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        var ready = (await client.GetFromJsonAsync<WorkPlanResponse>(prefix))!;
        Assert.AreEqual(WorkPlanTaskReadiness.Ready, ready.Tasks.Single(task => task.TaskId == second).Readiness);
        var work = (await client.GetFromJsonAsync<WorkTaskResponse>($"/api/workspaces/default/tasks/{second}"))!;
        Assert.IsNull(work.FlowRunId);
        var revise = new ReviseWorkPlanRequest(Guid.NewGuid(), ready.Version, new("Revised remaining objective", [second], []));
        using var activeEdit = await client.PostAsJsonAsync(prefix + "/revisions", revise);
        Assert.AreEqual(HttpStatusCode.Conflict, activeEdit.StatusCode);
        using var inactiveResponse = await client.PostAsJsonAsync(prefix + "/deactivate", Transition(ready));
        var inactive = (await inactiveResponse.Content.ReadFromJsonAsync<WorkPlanResponse>())!;
        revise = revise with { ExpectedVersion = inactive.Version };
        using var revisedResponse = await client.PostAsJsonAsync(prefix + "/revisions", revise);
        Assert.AreEqual(HttpStatusCode.Created, revisedResponse.StatusCode, await revisedResponse.Content.ReadAsStringAsync());
        var revised = (await revisedResponse.Content.ReadFromJsonAsync<WorkPlanResponse>())!;
        Assert.AreEqual(2, revised.CurrentRevision);
        Assert.AreEqual(WorkPlanStatus.Draft, revised.Status);
        Assert.IsNull(revised.ValidatedRevision);
        var original = (await client.GetFromJsonAsync<WorkPlanRevisionResponse>(prefix + "/revisions/1"))!;
        Assert.AreEqual(draft.Revision.Digest, original.Digest);
        Assert.AreEqual(request.Definition!.Intent, original.Intent);
        Assert.HasCount(2, original.TaskIds);
        var receipt = (await client.GetFromJsonAsync<WorkPlanCommandResponse>(prefix + $"/commands/{revise.CommandId}"))!;
        Assert.AreEqual(revised.Version, receipt.PlanVersion);
        Assert.AreEqual(2, receipt.Revision);
        Assert.HasCount(2, receipt.ObservedTasks);
        using var createReplay = await client.PostAsJsonAsync("/api/workspaces/default/work-plans", request);
        Assert.AreEqual(revised.Version, (await createReplay.Content.ReadFromJsonAsync<WorkPlanResponse>())!.Version);
        using var missingRevision = await client.GetAsync(prefix + "/revisions/3");
        Assert.AreEqual(HttpStatusCode.NotFound, missingRevision.StatusCode);
    }

    [TestMethod]
    public async Task WorkApiRejectsCyclesInvalidCommandsAndCrossWorkspaceReferences()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var planId = Guid.NewGuid();
        using var tree = await client.PostAsJsonAsync("/api/workspaces/default/tasks/tree", new CreateWorkTaskTreeRequest(first,
            [new(first, WorkTaskKind.Composite, "Group", "Group intent"), new(second, WorkTaskKind.Atomic, "Child", "Child intent", first)]));
        Assert.AreEqual(HttpStatusCode.Created, tree.StatusCode);
        var request = new CreateWorkPlanRequest(planId, Guid.NewGuid(), new("Cycle", [first, second], [new(first, second), new(second, first)]));
        using var cycle = await client.PostAsJsonAsync("/api/workspaces/default/work-plans", request);
        Assert.AreEqual(HttpStatusCode.BadRequest, cycle.StatusCode);
        StringAssert.Contains(await cycle.Content.ReadAsStringAsync(), "work_plan_dependency_cycle");
        using var missing = await client.GetAsync($"/api/workspaces/default/work-plans/{planId}");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        using var malformed = await client.PostAsJsonAsync("/api/workspaces/default/work-plans", request with { Definition = null });
        Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var unknownTask = await client.PostAsJsonAsync("/api/workspaces/default/work-plans", request with
        { Definition = new("Unavailable member", [Guid.NewGuid()], []) });
        Assert.AreEqual(HttpStatusCode.NotFound, unknownTask.StatusCode);
        using var foreign = await client.PostAsJsonAsync($"/api/workspaces/{Guid.NewGuid()}/work-plans", request with
        { Definition = new("Foreign scope", [first, second], []) });
        Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var invalidTransition = await client.PostAsJsonAsync($"/api/workspaces/default/work-plans/{planId}/validate",
            new TransitionWorkPlanRequest(Guid.Empty, 0, 0, ""));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidTransition.StatusCode);
        using var foreignRead = await client.GetAsync($"/api/workspaces/{Guid.NewGuid()}/work-plans/{planId}/revisions/1");
        Assert.AreEqual(HttpStatusCode.Forbidden, foreignRead.StatusCode);
    }

    private static TransitionWorkPlanRequest Transition(WorkPlanResponse plan) => new(Guid.NewGuid(), plan.Version, plan.CurrentRevision, plan.Revision.Digest);
    private static WebApplicationFactory<global::Program> Factory() => new WebApplicationFactory<global::Program>()
        .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
}
