using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Tools;
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
    [DataRow("agent", ArtifactProcessingMode.Content)]
    [DataRow("agent", ArtifactProcessingMode.Access)]
    [DataRow("tool", ArtifactProcessingMode.Content)]
    [DataRow("tool", ArtifactProcessingMode.Access)]
    [DataRow("template-tool", ArtifactProcessingMode.Content)]
    public async Task RealWorkerProcessesAnArtifactWithAnUnchangedAgentAndScopedContentTools(string processor, ArtifactProcessingMode mode)
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
            await using var worker = await AwpTestWorker.StartAsync(factory);
            var current = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
            var artifacts = factory.Services.GetRequiredService<ArtifactManagementService>();
            var content = mode == ArtifactProcessingMode.Access && processor == "agent" ? "Alice " + new string('x', 200_000) : "Alice";
            var staged = await artifacts.CreateStagedAsync(new("input.txt", "text/plain",
                new() { Kind = ArtifactProducerKind.FlowRun, Id = "synthetic-input" }), default);
            await artifacts.WriteAsync(staged.Value.ArtifactId, 0, Encoding.UTF8.GetBytes(content), default);
            await artifacts.SealAsync(staged.Value.ArtifactId, default);
            var graph = new FlowGraphDefinition
            {
                EntryStep = "input",
                Steps = [new InputFlowStepDefinition { Name = "input" },
                    new ArtifactTransformFlowStepDefinition { Name = "process", Mode = mode },
                    new AgentFlowStepDefinition { Name = "anonymize", Agent = new("sql-expert"),
                        InputMapping = JsonSerializer.SerializeToElement("${transition.output}") },
                    new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success,
                        OutputMapping = JsonSerializer.SerializeToElement("${transition.output}") },
                    new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error,
                        Code = "PROCESSING_FAILED", DetailsExpression = "${transition.output}" }],
                Transitions = [new("start", "input", "completed", "process"), new("body", "process", "process", "anonymize"),
                    new("return", "anonymize", "success", "process"), new("agent-error", "anonymize", "error", "error"),
                    new("done", "process", "completed", "completed"), new("failed", "process", "error", "error")]
            };
            if (processor is "tool" or "template-tool")
            {
                var generic = new ToolFlowStepDefinition { Name = "anonymize", Tool = new(AgentstrationToolProvider.ToolResourceName("test.anonymizer")),
                    ArgumentsMapping = JsonSerializer.SerializeToElement(mode == ArtifactProcessingMode.Content
                        ? (object)"${input}" : new { text = "${steps.read.output.content}", language = "fr" }) };
                var processing = (ArtifactTransformFlowStepDefinition)graph.Steps.Single(step => step.Name == "process");
                var body = mode == ArtifactProcessingMode.Content
                    ? new FlowStepDefinition[] { generic }
                    : [new ToolFlowStepDefinition { Name = "read", Tool = new(AgentstrationToolProvider.ToolResourceName("artifact.processing.input_read")) },
                        generic,
                        new ToolFlowStepDefinition { Name = "write", Tool = new(AgentstrationToolProvider.ToolResourceName("artifact.processing.output_write")),
                            ArgumentsMapping = JsonSerializer.SerializeToElement(new { content = "${steps.anonymize.output.redacted}", expectedRevision = 0, requestId = "write" }) },
                        new ToolFlowStepDefinition { Name = "verify", Tool = new(AgentstrationToolProvider.ToolResourceName("artifact.processing.output_read")) },
                        new ToolFlowStepDefinition { Name = "complete", Tool = new(AgentstrationToolProvider.ToolResourceName("artifact.processing.output_complete")),
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
            if (processor == "template-tool")
            {
                var schema = JsonSerializer.SerializeToElement(new { type = "object" });
                var template = FlowDraftTemplates.CreateTransformation(new(schema, schema));
                graph = graph with
                {
                    InputSchema = template.InputSchema, OutputSchema = template.OutputSchema,
                    Steps = [template.Steps[0], template.Steps[1], .. graph.Steps.Where(step => step.Name is not ("input" or "completed")), template.Steps[2]],
                    Transitions = [new("unwrap", "input", "completed", "transform"), new("process-input", "transform", "completed", "process"),
                        .. graph.Transitions.Where(edge => edge.FromStep != "input")]
                };
            }
            var flows = factory.Services.GetRequiredService<FlowService>();
            var flow = await flows.CreateAsync(new WorkspaceId(current.WorkspaceId), new("artifact-agent", null, "1.0.0", true,
                new DirectFlowDefinition(new(FlowTargetKind.Agent, "sql-expert")), Graph: graph), default);
            await flows.PublishVersionAsync(new WorkspaceId(current.WorkspaceId), flow.Value.Id, "1.0.0", true, default);
            var input = JsonSerializer.SerializeToElement(new { artifacts = new[] { new { artifactId = staged.Value.ArtifactId.ToString(), kind = "staged" } } });
            if (processor == "template-tool") input = JsonSerializer.SerializeToElement(new { value = input, parameters = new { } });
            using var started = await client.PostAsJsonAsync("/api/flows/artifact-agent/runs", new CreateFlowRunRequest(input));
            Assert.IsTrue(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
            using var response = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
            var root = response.RootElement.TryGetProperty("run", out var wrapped) ? wrapped : response.RootElement;
            var runId = root.GetProperty("id").GetString()!;
            var runs = factory.Services.GetRequiredService<FlowRunService>();
            var executionScope = new FlowRunScope(current.TenantId, new(current.WorkspaceId), current.PrincipalId);
            FlowRun? run = null;
            for (var attempt = 0; attempt < 400; attempt++)
            {
                run = (await runs.GetAsync(runId, executionScope, default))?.Value;
                if (run?.Status.IsTerminal() == true) break;
                await Task.Delay(25);
            }
            Assert.AreEqual(FlowRunStatus.Succeeded, run?.Status, JsonSerializer.Serialize(new { run?.Error, run?.Steps }));
            var outputId = run!.Output!.Value.GetProperty("artifacts")[0].GetProperty("artifactId").GetString()!;
            Assert.AreNotEqual(staged.Value.ArtifactId.ToString(), outputId, "The generated transformation output must follow the inserted Artifact processor.");
            var output = (await artifacts.GetFlowRunArtifactAsync(FlowRunArtifactId.Parse(outputId), default))!.Value;
            var bytes = await factory.Services.GetRequiredService<IArtifactDurableStore>().ReadAsync(new(current.WorkspaceId), output.Receipt.OpaqueReference, 0, 1024, default);
            Assert.AreEqual("PERSON_1", Encoding.UTF8.GetString(bytes.Span));
            Assert.AreEqual(staged.Value.ArtifactId.ToString(), output.Receipt.Provenance["inputArtifactId"]);
            Assert.AreEqual(runId, output.ProducerFlowRunId);
            if (mode == ArtifactProcessingMode.Access && processor == "agent")
            {
                CollectionAssert.AreEqual(new[] { "input_read", "output_write", "output_read", "output_search_regex", "output_complete" }, model.CalledTools.ToArray());
                Assert.IsLessThan(30_000, model.MaximumMessageCharacters, "The complete 200 KB input must not be put into model input.");
            }
            else Assert.IsEmpty(model.CalledTools);
            if (processor is "tool" or "template-tool")
            {
                Assert.AreEqual(1, genericTool.Calls);
                Assert.AreEqual("Alice", genericTool.LastInput?.GetProperty("text").GetString());
                Assert.AreEqual("fr", genericTool.LastInput?.GetProperty("language").GetString());
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

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
