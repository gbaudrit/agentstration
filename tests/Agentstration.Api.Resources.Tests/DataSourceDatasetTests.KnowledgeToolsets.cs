using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.DataSources;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.ModelProviders;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    public async Task MixedContributionToolsetPreviewsWithoutPublicationDescribesAndEnforcesIndependentAccess()
    {
        var model = new KnowledgeToolsetProbeModel();
        await using var factory = Factory().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" }));
            b.ConfigureServices(services => { services.RemoveAll<IChatClientResolver>(); services.AddSingleton<IChatClientResolver>(model); });
        });
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        await using var storage = await StructuredStorageFixture.StartAsync(factory.Services, client, Owner, false);
        var origin = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
            Source("mixed-origin", fixture, JsonSerializer.SerializeToElement(new { dataset = "mixed" })), default);
        async Task<KnowledgeDataSourceBinding> Binding(string name, params int[] values)
        {
            var transform = await PublishAsync(factory.Services, "mixed-" + name, KnowledgeFlowContracts.ArtifactTransformation,
                JsonSerializer.SerializeToElement(new
                {
                    artifacts = values.Select((value, i) => new
                    {
                        artifactId = name + "-" + i,
                        kind = "inline",
                        disposition = "publishable",
                        mediaType = "application/json",
                        contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { status = name, value })))
                    }).ToArray()
                }));
            return new()
            {
                Name = name,
                Description = "Business records for " + name,
                RefreshIfNecessary = true,
                MaximumAge = TimeSpan.FromHours(1),
                DataSource = new(origin.Value.Name, Owner),
                TransformationFlow = new() { Name = transform.Flow.Name, ScopeRef = Owner, Version = transform.Version, UseActiveVersion = false }
            };
        }
        var sources = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
        var source = await sources.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            ScopeRef = Owner,
            Metadata = new() { Name = "mixed-knowledge" },
            Definition = new()
            {
                DisplayName = "Supplier knowledge",
                Description = "Contracts, procedures and invoices",
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                Output = new(storage.Reference, new(Write: true)) { DocumentCollectionUid = storage.Collection },
                DataSources = [await Binding("contracts", 1), await Binding("procedures", 100), await Binding("invoices", 10, 20)]
            }
        }, default);
        var id = new KnowledgeSourceId(source.Value.Name, ScopeRef: Owner);
        var exposures = factory.Services.GetRequiredService<KnowledgeSourceToolExposureService>();
        var access = factory.Services.GetRequiredService<KnowledgeAccessService>();
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var firstProposal = await exposures.ProposeAsync(id, default);
        Assert.IsTrue(firstProposal.Ready, string.Join("; ", firstProposal.Issues));
        Assert.HasCount(11, firstProposal.Tools); // describe, status, 3 independently selected operations per binding
        Assert.HasCount(3, firstProposal.Tools.Where(t => t.Operation == KnowledgeSourceOperation.StructuredQuery));
        Assert.IsNull(await exposures.GetAsync(id, default));
        Assert.IsEmpty(await acquisitions.ListAsync(origin.Value.Namespace, origin.Value.Name, Owner, default));
        Assert.IsEmpty(await factory.Services.GetRequiredService<KnowledgeSnapshotService>().ListAsync(id, default));
        Assert.AreEqual(firstProposal.Digest, (await exposures.ProposeAsync(id, default)).Digest);
        var changed = await sources.PutAsync(id, source.Value.Definition with { Description = "Updated business purpose" }, source.ETag, default);
        using var stale = await client.PostAsJsonAsync($"/api/knowledgesources/{id.Value}/tool-exposure?scopeRef=/instance",
            new PublishKnowledgeSourceToolExposureRequest { ProposalDigest = firstProposal.Digest });
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode, await stale.Content.ReadAsStringAsync());
        var proposal = await exposures.ProposeAsync(id, default);
        var exposure = await exposures.PublishAsync(id, "1.0.0", false, default, proposal.Digest);
        Assert.HasCount(11, exposure.Value.Operations);
        var describedTool = exposure.Value.Operations.Single(t => t.Operation == KnowledgeSourceOperation.Describe).Tool;
        async Task<JsonElement> Run(ResourceReference tool, object request)
        {
            using var response = await client.PostAsJsonAsync($"/api/tools/{tool.Name}/run?scopeRef=/instance",
                new RunToolRequest(JsonSerializer.SerializeToElement(new { request }), ToolRunMode.Execute));
            Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var result = (await response.Content.ReadFromJsonAsync<RunToolResponse>())!;
            Assert.AreEqual("completed", result.Status, JsonSerializer.Serialize(result));
            return result.Output!.Value;
        }
        var before = await Run(describedTool, new { });
        Assert.AreEqual("not_published", before.GetProperty("state").GetString());
        Assert.HasCount(11, before.GetProperty("tools").EnumerateArray());
        Assert.IsEmpty(await acquisitions.ListAsync(origin.Value.Namespace, origin.Value.Name, Owner, default));
        var profile = (await access.DescribeDocumentsAsync(id, null, default)).QueryProfiles.Single();
        var query = new KnowledgeDocumentQueryRequest { Profile = profile, Query = StructuredStorageFixture.Json("{}"), PageSize = 10 };
        var published = await AwaitNativeAsync(await access.QueryDocumentsAsync(id, query, default),
            pending => access.QueryDocumentsAsync(id, query with { PreparationId = pending.PreparationId }, default));
        var a = published.SnapshotId!;
        var selected = exposure.Value.Operations.Single(t => t.Operation == KnowledgeSourceOperation.StructuredQuery && t.Proposal!.Contribution!.BindingName == "invoices");
        var aggregate = await Run(selected.Tool, new { snapshotId = a, query = new Dictionary<string, string> { ["$apply"] = "aggregate($count as Count,value with sum as Total)" } });
        Assert.AreEqual(2, aggregate.GetProperty("documentPage").GetProperty("groups")[0].GetProperty("Count").GetInt32());
        Assert.AreEqual(30, aggregate.GetProperty("documentPage").GetProperty("groups")[0].GetProperty("Total").GetInt32());
        var contracts = exposure.Value.Operations.Single(t => t.Operation == KnowledgeSourceOperation.SearchRegex && t.Proposal!.Contribution!.BindingName == "contracts");
        var matches = await Run(contracts.Tool, new { snapshotId = a, pattern = "contracts" });
        Assert.AreEqual(1, matches.GetProperty("matches").GetArrayLength());
        var described = await Run(describedTool, new { snapshotId = a });
        Assert.AreEqual(a, described.GetProperty("snapshotId").GetString());
        Assert.AreEqual(3, described.GetProperty("contributions").GetArrayLength());
        Assert.IsTrue((await exposures.ProposeAsync(id, default)).Tools.All(t => t.Change == "unchanged"));
        var queryPin = selected.Proposal!.Contribution!;
        var page = await access.QueryDocumentsAsync(id, query with { SnapshotId = a, Contribution = queryPin, PageSize = 1 }, default);
        Assert.IsNotNull(page.NextCursor);
        var violation = await Assert.ThrowsAsync<KnowledgeAccessException>(() => access.QueryDocumentsAsync(id,
            new() { Cursor = page.NextCursor, Contribution = contracts.Proposal!.Contribution }, default));
        Assert.AreEqual("knowledge_access_cursor_contribution_mismatch", violation.Code);
        _ = await sources.PutAsync(id, changed.Value.Definition with { DataSources = changed.Value.Definition.DataSources.Take(2).ToArray() }, changed.ETag, default);
        var removed = await exposures.ProposeAsync(id, default);
        Assert.HasCount(3, removed.Tools.Where(t => t.Change == "removed"));
        var exact = await access.QueryDocumentsAsync(id, query with { SnapshotId = a, Contribution = queryPin }, default);
        Assert.HasCount(2, exact.DocumentPage!.Documents);
        // Real AWP Agent execution with a partial ToolSet assignment: discovery cannot advertise siblings.
        var agents = factory.Services.GetRequiredService<AgentManagementService>();
        var agent = await agents.PutAgentAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = AgentResourceKinds.Agent,
            Metadata = new() { Name = "mixed-knowledge-reader" },
            ScopeRef = Owner,
            Definition = new AgentProperties
            {
                DisplayName = "Mixed Knowledge reader",
                Instructions = "Describe the assigned Knowledge and compute the invoice total.",
                Tools = [],
                ModelProfile = new("reasoning-default", Owner),
                RuntimeProfile = new("maf-builtin", Owner),
                ToolSets = [new() { ToolSet = exposure.Value.ToolSet, Version = "1.0.0", Members = [describedTool.Name, selected.Tool.Name] }]
            }
        }, null, true, default);
        await agents.EnsureExecutionRevisionAsync(agent.Value.Namespace, agent.Value.Name, Owner, agent.Value.Generation, default);
        model.Snapshot = a;
        var flows = factory.Services.GetRequiredService<FlowService>();
        var flow = await flows.CreateExactAsync(Owner, new CreateFlowCommand("mixed-knowledge-agent", null, "1.0.0", true,
            new WorkflowFlowDefinition("input", [new FlowNode("input", FlowNodeKind.Input)], []), Graph: new()
            {
                EntryStep = "input",
                InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                Steps = [new InputFlowStepDefinition { Name = "input" },
                    new AgentFlowStepDefinition { Name = "reader", Agent = new(agent.Value.Name, ScopeRef: Owner), InputMapping = JsonSerializer.SerializeToElement("Inspect the invoices") },
                    new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.reader.output}") }],
                Transitions = [new("start", "input", "completed", "reader"), new("done", "reader", "success", "output")]
            }), default, default);
        await flows.PublishVersionExactAsync(Owner, flow.Value.Id, "1.0.0", true, default);
        using var submitted = await client.PostAsJsonAsync("/api/flows/mixed-knowledge-agent/runs?scopeRef=/instance",
            new CreateFlowRunRequest(Empty, "1.0.0") { DefinitionScopeRef = Owner, ExecutionScopeRef = Owner });
        Assert.AreEqual(HttpStatusCode.Accepted, submitted.StatusCode, await submitted.Content.ReadAsStringAsync());
        var agentRun = (await submitted.Content.ReadFromJsonAsync<FlowRun>())!;
        for (var i = 0; i < 600 && agentRun.Status is FlowRunStatus.Pending or FlowRunStatus.Running; i++)
        { await Task.Delay(20); agentRun = (await client.GetFromJsonAsync<FlowRun>($"/api/flowRuns/{agentRun.Id}"))!; }
        Assert.AreEqual(FlowRunStatus.Succeeded, agentRun.Status, JsonSerializer.Serialize(agentRun));
        Assert.AreEqual(2, model.ObservedAccessCount);
        Assert.AreEqual(30, model.ObservedTotal);
    }

    private sealed class KnowledgeToolsetProbeModel : IChatClientResolver, IChatClient
    {
        public string? Snapshot { get; set; }
        public int ObservedAccessCount { get; private set; }
        public int ObservedTotal { get; private set; }
        public ValueTask<IChatClient> ResolveAsync(string name, CancellationToken ct = default) => ValueTask.FromResult<IChatClient>(this);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var history = messages.ToArray();
            var results = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToArray();
            var tools = options!.Tools!.OfType<AIFunctionDeclaration>().ToArray();
            Assert.HasCount(2, tools);
            if (results.Length > 0)
            {
                var described = JsonSerializer.SerializeToElement(results[0].Result);
                ObservedAccessCount = described.GetProperty("access").GetArrayLength();
                Assert.AreEqual(2, ObservedAccessCount);
                Assert.HasCount(1, described.GetProperty("contributions").EnumerateArray());
            }
            if (results.Length > 1)
            {
                var queryResult = JsonSerializer.SerializeToElement(results[1].Result);
                ObservedTotal = queryResult.GetProperty("documentPage").GetProperty("groups")[0].GetProperty("Total").GetInt32();
                Assert.AreEqual(30, ObservedTotal);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "INVOICES_OK")) { FinishReason = ChatFinishReason.Stop });
            }
            var tool = tools.Single(t => results.Length == 0 ? t.Name.Contains(".describe.", StringComparison.Ordinal) : t.Name.Contains("query-documents", StringComparison.Ordinal));
            object request = results.Length == 0 ? new { snapshotId = Snapshot } : new { snapshotId = Snapshot, query = new Dictionary<string, string> { ["$apply"] = "aggregate($count as Count,value with sum as Total)" } };
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("knowledge-call-" + results.Length, tool.Name, new Dictionary<string, object?> { ["request"] = request })]))
            { FinishReason = ChatFinishReason.ToolCalls });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();
        public object? GetService(Type type, object? key = null) => type.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
