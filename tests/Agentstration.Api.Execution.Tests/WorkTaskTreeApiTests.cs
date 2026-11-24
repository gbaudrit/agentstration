using System.Net;
using System.Net.Http.Json;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.Api.Tests;

[TestClass]
public sealed class WorkTaskTreeApiTests
{
    [TestMethod]
    public async Task TaskTreeApiCreatesReplaysAndReadsWithoutExecuting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-task-tree-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var root = Guid.NewGuid();
            var child = Guid.NewGuid();
            var request = new CreateWorkTaskTreeRequest(root,
                [new(root, WorkTaskKind.Composite, "Assessment", "Assess evidence"),
                 new(child, WorkTaskKind.Atomic, "Collect", "Collect evidence", root)]);
            await using (var factory = Factory(directory))
            {
                using var client = factory.CreateClient();
                using var created = await client.PostAsJsonAsync("/api/workspaces/default/tasks/tree", request);
                Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
                var tree = await created.Content.ReadFromJsonAsync<WorkTaskTreeResponse>();
                Assert.IsNotNull(tree);
                Assert.HasCount(2, tree.Tasks);
                Assert.IsTrue(tree.Tasks.All(task => task.Status == WorkItemStatus.Pending));
                using var replay = await client.PostAsJsonAsync("/api/workspaces/default/tasks/tree", request);
                Assert.AreEqual(HttpStatusCode.Created, replay.StatusCode);
                using var conflict = await client.PostAsJsonAsync("/api/workspaces/default/tasks/tree",
                    request with { Tasks = [new(root, WorkTaskKind.Atomic, "Assessment", "Different intent")] });
                Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);
                var task = await client.GetFromJsonAsync<WorkTaskResponse>($"/api/workspaces/default/tasks/{child}");
                Assert.AreEqual(child, task?.Id);
                Assert.IsNull(task?.FlowRunId);
                var loaded = await client.GetFromJsonAsync<WorkTaskTreeResponse>($"/api/workspaces/default/tasks/{root}/tree");
                Assert.IsNotNull(loaded);
                Assert.HasCount(2, loaded.Tasks);
                Assert.AreEqual(root, loaded.Tasks.Single(task => task.Id == child).ParentTaskId);
                var subtree = await client.GetFromJsonAsync<WorkTaskTreeResponse>($"/api/workspaces/default/tasks/{child}/tree");
                Assert.HasCount(1, subtree!.Tasks);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task TaskTreeApiRejectsInvalidHierarchyAndForeignWorkspace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-task-tree-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var factory = Factory(directory);
            using var client = factory.CreateClient();
            var root = Guid.NewGuid();
            using var invalid = await client.PostAsJsonAsync("/api/workspaces/default/tasks/tree",
                new CreateWorkTaskTreeRequest(root, [new(root, WorkTaskKind.Atomic, "Root", "Work"),
                    new(Guid.NewGuid(), WorkTaskKind.Atomic, "Child", "More work", root)]));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            StringAssert.Contains(await invalid.Content.ReadAsStringAsync(), "atomic_task_cannot_have_children");
            using var malformed = await client.PostAsJsonAsync("/api/workspaces/default/tasks/tree", new CreateWorkTaskTreeRequest(root, [null]));
            Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
            using var missing = await client.GetAsync($"/api/workspaces/default/tasks/{root}/tree");
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            using var foreign = await client.PostAsJsonAsync($"/api/workspaces/{Guid.NewGuid()}/tasks/tree",
                new CreateWorkTaskTreeRequest(root, [new(root, WorkTaskKind.Atomic, "Task", "Work")]));
            Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
            var outcome = new RecordWorkTaskOutcomeRequest(Guid.NewGuid(), 1, WorkTaskOutcome.Accepted, "Reviewed evidence");
            using var missingOutcome = await client.PostAsJsonAsync($"/api/workspaces/default/tasks/{root}/outcome", outcome);
            Assert.AreEqual(HttpStatusCode.NotFound, missingOutcome.StatusCode);
            using var invalidOutcome = await client.PostAsJsonAsync($"/api/workspaces/default/tasks/{root}/outcome", outcome with { Evidence = " " });
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidOutcome.StatusCode);
            using var foreignOutcome = await client.PostAsJsonAsync($"/api/workspaces/{Guid.NewGuid()}/tasks/{root}/outcome", outcome);
            Assert.AreEqual(HttpStatusCode.Forbidden, foreignOutcome.StatusCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task OutcomeApiConsolidatesEvidenceAndReplaysWithoutChangingExecutionState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-task-outcome-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var factory = Factory(directory);
            using var client = factory.CreateClient();
            var root = Guid.NewGuid();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var optional = Guid.NewGuid();
            using var created = await client.PostAsJsonAsync("/api/workspaces/default/tasks/tree", new CreateWorkTaskTreeRequest(root,
                [new(root, WorkTaskKind.Composite, "Review", "Review results"),
                 new(first, WorkTaskKind.Atomic, "First", "First result", root),
                 new(second, WorkTaskKind.Atomic, "Second", "Second result", root),
                 new(optional, WorkTaskKind.Atomic, "Optional", "Additional result", root, IsOptional: true)]));
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
            var command = new RecordWorkTaskOutcomeRequest(Guid.NewGuid(), 1, WorkTaskOutcome.Accepted, "Reviewed first evidence");
            using var recorded = await client.PostAsJsonAsync($"/api/workspaces/default/tasks/{first}/outcome", command);
            Assert.AreEqual(HttpStatusCode.OK, recorded.StatusCode, await recorded.Content.ReadAsStringAsync());
            var partial = (await recorded.Content.ReadFromJsonAsync<WorkTaskTreeResponse>())!;
            Assert.AreEqual(WorkTaskOutcome.Pending, partial.Tasks.Single(task => task.Id == root).Acceptance!.Outcome);
            using var replay = await client.PostAsJsonAsync($"/api/workspaces/default/tasks/{first}/outcome", command);
            Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode);
            var replayed = (await replay.Content.ReadFromJsonAsync<WorkTaskTreeResponse>())!;
            Assert.AreEqual(partial.Tasks.Single(task => task.Id == root).Version, replayed.Tasks.Single(task => task.Id == root).Version);
            using var derived = await client.PostAsJsonAsync($"/api/workspaces/default/tasks/{root}/outcome", command);
            Assert.AreEqual(HttpStatusCode.Conflict, derived.StatusCode);
            using var reused = await client.PostAsJsonAsync($"/api/workspaces/default/tasks/{second}/outcome", command);
            Assert.AreEqual(HttpStatusCode.Conflict, reused.StatusCode);
            using var stale = await client.PostAsJsonAsync($"/api/workspaces/default/tasks/{second}/outcome",
                command with { EventId = Guid.NewGuid(), ExpectedVersion = 999 });
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            using var completed = await client.PostAsJsonAsync($"/api/workspaces/default/tasks/{second}/outcome",
                command with { EventId = Guid.NewGuid(), Evidence = "Reviewed second evidence" });
            Assert.AreEqual(HttpStatusCode.OK, completed.StatusCode);
            var accepted = (await completed.Content.ReadFromJsonAsync<WorkTaskTreeResponse>())!;
            var consolidated = accepted.Tasks.Single(task => task.Id == root);
            Assert.AreEqual(WorkTaskOutcome.Accepted, consolidated.Acceptance!.Outcome);
            Assert.HasCount(2, consolidated.Acceptance.RequiredChildren);
            CollectionAssert.AreEquivalent(new[] { first, second }, consolidated.Acceptance.RequiredChildren.Select(child => child.TaskId).ToArray());
            Assert.IsTrue(accepted.Tasks.All(task => task.Status == WorkItemStatus.Pending));
            using var optionalFailure = await client.PostAsJsonAsync($"/api/workspaces/default/tasks/{optional}/outcome",
                new RecordWorkTaskOutcomeRequest(Guid.NewGuid(), 1, WorkTaskOutcome.Failed, "Optional evidence rejected"));
            Assert.AreEqual(HttpStatusCode.OK, optionalFailure.StatusCode);
            var loaded = (await client.GetFromJsonAsync<WorkTaskTreeResponse>($"/api/workspaces/default/tasks/{root}/tree"))!;
            Assert.AreEqual(WorkTaskOutcome.Accepted, loaded.Tasks.Single(task => task.Id == root).Acceptance!.Outcome);
            Assert.IsTrue(loaded.Tasks.Single(task => task.Id == optional).IsOptional);
            Assert.AreEqual("Reviewed first evidence", loaded.Tasks.Single(task => task.Id == first).Acceptance!.Evidence!.Evidence);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static WebApplicationFactory<global::Program> Factory(string directory) =>
        new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Data:Directory", directory);
        });
}
