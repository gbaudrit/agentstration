using System.Net;
using System.Net.Http.Json;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.Api.Tests;

[TestClass]
public sealed class WorkProjectApiTests
{
    [TestMethod]
    public async Task ProjectApiCreatesListsAssociatesWorkAndRejectsStaleForeignOrInvalidChanges()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        var create = new CreateWorkProjectRequest(id, Guid.NewGuid(), "operations", new("Operations", null, [], []));
        using var created = await client.PostAsJsonAsync("/api/workspaces/default/projects", create);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var project = (await created.Content.ReadFromJsonAsync<WorkProjectResponse>())!;
        Assert.HasCount(0, project.PlanIds);
        var context = (await client.GetFromJsonAsync<WorkProjectContextResponse>($"/api/workspaces/default/projects/{id}/context"))!;
        Assert.AreEqual(project.Version, context.ProjectVersion); Assert.HasCount(0, context.Bindings);
        var root = Guid.NewGuid(); var task = Guid.NewGuid();
        using var tree = await client.PostAsJsonAsync("/api/workspaces/default/tasks/tree", new CreateWorkTaskTreeRequest(root,
            [new(root, WorkTaskKind.Composite, "Objective", "Deliver objective"), new(task, WorkTaskKind.Atomic, "Child", "Do local work", root)]));
        Assert.AreEqual(HttpStatusCode.Created, tree.StatusCode);
        var plans = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var planId in plans)
        {
            using var response = await client.PostAsJsonAsync("/api/workspaces/default/work-plans", new CreateWorkPlanRequest(planId, Guid.NewGuid(), new("Local work", [task], [])));
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        var url = "/api/workspaces/default/projects/" + id;
        var update = new UpdateWorkProjectRequest(Guid.NewGuid(), project.Version, new("Operations", "Associated work", plans, [task]));
        using var edited = await client.PutAsJsonAsync(url, update);
        Assert.AreEqual(HttpStatusCode.OK, edited.StatusCode, await edited.Content.ReadAsStringAsync());
        project = (await edited.Content.ReadFromJsonAsync<WorkProjectResponse>())!;
        CollectionAssert.AreEquivalent(plans, project.PlanIds.ToArray());
        Assert.AreEqual(task, project.TaskIds.Single());
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(project),
            System.Text.Json.JsonSerializer.Serialize(await client.GetFromJsonAsync<WorkProjectResponse>(created.Headers.Location)));
        var page = (await client.GetFromJsonAsync<WorkProjectPageResponse>("/api/workspaces/default/projects?skip=0&take=1"))!;
        Assert.AreEqual(id, page.Items.Single().Id); Assert.IsFalse(page.HasMore);
        using var replay = await client.PutAsJsonAsync(url, update);
        Assert.AreEqual(project.Version, (await replay.Content.ReadFromJsonAsync<WorkProjectResponse>())!.Version);
        using var stale = await client.PutAsJsonAsync(url, update with { CommandId = Guid.NewGuid() });
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        using var foreign = await client.GetAsync($"/api/workspaces/{Guid.NewGuid()}/projects/{id}");
        Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var missingWork = await client.PutAsJsonAsync(url, new UpdateWorkProjectRequest(Guid.NewGuid(), project.Version,
            new("Operations", null, [Guid.NewGuid()], [])));
        Assert.AreEqual(HttpStatusCode.NotFound, missingWork.StatusCode);
        using var invalid = await client.PostAsJsonAsync("/api/workspaces/default/projects", create with { Id = Guid.NewGuid(), Name = "../escape" });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var invalidPage = await client.GetAsync("/api/workspaces/default/projects?take=101");
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidPage.StatusCode);
        using var unavailable = await client.PostAsJsonAsync("/api/workspaces/default/projects", create with
        {
            Id = Guid.NewGuid(),
            Name = "missing-binding",
            Definition = create.Definition! with
            {
                Bindings = [new("sharing", WorkProjectBindingPurpose.Sharing, new(new(
                    Agentstration.Resources.ResourceScopeRef.Workspace(project.WorkspaceId), default, "ToolProvider", "missing-provider"), Guid.NewGuid(), 1))]
            }
        });
        Assert.AreEqual(HttpStatusCode.NotFound, unavailable.StatusCode, await unavailable.Content.ReadAsStringAsync());
        var selectedScope = Agentstration.Resources.ResourceScopeRef.Workspace(project.WorkspaceId);
        var tracking = new WorkProjectContextBinding("tracking", WorkProjectBindingPurpose.Tracking,
            new(new(selectedScope, default, "Tool", "missing-describe"), Guid.NewGuid(), 1));
        using var missingSelection = await client.PostAsJsonAsync("/api/workspaces/default/projects", create with
        {
            Id = Guid.NewGuid(),
            Name = "missing-selection",
            Definition = create.Definition! with { Bindings = [tracking] }
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, missingSelection.StatusCode);
        tracking = tracking with
        {
            Contract = new(new(new(selectedScope, default, "ToolProvider", "tracker"), Guid.NewGuid(), 1),
            new(new(selectedScope, default, "ExtensionRegistration", "tracker"), Guid.NewGuid(), 1),
            "1.0", "tracker-authority", "standalone-tracker", "binding", "revision-1", ["list"])
        };
        using var missingTarget = await client.PostAsJsonAsync("/api/workspaces/default/projects", create with
        {
            Id = Guid.NewGuid(),
            Name = "missing-contract-target",
            Definition = create.Definition! with { Bindings = [tracking] }
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, missingTarget.StatusCode, await missingTarget.Content.ReadAsStringAsync());
    }
    [TestMethod]
    public async Task ProjectApiPersistsPortableNeedsAndReportsUnconfiguredWithoutProviderDiscovery()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var need = new WorkProjectConnectionRequirement("backend", WorkProjectConnectionRules.Forge, "1.0", ["read"], new("authority", "backend"));
        var id = Guid.NewGuid();
        var create = new CreateWorkProjectRequest(id, Guid.NewGuid(), "portable", new("Portable project", null, [], []) { ConnectionRequirements = [need, need with { Name = "frontend" }] });
        using var created = await client.PostAsJsonAsync("/api/workspaces/default/projects", create);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var project = (await created.Content.ReadFromJsonAsync<WorkProjectResponse>())!;
        Assert.HasCount(2, project.ConnectionRequirements); Assert.HasCount(0, project.ConnectionAssignments);
        var context = (await client.GetFromJsonAsync<WorkProjectContextResponse>($"/api/workspaces/default/projects/{id}/context"))!;
        Assert.HasCount(2, context.Connections);
        Assert.IsTrue(context.Connections.All(value => !value.Available && value.Code == "work_project_connection_unconfigured" && value.Service is null));
        using var replayConflict = await client.PostAsJsonAsync("/api/workspaces/default/projects", create with { Definition = create.Definition! with { ConnectionRequirements = [need] } });
        Assert.AreEqual(HttpStatusCode.Conflict, replayConflict.StatusCode);
        using var duplicate = await client.PutAsJsonAsync($"/api/workspaces/default/projects/{id}", new UpdateWorkProjectRequest(Guid.NewGuid(), project.Version,
            create.Definition! with { ConnectionRequirements = [need, need] }));
        Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

}
