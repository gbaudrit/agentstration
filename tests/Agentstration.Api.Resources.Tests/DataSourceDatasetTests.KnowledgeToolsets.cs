using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.DataSources;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ModelProviders;
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task MixedContributionToolsetPreviewsWithoutPublicationDescribesAndEnforcesIndependentAccess(bool workspaceExecution)
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
        var originDefinition = Source("mixed-origin", fixture, JsonSerializer.SerializeToElement(new { dataset = "mixed" }));
        var origin = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
            originDefinition with { Definition = originDefinition.Definition with { Description = "Original supplier records" } }, default);
        async Task<KnowledgeDataSourceBinding> Binding(string name, params int[] values)
        {
            var transform = await PublishAsync(factory.Services, "mixed-" + name, KnowledgeFlowContracts.ArtifactTransformation,
                JsonSerializer.SerializeToElement(new
                {
                    artifacts = new[] { new
                    {
                        artifactId = name + "-records",
                        kind = "inline",
                        disposition = "publishable",
                        mediaType = "application/json",
                        recordContract = Agentstration.Schemas.DataContracts.Declared(JsonSerializer.SerializeToElement(new
                        {
                            type = "object",
                            properties = new { status = new { type = "string" }, value = new { type = "integer" } },
                            required = new[] { "status", "value" },
                            additionalProperties = false
                        }), "mixed-records", "application/json"),
                        contentBase64 = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(values.Select(value => new { status = name, value })))
                    } }
                }));
            return new()
            {
                Name = name,
                Description = name == "contracts" ? null : "Business records for " + name,
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
        _ = await sources.CreateAsync(source.Value with
        {
            Uid = Guid.Empty,
            Metadata = new() { Name = "unassigned-knowledge" },
            Definition = source.Value.Definition with { DisplayName = "Unassigned confidential source" }
        }, default);
        var id = new KnowledgeSourceId(source.Value.Name, ScopeRef: Owner);
        var exposures = factory.Services.GetRequiredService<KnowledgeSourceToolExposureService>();
        var access = factory.Services.GetRequiredService<KnowledgeAccessService>();
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var firstProposal = await exposures.ProposeAsync(id, default);
        Assert.IsTrue(firstProposal.Ready, string.Join("; ", firstProposal.Issues));
        Assert.HasCount(14, firstProposal.Tools); // source describe/status and four independently selected operations per input
        Assert.HasCount(3, firstProposal.Tools.Where(t => t.Operation == KnowledgeSourceOperation.StructuredQuery));
        Assert.IsNull(await exposures.GetAsync(id, default));
        Assert.IsEmpty(await acquisitions.ListAsync(origin.Value.Namespace, origin.Value.Name, Owner, default));
        Assert.IsEmpty(await factory.Services.GetRequiredService<KnowledgeSnapshotService>().ListAsync(id, default));
        Assert.AreEqual(firstProposal.Digest, (await exposures.ProposeAsync(id, default)).Digest);
        var contractsProposal = firstProposal.Tools.First(t => t.Contribution?.BindingName == "contracts");
        Assert.AreEqual("Original supplier records", contractsProposal.BusinessDescription);
        var originUpdated = await factory.Services.GetRequiredService<DataSourceManagementService>().PutAsync(origin.Value.Namespace, origin.Value.Name, Owner,
            origin.Value.Definition with { Description = "Updated supplier records" }, origin.ETag, default);
        Assert.AreEqual(origin.Value.Generation, originUpdated.Value.Generation);
        Assert.AreNotEqual(origin.ETag, originUpdated.ETag);
        var editorial = await sources.PutAsync(id, source.Value.Definition with { Description = "Updated business purpose" }, source.ETag, default);
        Assert.AreEqual(source.Value.Generation, editorial.Value.Generation);
        var editorialProposal = await exposures.ProposeAsync(id, default);
        Assert.AreEqual(firstProposal.Digest, editorialProposal.Digest);
        Assert.AreEqual(contractsProposal.ContractDigest, editorialProposal.Tools.First(t => t.Route == contractsProposal.Route).ContractDigest);
        var changed = await sources.PutAsync(id, editorial.Value.Definition with { Output = editorial.Value.Definition.Output! with { Rule = editorial.Value.Definition.Output.Rule with { MaximumReadBytes = 256 } } }, editorial.ETag, default);
        using var stale = await client.PostAsJsonAsync($"/api/knowledgesources/{id.Value}/tool-exposure?scopeRef=/instance",
            new PublishKnowledgeSourceToolExposureRequest { ProposalDigest = firstProposal.Digest });
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode, await stale.Content.ReadAsStringAsync());
        var proposal = await exposures.ProposeAsync(id, default);
        var exposure = await exposures.PublishAsync(id, "1.0.0", false, default, proposal.Digest);
        Assert.HasCount(14, exposure.Value.Operations);
        var describedTool = exposure.Value.Operations.Single(t => t.Operation == KnowledgeSourceOperation.Describe && t.Proposal!.Contribution is null).Tool;
        async Task<JsonElement> Run(ResourceReference tool, object request)
        {
            using var response = await client.PostAsJsonAsync($"/api/tools/{tool.Name}/run?scopeRef=/instance",
                new RunToolRequest(JsonSerializer.SerializeToElement(request), ToolRunMode.Execute));
            Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var result = (await response.Content.ReadFromJsonAsync<RunToolResponse>())!;
            Assert.AreEqual("completed", result.Status, JsonSerializer.Serialize(result));
            return result.Output!.Value;
        }
        var before = await Run(describedTool, new { });
        Assert.AreEqual("not_published", before.GetProperty("state").GetString());
        Assert.HasCount(14, before.GetProperty("toolSummaries").EnumerateArray());
        Assert.IsEmpty(before.GetProperty("tools").EnumerateArray());
        Assert.IsFalse(before.GetRawText().Contains("inputSchema", StringComparison.Ordinal));
        Assert.AreEqual("Updated supplier records", before.GetProperty("contributions")[0].GetProperty("description").GetString());
        Assert.IsEmpty(await acquisitions.ListAsync(origin.Value.Namespace, origin.Value.Name, Owner, default));
        var profile = (await access.DescribeDocumentsAsync(id, null, default)).QueryProfiles.Single();
        var query = new KnowledgeDocumentQueryRequest { Profile = profile, Query = StructuredStorageFixture.Json("{}"), PageSize = 10 };
        var published = await AwaitNativeAsync(await access.QueryDocumentsAsync(id, query, default),
            pending => access.QueryDocumentsAsync(id, query with { PreparationId = pending.PreparationId }, default));
        var a = published.SnapshotId!;
        var selected = exposure.Value.Operations.Single(t => t.Operation == KnowledgeSourceOperation.StructuredQuery && t.Proposal!.Contribution!.BindingName == "invoices");
        var inputDescribe = exposure.Value.Operations.Single(t => t.Operation == KnowledgeSourceOperation.Describe && t.Proposal!.Contribution?.BindingName == "invoices").Tool;
        var aggregate = await Run(selected.Tool, new { snapshotId = a, query = new Dictionary<string, string> { ["$apply"] = "aggregate($count as Count,value with sum as Total)" } });
        Assert.AreEqual(2, aggregate.GetProperty("documentPage").GetProperty("groups")[0].GetProperty("Count").GetInt32());
        Assert.AreEqual(30, aggregate.GetProperty("documentPage").GetProperty("groups")[0].GetProperty("Total").GetInt32());
        var records = await Run(selected.Tool, new { snapshotId = a, query = new { }, pageSize = 1 });
        var member = records.GetProperty("records").EnumerateArray().Single();
        Assert.AreEqual(0, member.GetProperty("memberOrdinal").GetInt32());
        Assert.AreEqual(member.GetProperty("artifactId").GetString(), records.GetProperty("documentArtifacts").EnumerateObject().Single().Value.GetString());
        var continuation = await Run(selected.Tool, new { cursor = records.GetProperty("nextCursor").GetString() });
        var nextMember = continuation.GetProperty("records").EnumerateArray().Single();
        Assert.AreEqual(member.GetProperty("artifactId").GetString(), nextMember.GetProperty("artifactId").GetString());
        Assert.AreEqual(1, nextMember.GetProperty("memberOrdinal").GetInt32());
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
        var previewBeforeEditorial = await exposures.ProposeAsync(id, default);
        originUpdated = await factory.Services.GetRequiredService<DataSourceManagementService>().PutAsync(origin.Value.Namespace, origin.Value.Name, Owner,
            originUpdated.Value.Definition with { Description = "Live supplier records" }, originUpdated.ETag, default);
        var overview = await Run(describedTool, new { snapshotId = a });
        Assert.AreEqual("Live supplier records", overview.GetProperty("contributions")[0].GetProperty("description").GetString());
        var details = await Run(describedTool, new { snapshotId = a, input = "contracts" });
        Assert.AreEqual("input", details.GetProperty("detailLevel").GetString());
        Assert.HasCount(1, details.GetProperty("contributions").EnumerateArray());
        Assert.HasCount(4, details.GetProperty("tools").EnumerateArray());
        Assert.IsTrue(details.GetProperty("tools")[0].TryGetProperty("inputSchema", out _));
        var metadataOnly = await sources.PutAsync(id, changed.Value.Definition with { DataSources = changed.Value.Definition.DataSources.Select(b => b.Name == "invoices" ? b with { Description = "Current invoice purpose" } : b).ToArray() }, changed.ETag, default);
        Assert.AreEqual(changed.Value.Generation, metadataOnly.Value.Generation);
        var afterEditorial = await exposures.ProposeAsync(id, default);
        Assert.AreEqual(previewBeforeEditorial.Digest, afterEditorial.Digest);
        Assert.IsTrue(afterEditorial.Tools.All(t => t.Change == "unchanged"));
        Assert.AreEqual("1.0.0", (await exposures.GetAsync(id, default))!.Value.ToolSetVersion);
        var invoiceDetails = await Run(inputDescribe, new { snapshotId = a });
        Assert.AreEqual("invoices", invoiceDetails.GetProperty("contributions")[0].GetProperty("selection").GetProperty("bindingName").GetString());
        Assert.HasCount(4, invoiceDetails.GetProperty("tools").EnumerateArray());
        var alias = overview.GetProperty("contributions").EnumerateArray().Single(c => c.GetProperty("selection").GetProperty("bindingName").GetString() == "invoices").GetProperty("input").GetString();
        var aliased = await Run(describedTool, new { snapshotId = a, input = alias });
        Assert.AreEqual("invoices", aliased.GetProperty("contributions")[0].GetProperty("selection").GetProperty("bindingName").GetString());

        var invoiceQuery = exposure.Value.Operations.Single(operation => operation.Operation == KnowledgeSourceOperation.StructuredQuery
            && operation.Proposal!.Contribution!.BindingName == "invoices").Tool;
        var firstPage = await Run(invoiceQuery, new { snapshotId = a, pageSize = 1, query = new { } });
        Assert.AreEqual(1, firstPage.GetProperty("documentPage").GetProperty("documents").GetArrayLength());
        var nextPage = await Run(invoiceQuery, new { cursor = firstPage.GetProperty("nextCursor").GetString() });
        Assert.AreEqual(1, nextPage.GetProperty("documentPage").GetProperty("documents").GetArrayLength());
        Assert.AreEqual(a, nextPage.GetProperty("snapshotId").GetString());

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
                ToolSets = [new() { ToolSet = exposure.Value.ToolSet, Version = "1.0.0", Members = [describedTool.Name, inputDescribe.Name, selected.Tool.Name] }]
            }
        }, null, true, default);
        await agents.EnsureExecutionRevisionAsync(agent.Value.Namespace, agent.Value.Name, Owner, agent.Value.Generation, default);
        var declared = new List<Agentstration.Runtime.Abstractions.RuntimeToolReference>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        foreach (var reference in new[] { describedTool, inputDescribe, selected.Tool })
        {
            var stored = (await store.GetExactAsync<Agentstration.Tools.ToolResource>(new(reference.ScopeRef!.Value, reference.Namespace ?? default, "Tool", reference.Name), default))!.Value;
            declared.Add(new(Agentstration.Tools.ToolResourceIdentity.CatalogId(stored.Namespace, stored.Name), stored.Namespace, stored.ScopeRef!.Value, stored.Uid, stored.Generation));
        }
        var runtimeTools = await factory.Services.GetRequiredService<Agentstration.Runtime.Abstractions.IToolCatalog>().ResolveAsync(declared);
        var enrichers = factory.Services.GetServices<Agentstration.Runtime.Abstractions.IRuntimeAgentPresentationEnricher>().ToArray();
        Assert.HasCount(1, enrichers);
        var present = await Agentstration.Runtime.Abstractions.RuntimeAgentPresentation.CreateAsync("Original instructions", runtimeTools, enrichers, default);
        StringAssert.Contains(present.Instructions, "Knowledge Sources assigned to this Agent", JsonSerializer.Serialize(runtimeTools.Select(t => new { t.Name, t.ProviderId, t.ExternalId, t.ScopeRef })));
        var accessOnly = await Agentstration.Runtime.Abstractions.RuntimeAgentPresentation.CreateAsync("Original instructions",
            runtimeTools.Where(t => t.Name.Contains("query-documents", StringComparison.Ordinal)).ToArray(), enrichers, default);
        Assert.AreEqual("Original instructions", accessOnly.Instructions);
        StringAssert.Contains(accessOnly.Tools.Single().Description!, "Current invoice purpose");
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
            new CreateFlowRunRequest(Empty, "1.0.0") { DefinitionScopeRef = Owner, ExecutionScopeRef = workspaceExecution ? ResourceScopeRef.Workspace(fixture.Context.WorkspaceId) : Owner });
        Assert.AreEqual(HttpStatusCode.Accepted, submitted.StatusCode, await submitted.Content.ReadAsStringAsync());
        var agentRun = (await submitted.Content.ReadFromJsonAsync<FlowRun>())!;
        for (var i = 0; i < 600 && agentRun.Status is FlowRunStatus.Pending or FlowRunStatus.Running; i++)
        { await Task.Delay(20); agentRun = (await client.GetFromJsonAsync<FlowRun>($"/api/flowRuns/{agentRun.Id}"))!; }
        Assert.AreEqual(FlowRunStatus.Succeeded, agentRun.Status, model.Diagnostic + "\n" + JsonSerializer.Serialize(agentRun.Error));
        Assert.AreEqual(3, model.ObservedAccessCount);
        Assert.AreEqual(30, model.ObservedTotal);
        Assert.AreEqual(1, model.ObservedDetailedInputs);
        _ = await sources.PutAsync(id, metadataOnly.Value.Definition with { DataSources = metadataOnly.Value.Definition.DataSources.Take(2).ToArray() }, metadataOnly.ETag, default);
        var removed = await exposures.ProposeAsync(id, default);
        Assert.HasCount(4, removed.Tools.Where(t => t.Change == "removed"));
        var exact = await access.QueryDocumentsAsync(id, query with { SnapshotId = a, Contribution = queryPin }, default);
        Assert.HasCount(2, exact.DocumentPage!.Documents);

    }

    private sealed class KnowledgeToolsetProbeModel : IChatClientResolver, IChatClient
    {
        public string? Snapshot { get; set; }
        public string? Diagnostic { get; private set; }
        public int ObservedAccessCount { get; private set; }
        public int ObservedTotal { get; private set; }
        public int ObservedDetailedInputs { get; private set; }
        public ValueTask<IChatClient> ResolveAsync(string name, CancellationToken ct = default) => ValueTask.FromResult<IChatClient>(this);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var history = messages.ToArray();
            var results = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToArray();
            var tools = options!.Tools!.OfType<AIFunctionDeclaration>().ToArray();
            Diagnostic = JsonSerializer.Serialize(new { options.Instructions, Tools = tools.Select(t => new { t.Name, t.Description }), Results = results.Length });
            Assert.HasCount(3, tools);
            foreach (var declaration in tools)
            {
                var properties = declaration.JsonSchema.GetProperty("properties");
                Assert.IsFalse(properties.TryGetProperty("request", out _));
                Assert.IsTrue(properties.TryGetProperty("snapshotId", out _));
                StringAssert.Contains(declaration.Description!, "do not add a request wrapper");
            }
            StringAssert.Contains(options.Instructions!, "do not add a request wrapper");
            StringAssert.Contains(options.Instructions!, "Knowledge Sources assigned to this Agent");
            StringAssert.Contains(options.Instructions!, "mixed-knowledge");
            Assert.IsFalse(options.Instructions!.Contains("unassigned-knowledge", StringComparison.Ordinal));
            StringAssert.Contains(options.Instructions!, "before the first data access");
            StringAssert.Contains(options.Instructions!, "Consult data from every exposed input");
            StringAssert.Contains(options.Instructions!, "Discovery alone is not a data consultation");
            var queryDeclaration = tools.Single(t => t.Name.Contains("query-documents", StringComparison.Ordinal));
            var querySchema = queryDeclaration.JsonSchema.GetProperty("properties").GetProperty("query");
            Assert.AreEqual("object", querySchema.GetProperty("type").GetString());
            Assert.IsTrue(querySchema.GetProperty("properties").TryGetProperty("$filter", out _));
            var queryDescription = queryDeclaration.Description!;
            StringAssert.Contains(queryDescription, "Current invoice purpose");
            StringAssert.Contains(queryDescription, "$filter");
            StringAssert.Contains(queryDescription, "eq/ne/gt/ge/lt/le");
            if (results.Length > 0)
            {
                var described = JsonSerializer.SerializeToElement(results[0].Result);
                StringAssert.Contains(described.GetProperty("invocationInstructions").GetString()!, "do not add a request wrapper");
                ObservedAccessCount = described.GetProperty("access").GetArrayLength();
                Assert.AreEqual(3, ObservedAccessCount);
                Assert.HasCount(1, described.GetProperty("contributions").EnumerateArray());
            }
            if (results.Length > 1)
            {
                var detail = JsonSerializer.SerializeToElement(results[1].Result);
                Assert.AreEqual("input", detail.GetProperty("detailLevel").GetString());
                ObservedDetailedInputs = detail.GetProperty("contributions").GetArrayLength();
                var recordSchema = detail.GetProperty("contributions")[0].GetProperty("recordSchemas").EnumerateArray().Single();
                Assert.AreEqual("object", recordSchema.GetProperty("type").GetString());
                Assert.IsTrue(recordSchema.GetProperty("properties").TryGetProperty("status", out _));
                Assert.HasCount(2, detail.GetProperty("tools").EnumerateArray());
            }
            if (results.Length > 2)
            {
                var queryResult = JsonSerializer.SerializeToElement(results[2].Result);
                ObservedTotal = queryResult.GetProperty("documentPage").GetProperty("groups")[0].GetProperty("Total").GetInt32();
                Assert.AreEqual(30, ObservedTotal);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "INVOICES_OK")) { FinishReason = ChatFinishReason.Stop });
            }
            var input = results.Length == 0 ? default : JsonSerializer.SerializeToElement(results[0].Result).GetProperty("contributions")[0];
            var tool = tools.Single(t => results.Length == 0 ? t.Name.Contains(".access.describe.", StringComparison.Ordinal)
                : results.Length == 1 ? t.Name == input.GetProperty("describeTool").GetString()
                : t.Name.Contains("query-documents", StringComparison.Ordinal));
            if (results.Length == 1)
            {
                Assert.IsFalse(tool.JsonSchema.GetProperty("properties").TryGetProperty("input", out _));
                Assert.IsEmpty(input.GetProperty("describeArguments").EnumerateObject());
            }
            object request = results.Length < 2 ? new { snapshotId = Snapshot } : new { snapshotId = Snapshot, query = new Dictionary<string, string> { ["$apply"] = "aggregate($count as Count,value with sum as Total)" } };
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("knowledge-call-" + results.Length, tool.Name, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(request)))]))
            { FinishReason = ChatFinishReason.ToolCalls });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();
        public object? GetService(Type type, object? key = null) => type.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
