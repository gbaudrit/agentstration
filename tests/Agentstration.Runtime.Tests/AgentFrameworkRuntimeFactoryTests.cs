using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.AgentFramework;
using Agentstration.Runtime.Local;
using Agentstration.Runtime.Storage.Sqlite;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class AgentFrameworkRuntimeFactoryTests
{
    private static readonly WorkspaceId TestWorkspaceId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    [TestMethod]
    public void FlowOrchestrationMapsToolApprovalRequestsAndResponsesAsConfirmations()
    {
        var approval = new ToolApprovalRequestContent(
            "approval-1",
            new FunctionCallContent("call-1", "delete_record", new Dictionary<string, object?>()));
        var envelope = new TestApprovalEnvelope(approval);
        var port = RequestPort.Create<TestApprovalEnvelope, TestApprovalResponse>("approval-port");
        var request = ExternalRequest.Create(port, envelope, "request-1");

        var description = AgentFrameworkFlowOrchestrationEngine.DescribeInteraction(request, "ignored");
        Assert.AreEqual(InputRequestType.Confirmation, description.Type);
        StringAssert.Contains(description.Prompt, "Approve");

        var input = new InputRequest
        {
            WorkspaceId = TestWorkspaceId,
            Id = "input-1",
            RunId = "run-1",
            RuntimeRequestId = request.RequestId,
            Prompt = description.Prompt,
            Type = description.Type,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Status = InputRequestStatus.Answered,
            Response = new(DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(true), "principal-1")
        };
        var response = AgentFrameworkFlowOrchestrationEngine.CreateResponse(request, input);

        Assert.IsTrue(response.TryGetDataAs<TestApprovalResponse>(out var wrapped));
        var approvalResponse = wrapped!.Messages.SelectMany(message => message.Contents)
            .OfType<ToolApprovalResponseContent>().Single();
        Assert.IsTrue(approvalResponse.Approved);
    }

    [TestMethod]
    public async Task MafApprovalRequestResumesFromSqliteAfterCompleteReconstruction()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-maf-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "runtime.db");
        try
        {
            static ServiceProvider StateProvider(string path)
            {
                var services = new ServiceCollection();
                services.AddSingleton(TimeProvider.System);
                services.AddSqliteRuntimeRuns($"Data Source={path};Pooling=False");
                return services.BuildServiceProvider();
            }

            var definition = new OrchestrationFlowDefinition(
                [new FlowTargetReference(FlowTargetKind.Agent, "agent-1")],
                new SequentialOrchestrationPattern());
            var initialEvents = new List<FlowExecutionEvent>();
            var agentResolver = new RecordingAgentResolver();
            FlowExternalInputRequested suspended;
            IReadOnlyList<RuntimeExecutionBinding> bindings;

            await using (var firstProvider = StateProvider(databasePath))
            {
                await firstProvider.GetRequiredService<IRuntimeRunStore>().InitializeAsync(default);
                using var requestingClient = new RecordingChatClient
                {
                    ResponseFactory = (_, _, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [
                        new ToolApprovalRequestContent(
                            "approval-1",
                            new FunctionCallContent("call-1", "delete_record", new Dictionary<string, object?>()))
                    ]))
                };
                var factory = new AgentFrameworkRuntimeFactory(
                    new RecordingResolver(requestingClient), NullLoggerFactory.Instance, new GenAiObservabilityOptions());
                var firstEngine = new AgentFrameworkFlowOrchestrationEngine(
                    agentResolver, new EmptyToolCatalog(), factory,
                    firstProvider.GetRequiredService<IRuntimeExecutionStateStore>());

                await foreach (var item in firstEngine.ExecuteAsync(new FlowOrchestrationExecutionRequest(
                    TestWorkspaceId, "run-maf-resume", definition, JsonSerializer.SerializeToElement(new { prompt = "Delete it" }), "correlation-1")))
                    initialEvents.Add(item);
                suspended = initialEvents.OfType<FlowExternalInputRequested>().Single();
                bindings = initialEvents.OfType<FlowRuntimeBindingsResolved>().Single().Bindings;
                Assert.AreEqual(InputRequestType.Confirmation, suspended.Type);
                Assert.IsNotNull(await firstProvider.GetRequiredService<IRuntimeExecutionStateStore>().GetAsync(
                    TestWorkspaceId, "run-maf-resume", suspended.RuntimeState.RuntimeType, suspended.RuntimeState.StateId, default));
            }

            await using (var secondProvider = StateProvider(databasePath))
            {
                await secondProvider.GetRequiredService<IRuntimeRunStore>().InitializeAsync(default);
                using var resumedClient = new RecordingChatClient
                {
                    ResponseFactory = (_, _, _) =>
                        new ChatResponse(new ChatMessage(ChatRole.Assistant, "APPROVED_OK"))
                };
                var factory = new AgentFrameworkRuntimeFactory(
                    new RecordingResolver(resumedClient), NullLoggerFactory.Instance, new GenAiObservabilityOptions());
                var resumedEngine = new AgentFrameworkFlowOrchestrationEngine(
                    agentResolver, new EmptyToolCatalog(), factory,
                    secondProvider.GetRequiredService<IRuntimeExecutionStateStore>());
                var answer = new InputRequest
                {
                    WorkspaceId = TestWorkspaceId,
                    Id = "input-1",
                    RunId = "run-maf-resume",
                    RuntimeRequestId = suspended.RuntimeRequestId,
                    Prompt = suspended.Prompt,
                    Type = InputRequestType.Confirmation,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                    Status = InputRequestStatus.Answered,
                    Response = new(DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(true), "principal-1")
                };
                var resumedEvents = new List<FlowExecutionEvent>();
                await foreach (var item in resumedEngine.ExecuteAsync(new FlowOrchestrationExecutionRequest(
                    TestWorkspaceId, "run-maf-resume", definition, JsonSerializer.SerializeToElement(new { prompt = "Delete it" }),
                    "correlation-1", bindings, suspended.RuntimeState, answer)))
                    resumedEvents.Add(item);

                Assert.AreEqual("APPROVED_OK", resumedEvents.OfType<FlowExecutionCompleted>().Single().Result.FinalOutput.GetString());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApprovalRequiredAiFunctionProducesARealMafExternalRequest()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-maf-approval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSqliteRuntimeRuns($"Data Source={Path.Combine(directory, "runtime.db")};Pooling=False");
            await using var provider = services.BuildServiceProvider();
            var states = provider.GetRequiredService<IRuntimeExecutionStateStore>();
            await provider.GetRequiredService<IRuntimeRunStore>().InitializeAsync(default);
            using var chatClient = new RecordingChatClient
            {
                ResponseFactory = (call, _, options) =>
                {
                    if (call > 1) return new ChatResponse(new ChatMessage(ChatRole.Assistant, "APPROVED_OK"));
                    var tool = options?.Tools?.Single(value => value.Name == ApprovalTool.ToolName)
                        ?? throw new InvalidOperationException("The governed tool was not exposed to the Agent.");
                    Assert.IsInstanceOfType<ApprovalRequiredAIFunction>(tool);
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "hash-call-1",
                            tool.Name,
                            new Dictionary<string, object?> { ["text"] = "durable approval" })
                    ]));
                }
            };
            var factory = new AgentFrameworkRuntimeFactory(
                new RecordingResolver(chatClient),
                NullLoggerFactory.Instance,
                new GenAiObservabilityOptions());
            var pipeline = new RecordingToolExecutionPipeline();
            var engine = new AgentFrameworkFlowOrchestrationEngine(
                new RecordingAgentResolver([ApprovalTool.ResourceId]),
                new ApprovalToolCatalog(),
                factory,
                states,
                configuredToolExecution: pipeline);
            var events = new List<FlowExecutionEvent>();

            await foreach (var item in engine.ExecuteAsync(new FlowOrchestrationExecutionRequest(
                TestWorkspaceId,
                "run-real-approval",
                new OrchestrationFlowDefinition(
                    [new FlowTargetReference(FlowTargetKind.Agent, "approval-agent")],
                    new SequentialOrchestrationPattern()),
                JsonSerializer.SerializeToElement(new { payload = "durable approval" }),
                "correlation-real-approval")))
            {
                events.Add(item);
            }

            var requested = events.OfType<FlowExternalInputRequested>().Single();
            Assert.AreEqual(InputRequestType.Confirmation, requested.Type);
            Assert.IsNotNull(await states.GetAsync(
                TestWorkspaceId,
                "run-real-approval",
                requested.RuntimeState.RuntimeType,
                requested.RuntimeState.StateId,
                default));

            var bindings = events.OfType<FlowRuntimeBindingsResolved>().Single().Bindings;
            var answer = new InputRequest
            {
                WorkspaceId = TestWorkspaceId,
                Id = "input-real-approval",
                RunId = "run-real-approval",
                RuntimeRequestId = requested.RuntimeRequestId,
                Prompt = requested.Prompt,
                Type = InputRequestType.Confirmation,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                Status = InputRequestStatus.Answered,
                Response = new(DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(true), "principal-1")
            };
            var resumed = new List<FlowExecutionEvent>();
            var scope = new FlowRunScope(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                TestWorkspaceId,
                Guid.Parse("33333333-3333-3333-3333-333333333333"));
            await foreach (var item in engine.ExecuteAsync(new FlowOrchestrationExecutionRequest(
                TestWorkspaceId,
                "run-real-approval",
                new OrchestrationFlowDefinition(
                    [new FlowTargetReference(FlowTargetKind.Agent, "approval-agent")],
                    new SequentialOrchestrationPattern()),
                JsonSerializer.SerializeToElement(new { payload = "durable approval" }),
                "correlation-real-approval",
                bindings,
                requested.RuntimeState,
                answer,
                scope)))
                resumed.Add(item);

            var invocation = pipeline.Contexts.Single();
            StringAssert.StartsWith(invocation.ToolCallId, "tool-");
            Assert.AreEqual(TestWorkspaceId, invocation.WorkspaceId);
            Assert.AreEqual(scope.TenantId, invocation.TenantId);
            Assert.AreEqual(scope.PrincipalId, invocation.PrincipalId);
            Assert.AreEqual("run-real-approval", invocation.RunId);
            Assert.AreEqual("correlation-real-approval", invocation.CorrelationId);
            Assert.AreEqual("APPROVED_OK", resumed.OfType<FlowExecutionCompleted>().Single().Result.FinalOutput.GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task MafToolAdapterHasNoProviderEscapeHatchAndAlwaysUsesPipeline()
    {
        var tool = new TestTool();
        var pipeline = new RecordingToolExecutionPipeline();
        var workspaceId = new WorkspaceId(Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var template = new ToolExecutionContext
        {
            ToolCallId = "pending",
            InvocationId = "pending",
            ToolId = "pending",
            ToolName = "pending",
            TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            WorkspaceId = workspaceId,
            PrincipalId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            RunId = "run-1",
            AgentId = "agent-1",
            AgentVersion = 7,
            AgentRevisionId = "revision-7",
            CorrelationId = "correlation-1"
        };
        var function = Assert.IsInstanceOfType<AIFunction>(
            AgentFrameworkRuntimeFactory.MapTool(tool, pipeline, template));
        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "hello" });

        _ = await function.InvokeAsync(arguments);
        _ = await function.InvokeAsync(arguments);

        Assert.IsNull(typeof(IAgentTool).GetMethod("GetService"));
        Assert.IsNull(typeof(IAgentTool).GetMethod("InvokeAsync"));
        Assert.AreEqual(2, pipeline.Contexts.Count);
        var first = pipeline.Contexts[0];
        var second = pipeline.Contexts[1];
        Assert.AreEqual(tool.Id, first.ToolId);
        Assert.AreEqual(workspaceId, first.WorkspaceId);
        Assert.AreEqual("run-1", first.RunId);
        Assert.AreEqual("agent-1", first.AgentId);
        Assert.AreEqual(7, first.AgentVersion);
        Assert.AreEqual("revision-7", first.AgentRevisionId);
        Assert.AreEqual(first.ToolCallId, second.ToolCallId);
        Assert.AreNotEqual(first.InvocationId, second.InvocationId);
    }

    [TestMethod]
    public async Task DirectAgentExecutionInvokesToolThroughPipelineWithRuntimeScope()
    {
        using var chatClient = new RecordingChatClient
        {
            ResponseFactory = (call, _, options) => call == 1
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("direct-call-1", options!.Tools!.Single().Name,
                        new Dictionary<string, object?> { ["value"] = "hello" })
                ]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "DIRECT_OK"))
        };
        var tool = new TestTool();
        var pipeline = new RecordingToolExecutionPipeline();
        var definition = Definition() with { EffectiveToolNames = [tool.Id] };
        var runtime = await new AgentFrameworkRuntimeFactory(
                new RecordingResolver(chatClient),
                NullLoggerFactory.Instance,
                new GenAiObservabilityOptions())
            .CreateAsync(definition, "revision-direct", new AgentRuntimeContext(new SingleToolCatalog(tool), pipeline), default);
        var workspaceId = new WorkspaceId(Guid.Parse("55555555-5555-5555-5555-555555555555"));

        var result = await runtime.ExecuteAsync(new AgentExecutionRequest(
            "Use the tool",
            "run-direct",
            ToolExecution: new ToolExecutionScope
            {
                TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                WorkspaceId = workspaceId,
                PrincipalId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                ExecutionId = "run-direct",
                CorrelationId = "correlation-direct"
            }), default);

        Assert.AreEqual("DIRECT_OK", result.Output);
        var invocation = pipeline.Contexts.Single();
        Assert.AreEqual(workspaceId, invocation.WorkspaceId);
        Assert.AreEqual("run-direct", invocation.RunId);
        Assert.AreEqual("revision-direct", invocation.AgentRevisionId);
        Assert.AreEqual("correlation-direct", invocation.CorrelationId);
    }

    [TestMethod]
    public async Task MafToolAdapterPropagatesCancellationAndProviderDiagnostics()
    {
        var tool = new TestTool();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellingPipeline = new RecordingToolExecutionPipeline((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<JsonElement?>(null);
        });
        var function = Assert.IsInstanceOfType<AIFunction>(
            AgentFrameworkRuntimeFactory.MapTool(tool, cancellingPipeline, Template()));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await function.InvokeAsync(new AIFunctionArguments(), cancelled.Token));

        var expected = new InvalidOperationException("provider diagnostic: tools/call failed");
        var failingPipeline = new RecordingToolExecutionPipeline(
            (_, _) => ValueTask.FromException<JsonElement?>(expected));
        function = Assert.IsInstanceOfType<AIFunction>(
            AgentFrameworkRuntimeFactory.MapTool(tool, failingPipeline, Template()));
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await function.InvokeAsync(new AIFunctionArguments()));
        Assert.AreSame(expected, actual);

        static ToolExecutionContext Template() => new()
        {
            ToolCallId = "pending",
            InvocationId = "pending",
            ToolId = "pending",
            ToolName = "pending",
            RunId = "run-diagnostics"
        };
    }

    [TestMethod]
    public void FlowOrchestrationMapsMafExecutorIdentityToInternalParticipantId()
    {
        using var chatClient = new RecordingChatClient();
        AIAgent agent = new ChatClientAgent(chatClient, name: "agent-1");
        IReadOnlyDictionary<string, AIAgent> participants = new Dictionary<string, AIAgent>
        {
            ["agent-1"] = agent
        };

        Assert.AreEqual(
            "agent-1",
            AgentFrameworkFlowOrchestrationEngine.ResolveParticipantId(agent.Id, "generated-executor", participants));
        var exception = Assert.ThrowsExactly<FlowValidationException>(() =>
            AgentFrameworkFlowOrchestrationEngine.ResolveParticipantId("unknown-agent", "unknown-maf-executor", participants));
        Assert.AreEqual("flow_orchestration_participant_unmapped", exception.Code);
    }

    [TestMethod]
    public async Task GroupChatWithObservabilityEmitsTurnsWithInternalParticipantIds()
    {
        using var chatClient = new RecordingChatClient();
        var factory = new AgentFrameworkRuntimeFactory(
            new RecordingResolver(chatClient),
            NullLoggerFactory.Instance,
            new GenAiObservabilityOptions { Enabled = true });
        var agentResolver = new RecordingAgentResolver();
        var engine = new AgentFrameworkFlowOrchestrationEngine(
            agentResolver,
            new EmptyToolCatalog(),
            factory);
        var packNamespace = new ResourceNamespace("daily-life-assistant");
        var definition = new OrchestrationFlowDefinition(
            [
                new FlowTargetReference(FlowTargetKind.Agent, "agent-1", Namespace: packNamespace),
                new FlowTargetReference(FlowTargetKind.Agent, "agent-2", Namespace: packNamespace)
            ],
            new GroupChatOrchestrationPattern(2));
        var events = new List<FlowExecutionEvent>();

        await foreach (var item in engine.ExecuteAsync(new FlowOrchestrationExecutionRequest(
            TestWorkspaceId,
            "run-1",
            definition,
            JsonSerializer.SerializeToElement(new { prompt = "Discuss" }),
            "correlation-1")))
            events.Add(item);

        CollectionAssert.AreEqual(
            new[] { "agent-1", "agent-2" },
            events.OfType<FlowParticipantTurnStarted>().Select(item => item.ParticipantId).ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 2 },
            events.OfType<FlowParticipantTurnStarted>().Select(item => item.Turn).ToArray());
        Assert.IsTrue(agentResolver.ResolvedNamespaces.All(@namespace => @namespace == packNamespace));
        Assert.AreEqual(2, events.OfType<FlowParticipantTurnCompleted>().Count());
        var transitions = events.Where(item => item is FlowParticipantTurnStarted or FlowParticipantTurnCompleted)
            .Select(item => item switch
            {
                FlowParticipantTurnStarted started => $"start:{started.ParticipantId}:{started.Turn}",
                FlowParticipantTurnCompleted completed => $"complete:{completed.ParticipantId}:{completed.Turn}",
                _ => throw new UnreachableException()
            }).ToArray();
        CollectionAssert.AreEqual(
            new[] { "start:agent-1:1", "complete:agent-1:1", "start:agent-2:2", "complete:agent-2:2" },
            transitions,
            string.Join(", ", transitions));
        Assert.IsTrue(chatClient.Messages.Any(message =>
            message.Role == ChatRole.User && message.Text.Contains("Discuss", StringComparison.Ordinal)));
        Assert.HasCount(2, chatClient.Calls);
        Assert.IsTrue(chatClient.Calls[1].Any(message => message.Text.Contains("OK", StringComparison.Ordinal)),
            string.Join(" | ", chatClient.Calls[1].Select(message => $"{message.Role}:{message.AuthorName}:{message.Text}")));
        var result = events.OfType<FlowExecutionCompleted>().Single().Result;
        Assert.AreEqual(FlowOrchestrationStrategy.GroupChat, result.Strategy);
        Assert.HasCount(2, result.Participants);
    }

    [TestMethod]
    public async Task SequentialPassesThePreviousResponseAndReturnsTheLastParticipant()
    {
        using var chatClient = new RecordingChatClient();
        var events = await ExecuteOrchestrationAsync(new SequentialOrchestrationPattern(), chatClient);

        Assert.HasCount(2, chatClient.Calls);
        Assert.IsTrue(chatClient.Calls[1].Any(message => message.Text.Contains("OK", StringComparison.Ordinal)));
        var result = events.OfType<FlowExecutionCompleted>().Single().Result;
        CollectionAssert.AreEqual(new[] { "agent-1", "agent-2" }, result.Participants.Select(value => value.ParticipantId).ToArray());
        Assert.AreEqual("OK", result.FinalOutput.GetString());
    }

    [TestMethod]
    public async Task ConcurrentRunsEveryParticipantAndKeepsDeclaredResultOrder()
    {
        using var chatClient = new RecordingChatClient();
        var events = await ExecuteOrchestrationAsync(new ConcurrentOrchestrationPattern(), chatClient);

        Assert.HasCount(2, chatClient.Calls);
        Assert.IsTrue(chatClient.Calls.All(call => call.Any(message => message.Text.Contains("Discuss", StringComparison.Ordinal))));
        var result = events.OfType<FlowExecutionCompleted>().Single().Result;
        CollectionAssert.AreEqual(new[] { "agent-1", "agent-2" }, result.Participants.Select(value => value.ParticipantId).ToArray());
        Assert.AreEqual(JsonValueKind.Array, result.FinalOutput.ValueKind);
    }

    [TestMethod]
    public async Task HandoffInvokesTheDeclaredTransferAndRunsTheDestination()
    {
        using var chatClient = new RecordingChatClient
        {
            ResponseFactory = (call, _, options) =>
            {
                if (call == 1)
                {
                    var handoff = options?.Tools?.SingleOrDefault(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException($"The Handoff workflow did not expose the destination transfer tool: {string.Join(", ", options?.Tools?.Select(tool => tool.Name) ?? [])}.");
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new TextContent("Transferring."), new FunctionCallContent("handoff-1", handoff.Name, new Dictionary<string, object?>())]));
                }
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "HANDOFF_OK"));
            }
        };
        var events = await ExecuteOrchestrationAsync(
            new HandoffOrchestrationPattern("agent-1", [new FlowHandoff("agent-1", "agent-2")], Autonomous: true),
            chatClient);

        Assert.IsTrue(chatClient.Calls.Count >= 2);
        CollectionAssert.AreEqual(
            new[] { "agent-1", "agent-2" },
            events.OfType<FlowParticipantCompleted>().Select(value => value.Result.ParticipantId).ToArray());
        Assert.IsFalse(events.OfType<FlowParticipantCompleted>()
            .SelectMany(value => value.Result.Tools)
            .Any(tool => tool.StartsWith("handoff_to_", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task MagenticRunsAutonomouslyWithoutExposingTheManagerAsAParticipant()
    {
        var ledgerCalls = 0;
        using var chatClient = new RecordingChatClient
        {
            ResponseFactory = (_, messages, _) =>
            {
                var prompt = messages.LastOrDefault()?.Text ?? string.Empty;
                if (prompt.Contains("is_request_satisfied", StringComparison.Ordinal))
                {
                    ledgerCalls++;
                    var satisfied = ledgerCalls > 1;
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, $$"""
                        {
                          "is_request_satisfied": { "answer": {{satisfied.ToString().ToLowerInvariant()}}, "reason": "{{(satisfied ? "The participant answered." : "A participant must answer.")}}" },
                          "is_in_loop": { "answer": false, "reason": "No loop." },
                          "is_progress_being_made": { "answer": true, "reason": "The workflow is progressing." },
                          "next_speaker": { "answer": "agent-1", "reason": "The first participant owns the task." },
                          "instruction_or_question": { "answer": "Provide the answer.", "reason": "Complete the request." }
                        }
                        """));
                }

                var response = prompt.Contains("pre-survey", StringComparison.OrdinalIgnoreCase)
                    ? "No additional facts are required."
                    : prompt.Contains("devise a short bullet-point plan", StringComparison.OrdinalIgnoreCase)
                        ? "Ask agent-1 to answer, then return the result."
                        : ledgerCalls > 1 ? "MAGENTIC_OK" : "PARTICIPANT_OK";
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, response));
            }
        };

        var events = await ExecuteOrchestrationAsync(
            new MagenticOrchestrationPattern(
                new FlowTargetReference(FlowTargetKind.Agent, "manager"),
                MaximumRounds: 3,
                MaximumStalls: 2,
                MaximumResets: 1),
            chatClient);

        var result = events.OfType<FlowExecutionCompleted>().Single().Result;
        Assert.AreEqual(FlowOrchestrationStrategy.Magentic, result.Strategy);
        Assert.IsFalse(result.Participants.Any(participant => participant.ParticipantId == "manager"));
        Assert.IsTrue(
            result.Participants.Any(participant => participant.ParticipantId == "agent-1"),
            string.Join("\n--- CALL ---\n", chatClient.Calls.Select(call => string.Join("\n", call.Select(message => message.Text)))));
        Assert.AreEqual("MAGENTIC_OK", result.FinalOutput.GetString());
    }

    private static async Task<List<FlowExecutionEvent>> ExecuteOrchestrationAsync(
        FlowOrchestrationPattern pattern,
        RecordingChatClient chatClient)
    {
        var factory = new AgentFrameworkRuntimeFactory(
            new RecordingResolver(chatClient),
            NullLoggerFactory.Instance,
            new GenAiObservabilityOptions { Enabled = true });
        var engine = new AgentFrameworkFlowOrchestrationEngine(new RecordingAgentResolver(), new EmptyToolCatalog(), factory);
        var definition = new OrchestrationFlowDefinition(
            [
                new FlowTargetReference(FlowTargetKind.Agent, "agent-1"),
                new FlowTargetReference(FlowTargetKind.Agent, "agent-2")
            ],
            pattern);
        var events = new List<FlowExecutionEvent>();
        await foreach (var item in engine.ExecuteAsync(new FlowOrchestrationExecutionRequest(
            TestWorkspaceId,
            "run-pattern",
            definition,
            JsonSerializer.SerializeToElement(new { prompt = "Discuss" }),
            "correlation-pattern")))
            events.Add(item);
        return events;
    }

    [TestMethod]
    public async Task FactoryResolvesDeclaredProfileAndPassesAgentInstructionsToMaf()
    {
        using var chatClient = new RecordingChatClient
        {
            Metadata = new ModelChatClientMetadata("reasoning-default", "local-reasoning", "ollama", "ollama-local", "qwen3:4b", new ModelGenerationOptions { Temperature = 0.2, MaxOutputTokens = 1000 })
        };
        var resolver = new RecordingResolver(chatClient);
        var factory = new AgentFrameworkRuntimeFactory(resolver, NullLoggerFactory.Instance, new GenAiObservabilityOptions { Enabled = false });
        var definition = Definition();

        var runtime = await factory.CreateAsync(definition, "revision-1", new AgentRuntimeContext(new EmptyToolCatalog()), default);
        var result = await runtime.ExecuteAsync(new AgentExecutionRequest("What is HAVING?", "run-1", new ModelExecutionOptions(0.7f, 1500)), default);

        Assert.AreEqual(definition.ModelProfileName, resolver.RequestedProfile);
        Assert.AreEqual("sql-expert", runtime.AgentId);
        Assert.AreEqual("OK", result.Output);
        Assert.IsTrue(chatClient.Options?.Instructions?.Contains(definition.EffectiveInstructions, StringComparison.Ordinal) == true);
        Assert.IsTrue(chatClient.Messages.Any(message => message.Role == ChatRole.User && message.Text.Contains("HAVING", StringComparison.Ordinal)));
        Assert.AreEqual("qwen3:4b", chatClient.Options?.ModelId);
        Assert.AreEqual(0.7f, chatClient.Options?.Temperature);
        Assert.AreEqual(1500, chatClient.Options?.MaxOutputTokens);
        Assert.AreEqual(0.7f, result.EffectiveOptions?.Temperature);
    }

    [TestMethod]
    public async Task RuntimeResolvesCurrentProfileClientForEveryExecution()
    {
        using var first = new RecordingChatClient { Metadata = new ModelChatClientMetadata("profile", "deployment", "ollama", "local", "qwen3:1.7b") };
        using var second = new RecordingChatClient { Metadata = new ModelChatClientMetadata("profile", "deployment", "ollama", "local", "qwen3:4b") };
        var resolver = new RecordingResolver(first);
        var runtime = await new AgentFrameworkRuntimeFactory(resolver, NullLoggerFactory.Instance, new GenAiObservabilityOptions { Enabled = false })
            .CreateAsync(Definition(), "revision-1", new AgentRuntimeContext(new EmptyToolCatalog()), default);

        _ = await runtime.ExecuteAsync(new AgentExecutionRequest("first"), default);
        resolver.Client = second;
        var result = await runtime.ExecuteAsync(new AgentExecutionRequest("second"), default);

        Assert.AreEqual(2, resolver.ResolutionCount);
        Assert.AreEqual("qwen3:4b", result.ModelName);
        Assert.IsTrue(second.Messages.Any(message => message.Text.Contains("second", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RuntimeMapsCanonicalOptionsAndNormalizesStreamingEvents()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        using var chatClient = new RecordingChatClient
        {
            Metadata = new ModelChatClientMetadata(
                "profile",
                "deployment",
                "ollama",
                "local",
                "qwen3:8b",
                Generation: new ModelGenerationOptions
                {
                    Temperature = 0.2,
                    TopP = 0.8,
                    TopK = 20,
                    Seed = 42,
                    StopSequences = ["STOP"]
                },
                Reasoning: new ModelReasoningOptions { Mode = ReasoningMode.Enabled, Effort = Agentstration.Management.Abstractions.ReasoningEffort.Medium },
                Output: new ModelOutputOptions { Format = ModelOutputFormat.JsonSchema, JsonSchema = schema.RootElement.Clone(), Strict = true })
        };
        var runtime = await new AgentFrameworkRuntimeFactory(new RecordingResolver(chatClient), NullLoggerFactory.Instance, new GenAiObservabilityOptions { Enabled = false })
            .CreateAsync(Definition(), "revision-1", new AgentRuntimeContext(new EmptyToolCatalog()), default);

        var events = new List<AgentExecutionEvent>();
        await foreach (var item in runtime.ExecuteEventsAsync(
            new AgentExecutionRequest("stream", Execution: new AgentExecutionOptions { Streaming = RuntimeStreamingMode.Enabled })))
            events.Add(item);

        Assert.AreEqual("microsoft-agent-framework", runtime.RuntimeType);
        Assert.AreEqual(CapabilitySupport.Native, runtime.Capabilities.Streaming.Support);
        Assert.AreEqual(1, chatClient.StreamingCalls);
        Assert.IsTrue(events.OfType<ExecutionStarted>().Any());
        Assert.AreEqual("OK", string.Concat(events.OfType<ContentDelta>().Select(item => item.Content)));
        Assert.IsTrue(events.OfType<ExecutionCompleted>().Any());
        Assert.AreEqual(0.2f, chatClient.Options?.Temperature);
        Assert.AreEqual(0.8f, chatClient.Options?.TopP);
        Assert.AreEqual(20, chatClient.Options?.TopK);
        Assert.IsNotNull(chatClient.Options?.ResponseFormat);
        Assert.AreEqual("medium", chatClient.Options?.AdditionalProperties?["reasoning_effort"]);
    }

    [TestMethod]
    public async Task MafTelemetryIsEmittedWithoutPromptContent()
    {
        const string secretPrompt = "secret-customer-prompt";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentFrameworkRuntimeFactory.TelemetrySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        using var chatClient = new RecordingChatClient
        {
            Metadata = new ModelChatClientMetadata("profile", "deployment", "ollama", "local", "qwen3:1.7b")
        };
        var runtime = await new AgentFrameworkRuntimeFactory(
                new RecordingResolver(chatClient),
                NullLoggerFactory.Instance,
                new GenAiObservabilityOptions())
            .CreateAsync(Definition(), "revision-1", new AgentRuntimeContext(new EmptyToolCatalog()), default);

        _ = await runtime.ExecuteAsync(new AgentExecutionRequest(secretPrompt, "run-telemetry"), default);

        Assert.IsNotEmpty(stopped);
        var emittedData = string.Join(' ', stopped.SelectMany(ActivityData));
        Assert.IsFalse(emittedData.Contains(secretPrompt, StringComparison.Ordinal));
    }

    private static IEnumerable<string> ActivityData(Activity activity) =>
        activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")
            .Concat(activity.Events.SelectMany(activityEvent => activityEvent.Tags.Select(tag => $"{tag.Key}={tag.Value}")));

    private static ExecutableAgentDefinition Definition(string agentKey = "sql-expert") => new()
    {
        AgentId = Guid.NewGuid(),
        AgentKey = agentKey,
        DisplayName = "SQL Expert",
        Description = "SQL specialist",
        AgentVersion = 1,
        EffectiveInstructions = "Focus on SQL Server.",
        ModelProfileName = "reasoning-default",
        RuntimeProfileName = "maf-default",
        EffectiveToolNames = [],
        MiddlewareIds = [],
        ContextProviderIds = [],
        Capabilities = [],
        Handler = "prompt-agent",
        DefinitionHash = "hash"
    };

    private sealed class RecordingAgentResolver(IReadOnlyCollection<string>? effectiveToolNames = null) : IRuntimeAgentResolver
    {
        private readonly Dictionary<string, Guid> agentIds = new(StringComparer.Ordinal);
        public List<ResourceNamespace> ResolvedNamespaces { get; } = [];

        public Task<ResolvedRuntimeAgent> ResolveAsync(RuntimeAgentReference reference, CancellationToken cancellationToken) =>
            ResolveLatestAsync(reference.ResourceId, reference.Namespace, cancellationToken);

        public Task<ResolvedRuntimeAgent> ResolveLatestAsync(string resourceId, CancellationToken cancellationToken)
            => ResolveLatestAsync(resourceId, ResourceNamespace.Default, cancellationToken);

        public Task<ResolvedRuntimeAgent> ResolveLatestAsync(string resourceId, ResourceNamespace @namespace, CancellationToken cancellationToken)
        {
            ResolvedNamespaces.Add(@namespace);
            if (!agentIds.TryGetValue(resourceId, out var agentId))
            {
                agentId = Guid.NewGuid();
                agentIds.Add(resourceId, agentId);
            }
            var definition = Definition(resourceId) with
            {
                AgentId = agentId,
                EffectiveToolNames = effectiveToolNames ?? []
            };
            return Task.FromResult(new ResolvedRuntimeAgent(
                definition.AgentId,
                resourceId,
                1,
                $"deployment-{resourceId}",
                $"revision-{resourceId}",
                definition.RuntimeProfileName,
                definition.ModelProfileName,
                definition,
                true,
                "Ready",
                null));
        }
    }

    private sealed class ApprovalToolCatalog : IToolCatalog
    {
        public ValueTask<IReadOnlyCollection<IAgentTool>> ResolveAsync(
            IEnumerable<string> toolIds,
            CancellationToken cancellationToken = default)
        {
            CollectionAssert.AreEqual(new[] { ApprovalTool.ResourceId }, toolIds.ToArray());
            return ValueTask.FromResult<IReadOnlyCollection<IAgentTool>>([new ApprovalTool()]);
        }
    }

    private sealed class ApprovalTool : IAgentTool
    {
        public const string ResourceId = "utilities.hash.compute";
        public const string ToolName = "hash_compute";

        public string Id => ResourceId;
        public string Name => ToolName;
        public string? Description => "Compute SHA-256 after approval.";
        public string? ProviderId => "utilities";
        public string? ExternalId => ToolName;
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { text = new { type = "string" } },
            required = new[] { "text" }
        });
        public JsonElement? OutputSchema => null;
        public bool RequiresApproval => true;
    }

    private sealed class TestTool : IAgentTool
    {
        public string Id => "provider.tool";
        public string Name => "tool";
        public string? Description => "Test tool";
        public string? ProviderId => "provider";
        public string? ExternalId => "external-tool";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public JsonElement? OutputSchema => null;
        public bool RequiresApproval => false;
    }

    private sealed class SingleToolCatalog(IAgentTool tool) : IToolCatalog
    {
        public ValueTask<IReadOnlyCollection<IAgentTool>> ResolveAsync(
            IEnumerable<string> toolIds,
            CancellationToken cancellationToken = default)
        {
            CollectionAssert.AreEqual(new[] { tool.Id }, toolIds.ToArray());
            return ValueTask.FromResult<IReadOnlyCollection<IAgentTool>>([tool]);
        }
    }

    private sealed class RecordingToolExecutionPipeline(
        Func<ToolExecutionContext, CancellationToken, ValueTask<JsonElement?>>? execute = null) : IToolExecutionPipeline
    {
        public List<ToolExecutionContext> Contexts { get; } = [];

        public ValueTask<JsonElement?> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return execute?.Invoke(context, cancellationToken)
                ?? ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement("tool-result"));
        }
    }

    private sealed record TestApprovalResponse(IList<ChatMessage> Messages);

    private sealed record TestApprovalEnvelope(ToolApprovalRequestContent Approval) : IExternalRequestEnvelope
    {
        public AIContent GetInnerRequestContent() => Approval;
        public object CreateResponse(IList<ChatMessage> messages) => new TestApprovalResponse(messages);
    }

    private sealed class RecordingResolver(IChatClient client) : IChatClientResolver
    {
        public string? RequestedProfile { get; private set; }
        public IChatClient Client { get; set; } = client;
        public int ResolutionCount { get; private set; }

        public ValueTask<IChatClient> ResolveAsync(string modelProfileResourceId, CancellationToken cancellationToken = default)
        {
            RequestedProfile = modelProfileResourceId;
            ResolutionCount++;
            return ValueTask.FromResult(Client);
        }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public Func<int, IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>? ResponseFactory { get; init; }
        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];
        public IReadOnlyList<ChatMessage> Messages { get; private set; } = [];
        public ChatOptions? Options { get; private set; }
        public ModelChatClientMetadata? Metadata { get; init; }
        public int StreamingCalls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToArray();
            Calls.Add(Messages);
            Options = options;
            return Task.FromResult(ResponseFactory?.Invoke(Calls.Count, Messages, options)
                ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingCalls++;
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(ModelChatClientMetadata) ? Metadata : serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
