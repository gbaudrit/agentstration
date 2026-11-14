using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Resources;
using Agentstration.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Api.Tests;

[TestClass]
public sealed class ImplicitProcessExecutionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealWorkerSchedulesParallelProcessChainsWithNamedCallsAndPreservesFailureOrigin(bool fail)
    {
        var directory = Path.Combine(Path.GetTempPath(), "implicit-process-" + Guid.NewGuid().ToString("N"));
        var tool = new ProcessTool(fail);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Data:Directory", directory);
                builder.UseSetting("Agentstration:Testing:HostedServicesEnabled", "true");
                AwpTestWorker.Configure(builder);
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IInternalMcpToolDefinitionProvider>(tool);
                    services.AddSingleton<IInternalMcpToolHandler>(tool);
                });
            });
            using var client = factory.CreateClient();
            var current = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
            var flows = factory.Services.GetRequiredService<FlowService>();
            var ownerScope = ResourceScopeRef.Workspace(current.WorkspaceId);
            var leaf = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" },
                new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success }],
                Transitions = [new("done", "input", "completed", "completed")] };
            await PublishAsync("named-leaf", leaf);
            var graph = new FlowGraphDefinition
            {
                EntryStep = "input",
                Steps = [new InputFlowStepDefinition { Name = "input" },
                    new TransformFlowStepDefinition { Name = "partition", DisplayName = "Process fragments", Processing = new()
                        { Partition = FlowProcessPartition.TextChunks, UnitSize = 2, MaximumConcurrency = 2, MaximumUnits = 3 } },
                    new ToolFlowStepDefinition { Name = "body", Tool = new(AgentstrationToolProvider.ToolResourceName("test.process.unit"), ScopeRef: ResourceScopeRef.Instance),
                        ArgumentsMapping = JsonSerializer.SerializeToElement(new { text = "${input}" }) },
                    new TransformFlowStepDefinition { Name = "correct", Mapping = JsonSerializer.SerializeToElement(new { final = "${steps.body.output.value}" }) },
                    new FlowCallStepDefinition { Name = "named", Flow = new("named-leaf", FlowCallVersionStrategy.Exact, "1.0.0", ScopeRef: ownerScope),
                        InputMapping = JsonSerializer.SerializeToElement("${transition.output}") },
                    new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success },
                    new OutputFlowStepDefinition { Name = "failed", Outcome = FlowOutputOutcome.Error }],
                Transitions = [new("start", "input", "completed", "partition"), new("process", "partition", "process", "body"),
                    new("correct", "body", "success", "correct"), new("named", "correct", "completed", "named"),
                    new("return", "named", "completed", "partition"), new("done", "partition", "completed", "completed"),
                    new("error", "partition", "error", "failed")]
            };
            await PublishAsync("implicit-process", graph);
            using var started = await client.PostAsJsonAsync("/api/flows/implicit-process/runs", new CreateFlowRunRequest(JsonSerializer.SerializeToElement("aabbcc")));
            Assert.IsTrue(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
            var value = json.RootElement.TryGetProperty("run", out var wrapped) ? wrapped : json.RootElement;
            var id = value.GetProperty("id").GetString()!;
            var assignments = factory.Services.GetRequiredService<RuntimeWorkerAssignmentService>();
            var queued = await assignments.GetByTargetExactAsync(ownerScope, RuntimeAssignmentTargetKind.FlowRun, id, default);
            for (var attempt = 0; queued is null && attempt < 200; attempt++)
            {
                await Task.Delay(25);
                queued = await assignments.GetByTargetExactAsync(ownerScope, RuntimeAssignmentTargetKind.FlowRun, id, default);
            }
            Assert.IsNotNull(queued, "The accepted Flow Run must publish its worker assignment before the material version is inspected.");
            Assert.AreEqual(RuntimeExecutionMaterialVersions.CoordinatedProcesses, queued.Value.ExecutionMaterialVersion);
            var legacy = await assignments.ClaimNextAsync(new(Guid.NewGuid()), new(Guid.NewGuid()), "microsoft-agent-framework",
                new HashSet<string> { "1.0" }, new HashSet<string> { "1.0" }, 1, default);
            Assert.IsNull(legacy, "A legacy Worker must not claim material that introduces processing semantics it cannot interpret.");
            await using var worker = await AwpTestWorker.StartAsync(factory);
            var runs = factory.Services.GetRequiredService<FlowRunService>();
            var runScope = new FlowRunScope(current.TenantId, new(current.WorkspaceId), current.PrincipalId);
            FlowRun? run = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!timeout.IsCancellationRequested)
            {
                run = (await runs.GetAsync(id, runScope, timeout.Token))?.Value;
                if (run?.Status.IsTerminal() == true) break;
                await Task.Delay(25, timeout.Token);
            }
            Assert.AreEqual(fail ? FlowRunStatus.Failed : FlowRunStatus.Succeeded, run?.Status, JsonSerializer.Serialize(run?.Error));
            Assert.AreEqual(2, tool.MaximumActive, "Two units must overlap, and the owner limit must be respected.");
            Assert.AreEqual(3, tool.Calls);
            var causal = await runs.GetCausalityAsync(id, 0, 100, runScope, default);
            var units = causal.Items.Where(node => node.ProcessOrigin is not null).OrderBy(node => node.ProcessOrigin!.Ordinal).ToArray();
            Assert.HasCount(3, units);
            Assert.HasCount(1, units.Select(node => node.ProcessOrigin!.Definition).Distinct().ToArray());
            Assert.IsTrue(units.All(node => node.ProcessOrigin!.OwnerStepExecutionId is not null && node.ParentStepName == "partition"));
            Assert.IsTrue(run!.Steps.Single(step => step.StepName == "partition").Process!.Closed);
            var named = causal.Items.Where(node => node.FlowId.Value == "named-leaf").ToArray();
            Assert.HasCount(fail ? 2 : 3, named);
            Assert.IsTrue(named.All(node => node.ProcessOrigin is null && units.Any(unit => unit.FlowRunId == node.ParentFlowRunId)));
            if (fail)
            {
                Assert.AreEqual("data_contract_violation", run.Error!.Code);
                Assert.AreEqual(units[1].FlowRunId, run.Error.Origin!.RunId);
                Assert.AreEqual("body", run.Error.Origin.StepName);
                Assert.AreEqual("unit-1", run.Error.Origin.UnitKey);
            }
            else
                CollectionAssert.AreEqual(new[] { "aa", "bb", "cc" }, run.Output!.Value.EnumerateArray().Select(unit => unit.GetProperty("final").GetString()).ToArray());
            Assert.IsNull(await runs.GetAsync(units[0].FlowRunId, runScope with { WorkspaceId = WorkspaceId.New() }, default));

            async Task PublishAsync(string name, FlowGraphDefinition definition)
            {
                var model = new WorkflowFlowDefinition("input", [new("input", FlowNodeKind.Input), new("completed", FlowNodeKind.Output)], [new("input", "completed")], ["completed"]);
                var flow = await flows.CreateExactAsync(ownerScope, new(name, null, "1.0.0", true, model, Graph: definition), ResourceNamespace.Default, default);
                await flows.PublishVersionExactAsync(ownerScope, flow.Value.Id, "1.0.0", true, default);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class ProcessTool(bool fail) : IInternalMcpToolHandler
    {
        private readonly TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource together = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls, active, maximum;
        public int Calls => Volatile.Read(ref calls);
        public int MaximumActive => Volatile.Read(ref maximum);
        public InternalMcpToolDefinition Definition { get; } = new("test.process.unit", "Process fixture", "Synthetic bounded process fixture.",
            JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}"""),
            JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}"""));
        public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
        {
            var callOrdinal = Interlocked.Increment(ref calls);
            var count = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref maximum); if (previous >= count) break; }
            while (Interlocked.CompareExchange(ref maximum, count, previous) != previous);
            var text = invocation.Arguments.GetProperty("text").GetString();
            try
            {
                if (callOrdinal <= 2)
                {
                    if (count == 2) together.TrySetResult();
                    await together.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
                if (text == "aa") await second.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                if (text == "bb") second.TrySetResult();
                return fail && text == "bb" ? JsonSerializer.SerializeToElement(new { value = 42 }) : JsonSerializer.SerializeToElement(new { value = text });
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
