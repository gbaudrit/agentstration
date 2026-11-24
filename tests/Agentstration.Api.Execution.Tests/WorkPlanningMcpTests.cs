using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Application.Work;
using Agentstration.Infrastructure.Work;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Tools;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Agentstration.Api.Tests;

[TestClass]
public sealed class WorkPlanningMcpTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task GovernedMcpCreatesRevisesAndExecutesLocalWorkAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "work-planning-mcp-" + Guid.NewGuid().ToString("N"));
        var taskId = Guid.NewGuid(); var planId = Guid.NewGuid();
        var prefix = $"/api/workspaces/default/work-plans/{planId}";
        JsonElement startArguments = default;
        try
        {
            await using (var factory = Factory(directory))
            {
                using var http = factory.CreateClient();
                var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
                using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
                var flows = factory.Services.GetRequiredService<FlowService>();
                var graph = new FlowGraphDefinition
                {
                    EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }, new OutputFlowStepDefinition { Name = "done" }],
                    Transitions = [new("done", "input", "completed", "done")]
                };
                var workflow = new WorkflowFlowDefinition("input", [new("input", FlowNodeKind.Input), new("done", FlowNodeKind.Output)], [new("input", "done")], ["done"]);
                var resourceScope = ResourceScopeRef.Workspace(context.WorkspaceId);
                var flow = await flows.CreateExactAsync(resourceScope, new("mcp-work", null, "1.0.0", true, workflow, Graph: graph), ResourceNamespace.Default, default);
                await flows.PublishVersionExactAsync(resourceScope, flow.Value.Id, "1.0.0", true, default);
                await using var worker = await AwpTestWorker.StartAsync(factory);
                await using var mcp = await ConnectAsync(http);
                var published = await mcp.ListToolsAsync();
                Assert.HasCount(12, published.Where(tool => tool.Name.StartsWith("work.plan.", StringComparison.Ordinal)
                    || tool.Name.StartsWith("work.project.", StringComparison.Ordinal) || tool.Name.StartsWith("work.task.", StringComparison.Ordinal)).ToArray());
                var treeArguments = JsonSerializer.SerializeToElement(new { tree = new CreateWorkTaskTreeRequest(taskId, [new(taskId, WorkTaskKind.Atomic, "Collect", "Collect local evidence")]) }, Json);
                _ = Value(await CallAsync(mcp, "work.task.create-tree", "tree", treeArguments));
                _ = Value(await CallAsync(mcp, "work.task.create-tree", "tree", treeArguments));
                var proposed = new { planId, definition = new WorkPlanDefinitionRequest("Collect evidence", [taskId], []) };
                var preview = await CallAsync(mcp, "work.plan.propose", "preview", JsonSerializer.SerializeToElement(new { proposed.planId, proposed.definition, dryRun = true }, Json));
                Assert.IsFalse(preview.IsError == true);
                using var absent = await http.GetAsync(prefix);
                Assert.AreEqual(HttpStatusCode.NotFound, absent.StatusCode);
                var plan = Plan(await CallAsync(mcp, "work.plan.propose", "propose", JsonSerializer.SerializeToElement(proposed, Json)));
                var replay = Plan(await CallAsync(mcp, "work.plan.propose", "propose", JsonSerializer.SerializeToElement(proposed, Json)));
                Assert.AreEqual(plan.Version, replay.Version);
                plan = Plan(await CallAsync(mcp, "work.plan.revise", "revise", JsonSerializer.SerializeToElement(new
                { planId, expectedVersion = plan.Version, definition = new WorkPlanDefinitionRequest("Collect reviewed evidence", [taskId], []) }, Json)));
                Assert.AreEqual(2, plan.CurrentRevision);
                var stale = await CallAsync(mcp, "work.plan.revise", "stale", JsonSerializer.SerializeToElement(new
                { planId, expectedVersion = 1, definition = proposed.definition }, Json));
                Error(stale, "work_plan_conflict");
                var cycle = await CallAsync(mcp, "work.plan.revise", "cycle", JsonSerializer.SerializeToElement(new
                { planId, expectedVersion = plan.Version, definition = new WorkPlanDefinitionRequest("Cycle", [taskId], [new(taskId, taskId)]) }, Json));
                Error(cycle, "work_plan_dependency_cycle");
                foreach (var operation in new[] { "validate", "activate" })
                    plan = Plan(await CallAsync(mcp, "work.plan." + operation, operation, Transition(plan)));
                var rest = await http.GetFromJsonAsync<WorkPlanResponse>(prefix);
                Assert.AreEqual(plan.Revision.Digest, rest!.Revision.Digest);
                Assert.AreEqual(plan.Status, rest.Status);
                startArguments = JsonSerializer.SerializeToElement(new
                {
                    planId, expectedVersion = plan.Version, revision = plan.CurrentRevision, revisionDigest = plan.Revision.Digest,
                    targets = new[] { new WorkPlanExecutionTargetRequest(taskId, new(new FlowId("mcp-work")), JsonSerializer.SerializeToElement(new { evidence = "deterministic" })) }
                }, Json);
                _ = Plan(await CallAsync(mcp, "work.plan.start", "start", startArguments));
                plan = await WaitAsync(http, prefix);
                Assert.HasCount(1, plan.Attempts);
                Assert.AreEqual(WorkTaskOutcome.Pending, plan.Tasks.Single().Outcome);
            }
            await using (var restarted = Factory(directory))
            {
                using var http = restarted.CreateClient();
                await using var mcp = await ConnectAsync(http);
                var plan = Plan(await CallAsync(mcp, "work.plan.start", "start", startArguments));
                Assert.HasCount(1, plan.Attempts);
                Assert.AreEqual(WorkPlanAttemptStatus.Completed, plan.Attempts.Single().Status);
                _ = Value(await CallAsync(mcp, "work.task.record-outcome", "review", JsonSerializer.SerializeToElement(new
                { taskId, expectedVersion = plan.Tasks.Single().Version, outcome = WorkTaskOutcome.Accepted, evidence = "Reviewed deterministic output" }, Json)));
                plan = Plan(await CallAsync(mcp, "work.plan.advance", "advance", JsonSerializer.SerializeToElement(new { planId })));
                Assert.AreEqual(WorkPlanTaskReadiness.Accepted, plan.Tasks.Single().Readiness);
                Assert.HasCount(1, plan.Attempts);
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task McpRejectsForgedScopeUnassignedDisabledAndApprovalRequiredCalls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "work-planning-governance-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = Factory(directory);
            using var http = factory.CreateClient();
            var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
            await using var mcp = await ConnectAsync(http);
            _ = await mcp.ListToolsAsync();
            var forged = await CallAsync(mcp, "work.project.list", "forged", JsonSerializer.SerializeToElement(new { workspaceId = Guid.NewGuid() }));
            Assert.IsTrue(forged.IsError == true);
            var oversized = await CallAsync(mcp, "work.project.list", "oversized-page", JsonSerializer.SerializeToElement(new { take = 101 }));
            Error(oversized, "work_project_page_invalid");
            var nestedForgery = await CallAsync(mcp, "work.plan.propose", "nested-forgery", JsonSerializer.SerializeToElement(new
            { planId = Guid.NewGuid(), definition = new { intent = "forged", taskIds = new[] { Guid.NewGuid() }, dependencies = Array.Empty<object>(), principalId = Guid.NewGuid() } }));
            Assert.IsTrue(nestedForgery.IsError == true);
            var handler = factory.Services.GetServices<IInternalMcpToolHandler>().Single(value => value.Definition.Name == "work.project.list");
            var invocation = new InternalMcpToolInvocation(context.TenantId, new(context.WorkspaceId), context.PrincipalId, "lookup", "test", JsonSerializer.SerializeToElement(new { }), ToolDefinitionCallerKind.Agent);
            var unassigned = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => handler.ExecuteAsync(invocation, default));
            Assert.AreEqual("tool_not_authorized", unassigned.Code);
            var mismatched = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => handler.ExecuteAsync(invocation with { WorkspaceId = new(Guid.NewGuid()) }, default));
            Assert.AreEqual("work_planning_scope_required", mismatched.Code);
            var assigned = invocation with { ExposedTools = [new(AgentstrationToolProvider.ToolResourceName(handler.Definition.Name), ResourceScopeRef.Workspace(context.WorkspaceId), ResourceNamespace.Default)] };
            Assert.IsNotNull(await handler.ExecuteAsync(assigned, default));
            var store = factory.Services.GetRequiredService<IResourceStore>();
            var address = ScopedResourceAddress.Create(ResourceScopeRef.Workspace(context.WorkspaceId), ResourceNamespace.Default, ToolResourceKinds.Tool, AgentstrationToolProvider.ToolResourceName(handler.Definition.Name));
            var tool = (await store.GetExactAsync<ToolResource>(address, default))!;
            using (factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Core("tools.internal-mcp")))
                tool = await store.PutExactAsync(address.ScopeRef, tool.Value with { Definition = tool.Value.Definition with { Enabled = false } }, tool.ETag, false, default);
            Error(await CallAsync(mcp, handler.Definition.Name, "disabled", JsonSerializer.SerializeToElement(new { })), "tool_disabled");
            using (factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Core("tools.internal-mcp")))
                _ = await store.PutExactAsync(address.ScopeRef, tool.Value with { Definition = tool.Value.Definition with { Enabled = true, RequiresApproval = true } }, tool.ETag, false, default);
            Error(await CallAsync(mcp, handler.Definition.Name, "approval", JsonSerializer.SerializeToElement(new { })), "tool_approval_required");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task ToolRechecksCurrentRunPermissionBeforeReadingPrivateWork()
    {
        var directory = Path.Combine(Path.GetTempPath(), "work-planning-revoked-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = Factory(directory);
            using var http = factory.CreateClient();
            var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
            var tool = new WorkPlanningMcpTool(WorkPlanningOperation.Projects,
                factory.Services.GetRequiredService<WorkProjectService>(), factory.Services.GetRequiredService<WorkTaskTreeService>(),
                factory.Services.GetRequiredService<WorkPlanService>(), factory.Services.GetRequiredService<WorkPlanExecutionService>(),
                factory.Services.GetRequiredService<ICurrentRequestContext>(), new DeniedAuthorization());
            var error = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => tool.ExecuteAsync(new(context.TenantId,
                new(context.WorkspaceId), context.PrincipalId, "revoked", null, JsonSerializer.SerializeToElement(new { }), ToolDefinitionCallerKind.Mcp), default));
            Assert.AreEqual("authorization_denied", error.Code);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class DeniedAuthorization : IAuthorizationService
    {
        public Task<IReadOnlySet<string>> GetPermissionsAsync(RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
        public Task<bool> HasPermissionAsync(RequestContext context, string permission, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task EnsurePermissionAsync(RequestContext context, string permission, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Permission denied by fixture."));
    }

    private static WebApplicationFactory<Program> Factory(string directory) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development"); builder.UseSetting("Data:Directory", directory);
        builder.UseSetting("Agentstration:Authentication:Mode", "Development");
        builder.UseSetting("AI:Provider", "Deterministic");
        builder.UseSetting("Agentstration:Extensions:DiscoverOnStartup", "false");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        AwpTestWorker.Configure(builder);
    });
    private static Task<McpClient> ConnectAsync(HttpClient http) => McpClient.CreateAsync(new HttpClientTransport(new()
    { Endpoint = new Uri(http.BaseAddress!, "mcp"), Name = "work-planning-test" }, http, NullLoggerFactory.Instance, ownsHttpClient: false), loggerFactory: NullLoggerFactory.Instance);
    private static ValueTask<CallToolResult> CallAsync(McpClient mcp, string name, string callId, JsonElement arguments) => mcp.CallToolAsync(new CallToolRequestParams
    {
        Name = name, Arguments = arguments.Deserialize<Dictionary<string, JsonElement>>()!,
        Meta = new JsonObject { ["agentstration/idempotencyKey"] = callId, ["agentstration/correlationId"] = "work-planning-test" }
    });
    private static JsonElement Value(CallToolResult result)
    {
        Assert.IsFalse(result.IsError == true, JsonSerializer.Serialize(result));
        return result.StructuredContent!.Value.GetProperty("value");
    }
    private static WorkPlanResponse Plan(CallToolResult result) => Value(result).Deserialize<WorkPlanResponse>(Json)!;
    private static void Error(CallToolResult result, string code)
    { Assert.IsTrue(result.IsError == true); Assert.AreEqual(code, result.Meta?["agentstration/errorCode"]?.GetValue<string>()); }
    private static JsonElement Transition(WorkPlanResponse plan) => JsonSerializer.SerializeToElement(new
    { planId = plan.Id, expectedVersion = plan.Version, revision = plan.CurrentRevision, revisionDigest = plan.Revision.Digest }, Json);
    private static async Task<WorkPlanResponse> WaitAsync(HttpClient http, string prefix)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            var plan = (await http.GetFromJsonAsync<WorkPlanResponse>(prefix, timeout.Token))!;
            if (plan.Attempts.Count == 1 && plan.Attempts[0].Status == WorkPlanAttemptStatus.Completed) return plan;
            await Task.Delay(100, timeout.Token);
        }
    }
}
