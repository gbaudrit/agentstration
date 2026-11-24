using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Api.Tests;

[TestClass]
public sealed class WorkPlanExecutionApiTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeterministicRootFlowsAdvanceOnceAfterRestartAndExplicitReview(bool fail)
    {
        var directory = Path.Combine(Path.GetTempPath(), "work-plan-execution-" + Guid.NewGuid().ToString("N"));
        var root = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid(); var planId = Guid.NewGuid();
        var prefix = $"/api/workspaces/default/work-plans/{planId}";
        try
        {
            await using (var factory = Factory(directory))
            {
                using var client = factory.CreateClient();
                var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
                using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context))
                {
                    var flows = factory.Services.GetRequiredService<FlowService>();
                    var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
                    var graph = new FlowGraphDefinition
                    {
                        EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" },
                            new OutputFlowStepDefinition { Name = "done", Outcome = fail ? FlowOutputOutcome.Error : FlowOutputOutcome.Success }],
                        Transitions = [new("done", "input", "completed", "done")]
                    };
                    var model = new WorkflowFlowDefinition("input", [new("input", FlowNodeKind.Input), new("done", FlowNodeKind.Output)], [new("input", "done")], ["done"]);
                    var flow = await flows.CreateExactAsync(scope, new("plan-fixture", null, "1.0.0", true, model, Graph: graph), ResourceNamespace.Default, default);
                    await flows.PublishVersionExactAsync(scope, flow.Value.Id, "1.0.0", true, default);
                }
                await using var worker = await AwpTestWorker.StartAsync(factory);
                using var tree = await client.PostAsJsonAsync("/api/workspaces/default/tasks/tree", new CreateWorkTaskTreeRequest(root,
                    [new(root, WorkTaskKind.Composite, "Assessment", "Assess evidence"), new(first, WorkTaskKind.Atomic, "Collect", "Collect evidence", root),
                        new(second, WorkTaskKind.Atomic, "Summarize", "Summarize evidence", root)]));
                Assert.AreEqual(HttpStatusCode.Created, tree.StatusCode);
                using var created = await client.PostAsJsonAsync("/api/workspaces/default/work-plans", new CreateWorkPlanRequest(planId, Guid.NewGuid(),
                    new("Collect then summarize", [first, second], [new(second, first)])));
                var plan = (await created.Content.ReadFromJsonAsync<WorkPlanResponse>())!;
                using var validated = await client.PostAsJsonAsync(prefix + "/validate", Transition(plan));
                plan = (await validated.Content.ReadFromJsonAsync<WorkPlanResponse>())!;
                using var activated = await client.PostAsJsonAsync(prefix + "/activate", Transition(plan));
                plan = (await activated.Content.ReadFromJsonAsync<WorkPlanResponse>())!;
                using var started = await client.PostAsJsonAsync(prefix + "/execution", new StartWorkPlanExecutionRequest(Guid.NewGuid(), plan.Version,
                    plan.CurrentRevision, plan.Revision.Digest, [Target(first), Target(second)]));
                Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode, await started.Content.ReadAsStringAsync());
                plan = await WaitAsync(client, prefix, plan => plan.Attempts.Count == 1
                    && plan.Attempts[0].Status == (fail ? WorkPlanAttemptStatus.Failed : WorkPlanAttemptStatus.Completed));
                Assert.AreEqual(WorkTaskOutcome.Pending, plan.Tasks.Single(task => task.TaskId == first).Outcome);
                Assert.AreEqual(WorkPlanTaskReadiness.Blocked, plan.Tasks.Single(task => task.TaskId == second).Readiness);
                using var foreign = await client.PostAsync($"/api/workspaces/{Guid.NewGuid()}/work-plans/{planId}/execution/advance", null);
                Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
            }
            // Restart the authority and Worker from the same persisted module stores.
            await using (var restarted = Factory(directory))
            {
                using var client = restarted.CreateClient();
                await using var worker = await AwpTestWorker.StartAsync(restarted);
                var plan = (await client.GetFromJsonAsync<WorkPlanResponse>(prefix))!;
                Assert.HasCount(1, plan.Attempts);
                var node = plan.Tasks.Single(task => task.TaskId == first);
                using var reviewed = await client.PostAsJsonAsync($"/api/workspaces/default/tasks/{first}/outcome",
                    new RecordWorkTaskOutcomeRequest(Guid.NewGuid(), node.Version, fail ? WorkTaskOutcome.Failed : WorkTaskOutcome.Accepted, "Reviewed deterministic evidence"));
                Assert.AreEqual(HttpStatusCode.OK, reviewed.StatusCode, await reviewed.Content.ReadAsStringAsync());
                if (!fail)
                {
                    plan = await WaitAsync(client, prefix, plan => plan.Attempts.Count == 2 && plan.Attempts[1].Status == WorkPlanAttemptStatus.Completed);
                    using var accepted = await client.PostAsJsonAsync($"/api/workspaces/default/tasks/{second}/outcome", new RecordWorkTaskOutcomeRequest(Guid.NewGuid(),
                        plan.Tasks.Single(task => task.TaskId == second).Version, WorkTaskOutcome.Accepted, "Summary reviewed"));
                    Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
                }
                using var replay = await client.PostAsync(prefix + "/execution/advance", null);
                Assert.AreEqual(HttpStatusCode.Accepted, replay.StatusCode);
                plan = (await replay.Content.ReadFromJsonAsync<WorkPlanResponse>())!;
                Assert.HasCount(fail ? 1 : 2, plan.Attempts);
                var current = await restarted.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
                using var scope = restarted.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
                var runs = await restarted.Services.GetRequiredService<FlowRunService>().ListAsync(null, null, 0, 100,
                    new(current.TenantId, new(current.WorkspaceId), current.PrincipalId), default);
                Assert.HasCount(fail ? 1 : 2, runs.Items.Where(run => plan.Attempts.Any(attempt => attempt.FlowRunId == run.Value.Id)).ToArray());
                var final = await client.GetFromJsonAsync<WorkTaskTreeResponse>($"/api/workspaces/default/tasks/{root}/tree");
                Assert.AreEqual(fail ? WorkTaskOutcome.Failed : WorkTaskOutcome.Accepted, final!.Tasks.Single(task => task.Id == root).Acceptance!.Outcome);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static WorkPlanExecutionTargetRequest Target(Guid id) => new(id, new(new FlowId("plan-fixture")), JsonSerializer.SerializeToElement(new { task = id }));
    private static TransitionWorkPlanRequest Transition(WorkPlanResponse plan) => new(Guid.NewGuid(), plan.Version, plan.CurrentRevision, plan.Revision.Digest);
    private static WebApplicationFactory<Program> Factory(string directory) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development"); builder.UseSetting("Data:Directory", directory);
        builder.UseSetting("Agentstration:Authentication:Mode", "Development");
        builder.UseSetting("AI:Provider", "Deterministic");
        builder.UseSetting("Agentstration:Extensions:DiscoverOnStartup", "false");
        AwpTestWorker.Configure(builder);
    });
    private static async Task<WorkPlanResponse> WaitAsync(HttpClient client, string prefix, Func<WorkPlanResponse, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        WorkPlanResponse? latest = null;
        try
        {
            while (true)
            {
                latest = await client.GetFromJsonAsync<WorkPlanResponse>(prefix, timeout.Token);
                if (latest is not null && predicate(latest)) return latest;
                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException) { Assert.Fail("Timed out waiting for WorkPlan: " + JsonSerializer.Serialize(latest)); throw; }
    }
}
