using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Agents;
using Agentstration.Models;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Artifacts.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Flows.Storage.Sqlite;
using Agentstration.Runtime.Storage.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Api.Tests;

[TestClass]
public sealed class ArtifactProcessingAgentExecutionTests
{
    [TestMethod]
    [DataRow("agent", ArtifactProcessingMode.Content, false, ResourceScopeKind.Workspace)]
    [DataRow("agent", ArtifactProcessingMode.Access, false, ResourceScopeKind.Workspace)]
    [DataRow("tool", ArtifactProcessingMode.Content, false, ResourceScopeKind.Workspace)]
    [DataRow("tool", ArtifactProcessingMode.Access, false, ResourceScopeKind.Workspace)]
    [DataRow("template-tool", ArtifactProcessingMode.Content, false, ResourceScopeKind.Workspace)]
    [DataRow("direct-template-tool", ArtifactProcessingMode.Content, false, ResourceScopeKind.Workspace)]
    [DataRow("agent", ArtifactProcessingMode.Content, true, ResourceScopeKind.Workspace)]
    [DataRow("agent", ArtifactProcessingMode.Access, true, ResourceScopeKind.Workspace)]
    [DataRow("direct-template-tool", ArtifactProcessingMode.Content, true, ResourceScopeKind.Workspace)]
    [DataRow("tool", ArtifactProcessingMode.Access, true, ResourceScopeKind.Workspace)]
    [DataRow("tool", ArtifactProcessingMode.Content, false, ResourceScopeKind.Instance)]
    [DataRow("tool", ArtifactProcessingMode.Content, true, ResourceScopeKind.Instance)]
    [DataRow("tool", ArtifactProcessingMode.Access, false, ResourceScopeKind.Instance)]
    [DataRow("tool", ArtifactProcessingMode.Access, true, ResourceScopeKind.Instance)]
    [DataRow("tool", ArtifactProcessingMode.Content, false, ResourceScopeKind.Tenant)]
    [DataRow("tool", ArtifactProcessingMode.Content, true, ResourceScopeKind.Tenant)]
    [DataRow("tool", ArtifactProcessingMode.Access, false, ResourceScopeKind.Tenant)]
    [DataRow("tool", ArtifactProcessingMode.Access, true, ResourceScopeKind.Tenant)]
    [DataRow("agent", ArtifactProcessingMode.Access, false, ResourceScopeKind.Instance)]
    [DataRow("agent", ArtifactProcessingMode.Access, true, ResourceScopeKind.Instance)]
    public async Task RealWorkerProcessesAnArtifactWithAnUnchangedAgentAndScopedContentTools(string processor, ArtifactProcessingMode mode, bool transient, ResourceScopeKind kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "artifact-agent-execution-" + Guid.NewGuid().ToString("N"));
        var model = new ProcessingModel();
        var genericTool = new GenericAnonymizerTool();
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Data:Directory", directory);
                builder.UseSetting("Agentstration:Testing:HostedServicesEnabled", "true");
                if (kind != ResourceScopeKind.Workspace)
                    builder.UseSetting("Agentstration:Authentication:DevelopmentGrantPlatformAdministrator", "true");
                AwpTestWorker.Configure(builder);
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IChatClientResolver>();
                    services.AddSingleton<IChatClientResolver>(model);
                    services.AddSingleton<IInternalMcpToolDefinitionProvider>(genericTool);
                    services.AddSingleton<IInternalMcpToolHandler>(genericTool);
                });
            });
            using var client = factory.CreateClient();
            var agentTemplate = processor == "agent" && kind != ResourceScopeKind.Workspace
                ? await client.GetFromJsonAsync<AgentResource>("/api/agents/sql-expert") : null;
            await using var worker = await AwpTestWorker.StartAsync(factory);
            var current = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            var topology = factory.Services.GetRequiredService<IOperationalTopologyProvisioner>();
            var operational = kind switch
            {
                ResourceScopeKind.Instance => await topology.GetInstanceOperationsWorkspaceAsync(default),
                ResourceScopeKind.Tenant => await topology.GetTenantOperationsWorkspaceAsync(current.TenantId, default),
                _ => null
            };
            if (operational is not null) current = new(current.PrincipalId, operational.TenantId, operational.Id);
            var ownerScope = kind switch
            {
                ResourceScopeKind.Instance => ResourceScopeRef.Instance,
                ResourceScopeKind.Tenant => ResourceScopeRef.Tenant(current.TenantId),
                _ => ResourceScopeRef.Workspace(current.WorkspaceId)
            };
            using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
            var processingAgent = "sql-expert";
            if (agentTemplate is not null)
            {
                if (kind == ResourceScopeKind.Tenant)
                {
                    using var sharedScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(current);
                    var resources = factory.Services.GetRequiredService<IResourceStore>();
                    var modelProfile = (await resources.GetExactAsync<ModelProfileResource>(ScopedResourceAddress.Create(ResourceScopeRef.Instance,
                        agentTemplate.Definition.ModelProfile.Namespace ?? ResourceNamespace.Default, ModelResourceKinds.ModelProfile, agentTemplate.Definition.ModelProfile.Name), default))!.Value;
                    var runtimeProfile = (await resources.GetExactAsync<RuntimeProfileResource>(ScopedResourceAddress.Create(ResourceScopeRef.Instance,
                        agentTemplate.Definition.RuntimeProfile.Namespace ?? ResourceNamespace.Default, RuntimeProfileResourceKinds.RuntimeProfile, agentTemplate.Definition.RuntimeProfile.Name), default))!.Value;
                    var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
                    foreach (var dependency in new Resource[] { modelProfile, runtimeProfile })
                    {
                        var existing = await policies.GetAsync(dependency.Uid, default);
                        var grants = (existing?.Policy.Grants ?? []).Where(grant => grant.TargetScopeRef != ownerScope)
                            .Append(new ResourceAccessGrant(ownerScope, [ResourceAccessAction.Discover, ResourceAccessAction.Use])).ToArray();
                        await policies.PutAsync(new(new(dependency.Uid, ResourceScopeRef.Instance, dependency.Namespace, dependency.Kind, dependency.Generation), grants), existing?.ETag, default);
                    }
                }
                using var agentDefinitionScope = kind == ResourceScopeKind.Instance
                    ? factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(current)
                    : factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushTenantAdministration(current);
                var agents = factory.Services.GetRequiredService<AgentManagementService>();
                var agent = await agents.PutAgentAsync(new AgentResource { ApiVersion = agentTemplate.ApiVersion, Kind = agentTemplate.Kind,
                    Metadata = new() { Name = "scoped-anonymizer" }, ScopeRef = ownerScope, Definition = agentTemplate.Definition with
                    { ModelProfile = agentTemplate.Definition.ModelProfile with { ScopeRef = ResourceScopeRef.Instance },
                        RuntimeProfile = agentTemplate.Definition.RuntimeProfile with { ScopeRef = ResourceScopeRef.Instance } } }, null, true, default);
                await agents.EnsureExecutionRevisionAsync(ResourceNamespace.Default, agent.Value.Name, ownerScope, agent.Value.Generation, default);
                processingAgent = agent.Value.Name;
            }
            var artifacts = factory.Services.GetRequiredService<ArtifactManagementService>();
            var content = !transient && mode == ArtifactProcessingMode.Access && processor == "agent" ? "Alice " + new string('x', 200_000) : "Alice";
            var inputId = "direct-input";
            JsonElement item;
            if (transient)
                item = JsonSerializer.SerializeToElement(new { artifactId = inputId, kind = "inline", name = "input.txt", mediaType = "text/plain",
                    contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) });
            else
            {
                var staged = await artifacts.CreateStagedAsync(new("input.txt", "text/plain",
                    new() { Kind = ArtifactProducerKind.FlowRun, Id = "synthetic-input" }), default);
                await artifacts.WriteAsync(staged.Value.ArtifactId, 0, Encoding.UTF8.GetBytes(content), default);
                await artifacts.SealAsync(staged.Value.ArtifactId, default);
                inputId = staged.Value.ArtifactId.ToString();
                item = JsonSerializer.SerializeToElement(new { artifactId = inputId, kind = "staged" });
            }
            var graph = new FlowGraphDefinition
            {
                EntryStep = "input",
                Steps = [new InputFlowStepDefinition { Name = "input" },
                    new ArtifactTransformFlowStepDefinition { Name = "process", Mode = mode },
                    new AgentFlowStepDefinition { Name = "anonymize", Agent = new(processingAgent, ScopeRef: ownerScope),
                        InputMapping = JsonSerializer.SerializeToElement("${transition.output}") },
                    new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success,
                        OutputMapping = JsonSerializer.SerializeToElement("${transition.output}") },
                    new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error,
                        Code = "PROCESSING_FAILED", DetailsExpression = "${transition.output}" }],
                Transitions = [new("start", "input", "completed", "process"), new("body", "process", "process", "anonymize"),
                    new("return", "anonymize", "success", "process"), new("agent-error", "anonymize", "error", "error"),
                    new("done", "process", "completed", "completed"), new("failed", "process", "error", "error")]
            };
            if (processor is "tool" or "template-tool" or "direct-template-tool")
            {
                var generic = new ToolFlowStepDefinition { Name = "anonymize", Tool = new(AgentstrationToolProvider.ToolResourceName("test.anonymizer"), ScopeRef: kind == ResourceScopeKind.Workspace ? ownerScope : ResourceScopeRef.Instance),
                    ArgumentsMapping = JsonSerializer.SerializeToElement(mode == ArtifactProcessingMode.Content
                        ? (object)"${input}" : new { text = "${steps.read.output.content}", language = "fr" }) };
                var processing = (ArtifactTransformFlowStepDefinition)graph.Steps.Single(step => step.Name == "process");
                var body = mode == ArtifactProcessingMode.Content
                    ? new FlowStepDefinition[] { generic }
                    : [new ToolFlowStepDefinition { Name = "read", Tool = new(AgentstrationToolProvider.ToolResourceName("artifact.processing.input_read"), ScopeRef: kind == ResourceScopeKind.Workspace ? ownerScope : ResourceScopeRef.Instance) },
                        generic,
                        new ToolFlowStepDefinition { Name = "write", Tool = new(AgentstrationToolProvider.ToolResourceName("artifact.processing.output_write"), ScopeRef: kind == ResourceScopeKind.Workspace ? ownerScope : ResourceScopeRef.Instance),
                            ArgumentsMapping = JsonSerializer.SerializeToElement(new { content = "${steps.anonymize.output.redacted}", expectedRevision = 0, requestId = "write" }) },
                        new ToolFlowStepDefinition { Name = "verify", Tool = new(AgentstrationToolProvider.ToolResourceName("artifact.processing.output_read"), ScopeRef: kind == ResourceScopeKind.Workspace ? ownerScope : ResourceScopeRef.Instance) },
                        new ToolFlowStepDefinition { Name = "complete", Tool = new(AgentstrationToolProvider.ToolResourceName("artifact.processing.output_complete"), ScopeRef: kind == ResourceScopeKind.Workspace ? ownerScope : ResourceScopeRef.Instance),
                            ArgumentsMapping = JsonSerializer.SerializeToElement(new { expectedRevision = 1 }) }];
                processing = processing with { ProcessingInputMapping = mode == ArtifactProcessingMode.Content
                    ? JsonSerializer.SerializeToElement(new { text = "${input.content}", language = "fr" }) : null,
                    ResultMapping = mode == ArtifactProcessingMode.Content ? JsonSerializer.SerializeToElement("${input.redacted}") : null };
                graph = graph with { Steps = [graph.Steps[0], processing, .. body, graph.Steps[^2], graph.Steps[^1]],
                    Transitions = [new("start", "input", "completed", "process"), new("body", "process", "process", body[0].Name),
                        .. body.Select((step, index) => new FlowTransitionDefinition("next-" + step.Name, step.Name, "success", index + 1 == body.Length ? "process" : body[index + 1].Name)),
                        .. body.Select(step => new FlowTransitionDefinition("error-" + step.Name, step.Name, "error", "error")),
                        new("done", "process", "completed", "completed"), new("failed", "process", "error", "error")] };
            }
            if (processor is "template-tool" or "direct-template-tool")
            {
                var schema = JsonSerializer.SerializeToElement(new { type = "object" });
                var template = FlowDraftTemplates.CreateTransformation(new(schema, schema));
                graph = graph with
                {
                    InputSchema = template.InputSchema, OutputSchema = template.OutputSchema,
                    Steps = [template.Steps[0], .. (processor == "template-tool" ? new[] { template.Steps[1] } : []), .. graph.Steps.Where(step => step.Name is not ("input" or "completed")), template.Steps[2]],
                    Transitions = [.. (processor == "template-tool"
                        ? new FlowTransitionDefinition[] { new("unwrap", "input", "completed", "transform"), new("process-input", "transform", "completed", "process") }
                        : [new("direct-process", "input", "completed", "process")]),
                        .. graph.Transitions.Where(edge => edge.FromStep != "input")]
                };
            }
            using var definitionScope = kind switch
            {
                ResourceScopeKind.Instance => factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(current),
                ResourceScopeKind.Tenant => factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushTenantAdministration(current),
                _ => factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current)
            };
            var flows = factory.Services.GetRequiredService<FlowService>();
            var flow = await flows.CreateExactAsync(ownerScope, new("artifact-agent", null, "1.0.0", true,
                processor == "agent" ? new DirectFlowDefinition(new(FlowTargetKind.Agent, processingAgent, ScopeRef: ownerScope))
                    : new WorkflowFlowDefinition("input", [new("input", FlowNodeKind.Input), new("completed", FlowNodeKind.Output)], [new("input", "completed")], ["completed"]), Graph: graph), ResourceNamespace.Default, default);
            await flows.PublishVersionExactAsync(ownerScope, flow.Value.Id, "1.0.0", true, default);
            var input = JsonSerializer.SerializeToElement(new { artifacts = new[] { item } });
            if (processor is "template-tool" or "direct-template-tool") input = JsonSerializer.SerializeToElement(new { value = input, parameters = new { } });
            var runs = factory.Services.GetRequiredService<FlowRunService>();
            var executionScope = new FlowRunScope(current.TenantId, new(current.WorkspaceId), current.PrincipalId) { LogicalScopeRef = ownerScope };
            string runId;
            if (transient || kind != ResourceScopeKind.Workspace)
            {
                var started = await runs.EnsureRootAsync(new("flowrun-direct-transform", flow.Value.Id, "1.0.0", "local",
                    FlowRunTrigger.Api, FlowInvocationOrigin.Api, current.PrincipalId.ToString(), null, null, "direct-transform-test",
                    input, "knowledge-projection:direct-transform-test", false, null, null, null, null, executionScope)
                    { TransientPayloads = transient, DefinitionScopeRef = ownerScope }, default);
                runId = started.Value.Id;
            }
            else
            {
                using var started = await client.PostAsJsonAsync("/api/flows/artifact-agent/runs", new CreateFlowRunRequest(input));
                Assert.IsTrue(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
                using var response = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
                var root = response.RootElement.TryGetProperty("run", out var wrapped) ? wrapped : response.RootElement;
                runId = root.GetProperty("id").GetString()!;
            }
            FlowRun? run = null;
            for (var attempt = 0; attempt < 400; attempt++)
            {
                run = (await runs.GetAsync(runId, executionScope, default))?.Value;
                if (run?.Status.IsTerminal() == true) break;
                await Task.Delay(25);
            }
            Assert.AreEqual(FlowRunStatus.Succeeded, run?.Status, JsonSerializer.Serialize(new { run?.Error, run?.Steps }));
            using var artifactObservation = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
            Assert.AreEqual(ownerScope, run!.Scope.AccessScopeRef);
            Assert.AreEqual(current.WorkspaceId, run.WorkspaceId.Value);
            var outputItem = run.Output!.Value.GetProperty("artifacts")[0];
            var outputId = outputItem.GetProperty("artifactId").GetString()!;
            Assert.AreNotEqual(inputId, outputId, "The generated transformation output must follow the inserted Artifact processor.");
            if (transient)
            {
                Assert.AreEqual("inline", outputItem.GetProperty("kind").GetString());
                Assert.AreEqual("PERSON_1", Encoding.UTF8.GetString(Convert.FromBase64String(outputItem.GetProperty("contentBase64").GetString()!)));
                Assert.IsEmpty(await artifacts.ListStagedAsync(default), "Direct processing must never create staged copies.");
                Assert.IsEmpty(await artifacts.ListFlowRunArtifactsAsync(default), "Only final Snapshot publication may retain direct content.");
                await using var flowDb = await factory.Services.GetRequiredService<IDbContextFactory<FlowDbContext>>().CreateDbContextAsync();
                var rawRuns = await flowDb.Database.SqlQueryRaw<string>("SELECT Payload AS Value FROM FlowResources WHERE Kind = 'run'").ToArrayAsync();
                Assert.IsTrue(rawRuns.Length >= 2, "The root and private processing child must both execute.");
                foreach (var raw in rawRuns)
                {
                    Assert.IsFalse(raw.Contains("Alice", StringComparison.Ordinal));
                    Assert.IsFalse(raw.Contains("PERSON_1", StringComparison.Ordinal));
                    Assert.IsFalse(raw.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("Alice")), StringComparison.Ordinal));
                }
                await using var runtimeDb = await factory.Services.GetRequiredService<IDbContextFactory<RuntimeRunDbContext>>().CreateDbContextAsync();
                var rawAssignments = await runtimeDb.Database.SqlQueryRaw<string>("SELECT Payload AS Value FROM RuntimeWorkerAssignments").ToArrayAsync();
                Assert.IsTrue(rawAssignments.Length >= 1, "Private processing children can share the parent assignment.");
                foreach (var raw in rawAssignments)
                {
                    using var assignment = JsonDocument.Parse(raw);
                    Assert.IsTrue(assignment.RootElement.GetProperty("transientPayloads").GetBoolean());
                    Assert.IsFalse(raw.Contains("Alice", StringComparison.Ordinal));
                    Assert.IsFalse(raw.Contains("PERSON_1", StringComparison.Ordinal));
                }
            }
            else
            {
                var output = (await artifacts.GetFlowRunArtifactAsync(FlowRunArtifactId.Parse(outputId), default))!.Value;
                var bytes = await factory.Services.GetRequiredService<IArtifactDurableStore>().ReadAsync(new(current.WorkspaceId), output.Receipt.OpaqueReference, 0, 1024, default);
                Assert.AreEqual("PERSON_1", Encoding.UTF8.GetString(bytes.Span));
                Assert.AreEqual(inputId, output.Receipt.Provenance["inputArtifactId"]);
                Assert.AreEqual(runId, output.ProducerFlowRunId);
            }
            if (mode == ArtifactProcessingMode.Access && processor == "agent")
            {
                CollectionAssert.AreEqual(new[] { "input_read", "output_write", "output_read", "output_search_regex", "output_complete" }, model.CalledTools.ToArray());
                Assert.IsLessThan(30_000, model.MaximumMessageCharacters, "The complete 200 KB input must not be put into model input.");
            }
            else Assert.IsEmpty(model.CalledTools);
            if (processor is "tool" or "template-tool" or "direct-template-tool")
            {
                Assert.AreEqual(1, genericTool.Calls);
                Assert.AreEqual("Alice", genericTool.LastInput?.GetProperty("text").GetString());
                Assert.AreEqual("fr", genericTool.LastInput?.GetProperty("language").GetString());
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task TenantAgentProcessesArtifactsWithExplicitSharedProfileAccess(bool transient) =>
        RealWorkerProcessesAnArtifactWithAnUnchangedAgentAndScopedContentTools("agent", ArtifactProcessingMode.Access, transient, ResourceScopeKind.Tenant);

    private sealed class GenericAnonymizerTool : IInternalMcpToolHandler
    {
        public int Calls { get; private set; }
        public JsonElement? LastInput { get; private set; }
        public InternalMcpToolDefinition Definition { get; } = new("test.anonymizer", "Synthetic anonymizer", "Generic text transformation fixture.",
            JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"text":{"type":"string"},"language":{"type":"string"}},"required":["text","language"],"additionalProperties":false}"""));
        public Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
        {
            Calls++;
            LastInput = invocation.Arguments.Clone();
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { redacted = invocation.Arguments.GetProperty("text").GetString()!.Replace("Alice", "PERSON_1", StringComparison.Ordinal) }));
        }
    }

    private sealed class ProcessingModel : IChatClientResolver, IChatClient
    {
        public List<string> CalledTools { get; } = [];
        public int MaximumMessageCharacters { get; private set; }
        public ValueTask<IChatClient> ResolveAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult<IChatClient>(this);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var history = messages.ToArray();
            MaximumMessageCharacters = Math.Max(MaximumMessageCharacters, history.Sum(message => message.Text.Length));
            var calls = history.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
            var tools = options?.Tools?.OfType<AIFunctionDeclaration>().Select(tool => tool.Name).ToArray() ?? [];
            var hasContentAccess = tools.Contains("input_read", StringComparer.Ordinal);
            if (!hasContentAccess || calls.Length >= 5)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, hasContentAccess ? "Completed and checked." : "PERSON_1")) { FinishReason = ChatFinishReason.Stop });
            var name = new[] { "input_read", "output_write", "output_read", "output_search_regex", "output_complete" }[calls.Length];
            CalledTools.Add(name);
            IDictionary<string, object?> arguments = name switch
            {
                "output_write" => new Dictionary<string, object?> { ["content"] = "PERSON_1", ["expectedRevision"] = 0, ["requestId"] = "anonymize" },
                "output_search_regex" => new Dictionary<string, object?> { ["pattern"] = "Alice|@" },
                "output_complete" => new Dictionary<string, object?> { ["expectedRevision"] = 1 },
                _ => new Dictionary<string, object?>()
            };
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-" + calls.Length, name, arguments)])) { FinishReason = ChatFinishReason.ToolCalls });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
