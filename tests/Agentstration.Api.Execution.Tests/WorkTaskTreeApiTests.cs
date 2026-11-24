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
