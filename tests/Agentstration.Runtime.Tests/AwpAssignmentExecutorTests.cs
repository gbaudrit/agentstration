using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Flows;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Worker.MicrosoftAgentFramework;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class AwpAssignmentExecutorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow(FlowOutputOutcome.Success, "completed", "success")]
    [DataRow(FlowOutputOutcome.Error, "rejected", "error")]
    public async Task NamedOutputPreservesItsPublicIdentityAndOutcome(
        FlowOutputOutcome outcome,
        string outputName,
        string expectedOutcome)
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = outputName,
            Steps =
            [
                new OutputFlowStepDefinition
                {
                    Name = outputName,
                    Outcome = outcome,
                    Code = outcome == FlowOutputOutcome.Error ? "request_rejected" : null,
                    Message = outcome == FlowOutputOutcome.Error ? "The request was rejected." : null,
                    OutputMapping = JsonSerializer.SerializeToElement("${input}")
                }
            ]
        };

        var (result, handler) = await ExecuteGraphAsync(graph,
            JsonSerializer.SerializeToElement(new { answer = "preserved" }));

        Assert.AreEqual(outputName, result.OutputName);
        Assert.AreEqual(expectedOutcome, result.Outcome);
        Assert.AreEqual("preserved", result.Output?.GetProperty("answer").GetString());
        Assert.AreEqual(outcome == FlowOutputOutcome.Error ? "request_rejected" : null, result.ErrorCode);
        Assert.IsTrue(handler.Events.Any(value => value.Kind == AwpExecutionEventKind.StepCompleted
            && value.Location.FlowStep?.StepDefinitionId == outputName));
    }

    [TestMethod]
    public async Task FlowCallRoutesByTheChildNamedOutput()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "delegate",
            Steps =
            [
                new FlowCallStepDefinition { Name = "delegate", Flow = new("child") },
                new OutputFlowStepDefinition { Name = "approved", Outcome = FlowOutputOutcome.Success },
                new FailureFlowStepDefinition { Name = "unexpected", Code = "unexpected", Message = "Unexpected child output." }
            ],
            Transitions =
            [
                new("child-approved", "delegate", "approved", "approved"),
                new("child-default", "delegate", "completed", "unexpected")
            ]
        };
        var handler = new GraphHandler(TestNow)
        {
            ChildOutputName = "approved",
            ChildOutput = JsonSerializer.SerializeToElement(new { decision = "yes" })
        };

        var (result, _) = await ExecuteGraphAsync(graph,
            JsonSerializer.SerializeToElement(new { request = "review" }), handler);

        Assert.AreEqual("approved", result.OutputName);
        Assert.AreEqual("yes", result.Output?.GetProperty("decision").GetString());
        var delegated = handler.Events.Single(value => value.Kind == AwpExecutionEventKind.StepCompleted
            && value.Location.FlowStep?.StepDefinitionId == "delegate");
        Assert.AreEqual("child-approved", delegated.Payload?.GetProperty("selectedTransition").GetString());
    }

    [TestMethod]
    public async Task FlowCallRoutesAChildFailureThroughTheExplicitFailedTransition()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "delegate",
            Steps =
            [
                new FlowCallStepDefinition { Name = "delegate", Flow = new("child") },
                new FailureFlowStepDefinition
                {
                    Name = "failure",
                    Code = "parent_failed",
                    Message = "The child Flow failed."
                }
            ],
            Transitions = [new("child-failed", "delegate", "failed", "failure")]
        };
        var handler = new GraphHandler(TestNow)
        {
            ChildGraph = new FlowGraphDefinition
            {
                EntryStep = "failure",
                Steps =
                [
                    new FailureFlowStepDefinition
                    {
                        Name = "failure",
                        Code = "child_failed",
                        Message = "The delegated Flow failed."
                    }
                ]
            }
        };

        var (result, _) = await ExecuteGraphAsync(graph,
            JsonSerializer.SerializeToElement(new { request = "review" }), handler);

        Assert.AreEqual("error", result.Outcome);
        Assert.AreEqual("parent_failed", result.ErrorCode);
        var delegated = handler.Events.Single(value => value.Kind == AwpExecutionEventKind.StepCompleted
            && value.Location.FlowStep?.StepDefinitionId == "delegate");
        Assert.AreEqual("failed", delegated.Payload?.GetProperty("status").GetString());
        Assert.AreEqual("child-failed", delegated.Payload?.GetProperty("selectedTransition").GetString());
        Assert.AreEqual("child_failed", delegated.Payload?.GetProperty("errorCode").GetString());
    }

    [TestMethod]
    public async Task FlowCallArtifactCapturesResolvedChildProvenance()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "delegate",
            Steps =
            [
                new FlowCallStepDefinition
                {
                    Name = "delegate",
                    Flow = new("child", FlowCallVersionStrategy.Exact, "1.0"),
                    ArtifactOutput = new()
                    {
                        FileName = "child-result.json",
                        ContentMapping = JsonSerializer.SerializeToElement("${step.output}"),
                        Clean = FlowStepArtifactCleanupMode.Never
                    }
                },
                new OutputFlowStepDefinition
                {
                    Name = "completed",
                    OutputMapping = JsonSerializer.SerializeToElement("${transition.output}")
                }
            ],
            Transitions = [new("delegate-completed", "delegate", "completed", "completed")]
        };
        var handler = new GraphHandler(TestNow)
        {
            ChildOutput = JsonSerializer.SerializeToElement(new { summary = "new item" })
        };

        var (result, _) = await ExecuteGraphAsync(graph,
            JsonSerializer.SerializeToElement(new { article = "new item" }), handler);

        Assert.AreEqual("new item", result.Output?.GetProperty("summary").GetString());
        Assert.AreEqual("new item", handler.CaptureRequest?.Content.GetProperty("summary").GetString());
        Assert.AreEqual("subFlow", handler.CaptureRequest?.Provenance["invocationKind"]);
        StringAssert.StartsWith(handler.CaptureRequest?.Provenance["childFlowRunId"], "flowrun-child-");
        Assert.AreEqual("child", handler.CaptureRequest?.Provenance["childFlowName"]);
        Assert.AreEqual("default", handler.CaptureRequest?.Provenance["childFlowNamespace"]);
        Assert.AreEqual("1.0", handler.CaptureRequest?.Provenance["childFlowVersion"]);
    }

    [TestMethod]
    public async Task TransformConditionAndTransitionOutputExecuteOnTheWorker()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "normalize",
            Steps =
            [
                new TransformFlowStepDefinition
                {
                    Name = "normalize",
                    Mode = "Mapping",
                    Mapping = JsonSerializer.SerializeToElement(new { accepted = "${input.accepted}", value = "${input.value}" })
                },
                new ConditionFlowStepDefinition
                {
                    Name = "gate",
                    Left = "${transition.output.accepted}",
                    Operator = "Equals",
                    Right = "true"
                },
                new OutputFlowStepDefinition
                {
                    Name = "accepted",
                    Outcome = FlowOutputOutcome.Success,
                    OutputMapping = JsonSerializer.SerializeToElement("${steps.normalize.output}")
                },
                new FailureFlowStepDefinition { Name = "rejected", Code = "rejected", Message = "Rejected." }
            ],
            Transitions =
            [
                new("normalized", "normalize", "completed", "gate"),
                new("accepted", "gate", "true", "accepted"),
                new("rejected", "gate", "false", "rejected")
            ]
        };

        var (result, handler) = await ExecuteGraphAsync(graph,
            JsonSerializer.SerializeToElement(new { accepted = true, value = "kept" }));

        Assert.AreEqual("accepted", result.OutputName);
        Assert.AreEqual("kept", result.Output?.GetProperty("value").GetString());
        var gate = handler.Events.Single(value => value.Kind == AwpExecutionEventKind.StepCompleted
            && value.Location.FlowStep?.StepDefinitionId == "gate");
        Assert.AreEqual("accepted", gate.Payload?.GetProperty("selectedTransition").GetString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FlowCallUsesTheIncomingTransitionOutputAsItsDefaultInput(bool explicitMapping)
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "prepare",
            Steps =
            [
                new TransformFlowStepDefinition
                {
                    Name = "prepare",
                    Mapping = JsonSerializer.SerializeToElement(new { article = "analyzed item", confidence = 0.9 })
                },
                new FlowCallStepDefinition
                {
                    Name = "deliver",
                    Flow = new("child"),
                    InputMapping = explicitMapping
                        ? JsonSerializer.SerializeToElement("${transition.output}")
                        : null
                },
                new OutputFlowStepDefinition
                {
                    Name = "completed",
                    OutputMapping = JsonSerializer.SerializeToElement("${transition.output}")
                }
            ],
            Transitions =
            [
                new("prepare-deliver", "prepare", "completed", "deliver"),
                new("deliver-completed", "deliver", "completed", "completed")
            ]
        };

        var (result, handler) = await ExecuteGraphAsync(graph,
            JsonSerializer.SerializeToElement(new { article = "original item" }));

        var child = handler.ChildRequests.Single();
        Assert.AreEqual("analyzed item", child.Input.GetProperty("article").GetString());
        Assert.AreEqual(0.9, child.Input.GetProperty("confidence").GetDouble());
        Assert.AreEqual("analyzed item", result.Output?.GetProperty("article").GetString());
    }

    [TestMethod]
    [DataRow("left")]
    [DataRow("right")]
    public async Task ConvergingOutputUsesTheSelectedIncomingTransitionOnTheWorker(string route)
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "branch",
            Steps =
            [
                new ConditionFlowStepDefinition
                {
                    Name = "branch",
                    Left = "${input.route}",
                    Operator = "equals",
                    Right = "left"
                },
                new TransformFlowStepDefinition
                {
                    Name = "left",
                    Mapping = JsonSerializer.SerializeToElement(new { route = "left" })
                },
                new TransformFlowStepDefinition
                {
                    Name = "right",
                    Mapping = JsonSerializer.SerializeToElement(new { route = "right" })
                },
                new OutputFlowStepDefinition { Name = "output" }
            ],
            Transitions =
            [
                new("branch-left", "branch", "true", "left"),
                new("branch-right", "branch", "false", "right"),
                new("left-output", "left", "completed", "output"),
                new("right-output", "right", "completed", "output")
            ]
        };

        var (result, handler) = await ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { route }));

        Assert.AreEqual(route, result.Output?.GetProperty("route").GetString());
        var branch = handler.Events.Single(value => value.Kind == AwpExecutionEventKind.StepCompleted
            && value.Location.FlowStep?.StepDefinitionId == "branch");
        Assert.AreEqual($"branch-{route}", branch.Payload?.GetProperty("selectedTransition").GetString());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task AgentAndOutputDefaultsPreserveExplicitMappingsOnTheWorker(
        bool mapAgentFromRootInput,
        bool mapOutputFromRootInput)
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "transform",
            Steps =
            [
                new TransformFlowStepDefinition
                {
                    Name = "transform",
                    Mapping = JsonSerializer.SerializeToElement(new { value = "transformed" })
                },
                new AgentFlowStepDefinition
                {
                    Name = "agent",
                    Agent = new("echo-agent"),
                    InputMapping = mapAgentFromRootInput
                        ? JsonSerializer.SerializeToElement("${input}")
                        : null
                },
                new OutputFlowStepDefinition
                {
                    Name = "output",
                    OutputMapping = mapOutputFromRootInput
                        ? JsonSerializer.SerializeToElement("${input}")
                        : null
                }
            ],
            Transitions =
            [
                new("transform-agent", "transform", "completed", "agent"),
                new("agent-output", "agent", "success", "output")
            ]
        };
        var handler = new GraphHandler(TestNow) { ModelOutput = "agent-result" };

        var (result, _) = await ExecuteGraphAsync(graph,
            JsonSerializer.SerializeToElement(new { value = "original" }), handler);

        var invocation = handler.ModelRequests.Single();
        var prompt = Assert.IsInstanceOfType<AwpModelTextContent>(
            invocation.Messages.Last().Contents.Single()).Text;
        StringAssert.Contains(prompt, mapAgentFromRootInput ? "original" : "transformed");
        if (mapOutputFromRootInput)
            Assert.AreEqual("original", result.Output?.GetProperty("value").GetString());
        else
            Assert.AreEqual("agent-result", result.Output?.GetString());
    }

    [TestMethod]
    public async Task AgentFailureWithoutErrorTransitionPreservesTheWorkerError()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "agent",
            Steps = [new AgentFlowStepDefinition { Name = "agent", Agent = new("echo-agent") }]
        };
        var handler = new GraphHandler(TestNow) { ModelFailureMessage = "simulated agent failure" };

        var exception = await Assert.ThrowsAsync<AwpExecutionNotSupportedException>(() =>
            ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { }), handler));

        Assert.AreEqual("agent_step_failed", exception.Code);
        Assert.AreEqual("simulated agent failure", exception.Message);
        var failed = handler.Events.Single(value => value.Kind == AwpExecutionEventKind.StepCompleted
            && value.Location.FlowStep?.StepDefinitionId == "agent");
        Assert.AreEqual("failed", failed.Payload?.GetProperty("status").GetString());
        Assert.AreEqual("agent_step_failed", failed.Payload?.GetProperty("errorCode").GetString());
        Assert.AreEqual(JsonValueKind.Null, failed.Payload?.GetProperty("selectedTransition").ValueKind);
    }

    [TestMethod]
    public async Task AgentFailureUsesTheExplicitErrorTransition()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "agent",
            Steps =
            [
                new AgentFlowStepDefinition { Name = "agent", Agent = new("echo-agent") },
                new FailureFlowStepDefinition
                {
                    Name = "failure",
                    Code = "HANDLED_AGENT_ERROR",
                    Message = "The agent failure was handled."
                }
            ],
            Transitions = [new("agent-error", "agent", "error", "failure")]
        };
        var handler = new GraphHandler(TestNow) { ModelFailureMessage = "simulated agent failure" };

        var (result, _) = await ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { }), handler);

        Assert.AreEqual("error", result.Outcome);
        Assert.AreEqual("HANDLED_AGENT_ERROR", result.ErrorCode);
        var failed = handler.Events.Single(value => value.Kind == AwpExecutionEventKind.StepCompleted
            && value.Location.FlowStep?.StepDefinitionId == "agent");
        Assert.AreEqual("agent-error", failed.Payload?.GetProperty("selectedTransition").GetString());
        Assert.AreEqual("agent_step_failed", failed.Payload?.GetProperty("errorCode").GetString());
    }

    [TestMethod]
    public async Task ToolStepMapsArgumentsAndProjectsResolvedToolIdentity()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "notify",
            Steps =
            [
                new ToolFlowStepDefinition
                {
                    Name = "notify",
                    Tool = new("notification.send"),
                    ArgumentsMapping = JsonSerializer.SerializeToElement(new { message = "${input.message}" })
                },
                new OutputFlowStepDefinition
                {
                    Name = "output",
                    OutputMapping = JsonSerializer.SerializeToElement("${steps.notify.output}")
                }
            ],
            Transitions = [new("notify-output", "notify", "success", "output")]
        };

        var (result, handler) = await ExecuteGraphAsync(graph,
            JsonSerializer.SerializeToElement(new { message = "A new article is available." }));

        Assert.AreEqual("found", result.Output?.GetProperty("answer").GetString());
        var request = handler.FlowToolRequests.Single();
        Assert.AreEqual("A new article is available.", request.Arguments.GetProperty("message").GetString());
        var completed = handler.Events.Single(value => value.Kind == AwpExecutionEventKind.StepCompleted
            && value.Location.FlowStep?.StepDefinitionId == "notify");
        Assert.AreEqual("knowledge-search", completed.Payload?.GetProperty("tool")
            .GetProperty("ToolName").GetString());
        Assert.AreEqual(3, completed.Payload?.GetProperty("tool").GetProperty("ToolGeneration").GetInt64());
    }

    [TestMethod]
    public async Task AgentArtifactCapturesTheExactMaterialGeneration()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "answer",
            Steps =
            [
                new AgentFlowStepDefinition
                {
                    Name = "answer",
                    Agent = new("assistant"),
                    ArtifactOutput = new()
                    {
                        FileName = "answer.json",
                        Clean = FlowStepArtifactCleanupMode.Never
                    }
                },
                new OutputFlowStepDefinition
                {
                    Name = "output",
                    OutputMapping = JsonSerializer.SerializeToElement("${steps.answer.output}")
                }
            ],
            Transitions = [new("answer-output", "answer", "success", "output")]
        };
        var agent = AgentMaterial() with { AgentName = "assistant", Generation = 3 };

        var (result, handler) = await ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { }),
            agentMaterial: agent);

        Assert.AreEqual("agent-result", result.Output?.GetString());
        Assert.AreEqual("agent", handler.CaptureRequest?.Provenance["invocationKind"]);
        Assert.AreEqual("assistant", handler.CaptureRequest?.Provenance["agentResourceId"]);
        Assert.AreEqual("3", handler.CaptureRequest?.Provenance["agentVersion"]);
        Assert.IsEmpty(handler.CleanupRequests);
    }

    [TestMethod]
    public async Task ToolArtifactCaptureDoesNotReplaceTheFlowOutputAndCleansTheStagedArtifact()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "notify",
            Steps =
            [
                new ToolFlowStepDefinition
                {
                    Name = "notify",
                    Tool = new("notification.send"),
                    ArtifactOutput = new()
                    {
                        FileName = "notification.txt",
                        MediaType = "text/plain",
                        ContentMapping = JsonSerializer.SerializeToElement("${step.output.answer}")
                    }
                },
                new OutputFlowStepDefinition
                {
                    Name = "output",
                    OutputMapping = JsonSerializer.SerializeToElement("${steps.notify.output}")
                }
            ],
            Transitions = [new("notify-output", "notify", "success", "output")]
        };

        var (result, handler) = await ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { }));

        Assert.AreEqual("found", result.Output?.GetProperty("answer").GetString());
        Assert.AreEqual("notification.txt", handler.CaptureRequest?.FileName);
        Assert.AreEqual("text/plain", handler.CaptureRequest?.MediaType);
        Assert.AreEqual("found", handler.CaptureRequest?.Content.GetString());
        Assert.AreEqual("tool", handler.CaptureRequest?.Provenance["invocationKind"]);
        Assert.AreEqual("knowledge-search", handler.CaptureRequest?.Provenance["toolName"]);
        Assert.AreEqual("3", handler.CaptureRequest?.Provenance["toolGeneration"]);
        Assert.AreEqual("artifact-1", handler.CleanupRequests.Single().Artifact.ArtifactId);
    }

    [TestMethod]
    public async Task SequentialOrchestrationExecutesParticipantsAndEmitsDurableWorkerEvents()
    {
        var orchestration = new OrchestrationFlowDefinition(
            [
                new FlowTargetReference(FlowTargetKind.Agent, "researcher"),
                new FlowTargetReference(FlowTargetKind.Agent, "reviewer")
            ],
            new SequentialOrchestrationPattern());
        var definition = new FlowVersion(new WorkspaceId(Guid.NewGuid()),
            new FlowId("orchestration", ResourceNamespace.Default), "1.0", null, orchestration,
            new Dictionary<string, string>(), TestNow, null, "sha256:orchestration");
        var researcher = AgentMaterial() with
        {
            ParticipantId = "researcher",
            AgentName = "researcher",
            ModelProfileName = "awp-participant:researcher"
        };
        var reviewer = AgentMaterial() with
        {
            ParticipantId = "reviewer",
            AgentName = "reviewer",
            ModelProfileName = "awp-participant:reviewer"
        };
        using var credential = new AwpClientCredential(Guid.NewGuid(), Guid.NewGuid(), "test-instance",
            System.Text.Encoding.UTF8.GetBytes("test-worker-secret-material-at-least-32-bytes"));
        var handler = new GraphHandler(TestNow);
        using var client = new AwpClient(new Uri("https://authority.test"), credential,
            new(Guid.NewGuid()), TimeProvider.System, 0, handler);
        var assignment = new AwpRunAssignment(new(Guid.NewGuid()), new(Guid.NewGuid()),
            new(Guid.NewGuid(), Guid.NewGuid().ToString("D")), new AwpRootFlowRunTarget("flowrun-root"),
            new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", "1.0"),
            new("material-1", "1.0", "sha256:material"),
            new("ownership", 1, DateTimeOffset.UtcNow.AddMinutes(5), 15));
        var session = new AwpAssignmentSession(client, assignment, TestNow, TimeProvider.System,
            TimeSpan.FromSeconds(5));
        var executor = new AwpAssignmentExecutor(NullLoggerFactory.Instance,
            NullLogger<AwpAssignmentExecutor>.Instance);
        var material = new AwpRootFlowExecutionMaterial("material-1", "1.0", "sha256:material",
            "flowrun-root", "orchestration", "default", "1.0", "sha256:orchestration",
            JsonSerializer.SerializeToElement(new { prompt = "Investigate" }),
            JsonSerializer.SerializeToElement(definition, JsonOptions), [researcher, reviewer], Guid.NewGuid(),
            OrchestrationTimeoutMilliseconds: 600_000);

        var result = await executor.ExecuteAsync(session, material, CancellationToken.None);

        Assert.AreEqual("agent-result", result.Output?.GetProperty("finalOutput").GetString());
        Assert.HasCount(2, result.Output?.GetProperty("participants").EnumerateArray().ToArray());
        CollectionAssert.AreEqual(new[] { "researcher", "reviewer" },
            handler.ModelRequests.Select(value => value.ParticipantId).ToArray());
        CollectionAssert.AreEquivalent(new[] { "Input", "researcher", "reviewer", "Output" },
            handler.Events.Where(value => value.Kind == AwpExecutionEventKind.StepCompleted)
                .Select(value => value.Location.FlowStep?.StepDefinitionId).ToArray());
        Assert.AreEqual(2, handler.Events.Count(value => value.Kind == AwpExecutionEventKind.TurnStarted));
        Assert.AreEqual(2, handler.Events.Count(value => value.Kind == AwpExecutionEventKind.TurnCompleted));
        Assert.AreEqual(2, handler.Events.Count(value => value.Kind == AwpExecutionEventKind.ResponseDelta));
        Assert.IsGreaterThan(0, handler.CheckpointRequests.Count);
    }

    [TestMethod]
    public async Task OrchestrationTimeoutIsEnforcedByTheWorker()
    {
        var orchestration = new OrchestrationFlowDefinition(
            [new FlowTargetReference(FlowTargetKind.Agent, "agent-a")],
            new SequentialOrchestrationPattern());
        var definition = new FlowVersion(new WorkspaceId(Guid.NewGuid()),
            new FlowId("timeout", ResourceNamespace.Default), "1.0", null, orchestration,
            new Dictionary<string, string>(), TestNow, null, "sha256:timeout");
        var agent = AgentMaterial() with
        {
            ParticipantId = "agent-a",
            AgentName = "agent-a",
            ModelProfileName = "awp-participant:agent-a"
        };
        using var credential = new AwpClientCredential(Guid.NewGuid(), Guid.NewGuid(), "test-instance",
            System.Text.Encoding.UTF8.GetBytes("test-worker-secret-material-at-least-32-bytes"));
        var handler = new GraphHandler(TestNow) { StallModel = true };
        using var client = new AwpClient(new Uri("https://authority.test"), credential,
            new(Guid.NewGuid()), TimeProvider.System, 0, handler);
        var assignment = new AwpRunAssignment(new(Guid.NewGuid()), new(Guid.NewGuid()),
            new(Guid.NewGuid(), Guid.NewGuid().ToString("D")), new AwpRootFlowRunTarget("flowrun-timeout"),
            new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", "1.0"),
            new("material-timeout", "1.0", "sha256:material-timeout"),
            new("ownership", 1, DateTimeOffset.UtcNow.AddMinutes(5), 15));
        var session = new AwpAssignmentSession(client, assignment, TestNow, TimeProvider.System,
            TimeSpan.FromSeconds(5));
        var executor = new AwpAssignmentExecutor(NullLoggerFactory.Instance,
            NullLogger<AwpAssignmentExecutor>.Instance);
        var material = new AwpRootFlowExecutionMaterial("material-timeout", "1.0", "sha256:material-timeout",
            "flowrun-timeout", "timeout", "default", "1.0", "sha256:timeout",
            JsonSerializer.SerializeToElement(new { prompt = "Wait" }),
            JsonSerializer.SerializeToElement(definition, JsonOptions), [agent], Guid.NewGuid(),
            OrchestrationTimeoutMilliseconds: 25);

        var exception = await Assert.ThrowsExactlyAsync<AwpExecutionTimedOutException>(() =>
            executor.ExecuteAsync(session, material, CancellationToken.None));

        Assert.AreEqual("flow_run_timed_out", exception.Code);
    }

    [TestMethod]
    public async Task ChildOrchestrationTimeoutUsesTheExplicitTimedOutTransition()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "analyze",
            Steps =
            [
                new FlowCallStepDefinition { Name = "analyze", Flow = new("child") },
                new FailureFlowStepDefinition
                {
                    Name = "failure",
                    Code = "CHILD_FLOW_FAILED",
                    Message = "Child Flow failed."
                }
            ],
            Transitions = [new("analyze-timeout", "analyze", "timedOut", "failure")]
        };
        var childAgent = AgentMaterial() with
        {
            ParticipantId = "agent-a",
            AgentName = "agent-a",
            ModelProfileName = "awp-participant:agent-a"
        };
        var handler = new GraphHandler(TestNow)
        {
            StallModel = true,
            ChildDefinition = new OrchestrationFlowDefinition(
                [new FlowTargetReference(FlowTargetKind.Agent, "agent-a")],
                new SequentialOrchestrationPattern()),
            ChildAgents = [childAgent],
            ChildOrchestrationTimeout = TimeSpan.FromMilliseconds(25)
        };

        var (result, _) = await ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { }), handler);

        Assert.AreEqual("error", result.Outcome);
        Assert.AreEqual("CHILD_FLOW_FAILED", result.ErrorCode);
        var call = handler.Events.Single(value => value.Kind == AwpExecutionEventKind.StepCompleted
            && value.Location.FlowStep?.StepDefinitionId == "analyze");
        Assert.AreEqual("analyze-timeout", call.Payload?.GetProperty("selectedTransition").GetString());
        Assert.AreEqual("flow_run_timed_out", call.Payload?.GetProperty("errorCode").GetString());
        var child = handler.Events.Single(value => value.Kind == AwpExecutionEventKind.RunCompleted
            && value.Location.RunId.StartsWith("flowrun-child-", StringComparison.Ordinal));
        Assert.AreEqual("timedOut", child.Payload?.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task DirectFlowUsesTheAssignedRunStepTurnAndAgentCoordinates()
    {
        var definition = new FlowVersion(new WorkspaceId(Guid.NewGuid()),
            new FlowId("direct", ResourceNamespace.Default), "1.0", null,
            new DirectFlowDefinition(new(FlowTargetKind.Agent, "assistant")),
            new Dictionary<string, string>(), TestNow, null, "sha256:direct");
        var agent = AgentMaterial() with
        {
            ParticipantId = "assistant-participant",
            AgentName = "assistant",
            Generation = 3
        };
        using var credential = new AwpClientCredential(Guid.NewGuid(), Guid.NewGuid(), "test-instance",
            System.Text.Encoding.UTF8.GetBytes("test-worker-secret-material-at-least-32-bytes"));
        var handler = new GraphHandler(TestNow);
        using var client = new AwpClient(new Uri("https://authority.test"), credential,
            new(Guid.NewGuid()), TimeProvider.System, 0, handler);
        var assignment = new AwpRunAssignment(new(Guid.NewGuid()), new(Guid.NewGuid()),
            new(Guid.NewGuid(), Guid.NewGuid().ToString("D")), new AwpRootFlowRunTarget("flowrun-direct"),
            new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", "1.0"),
            new("material-direct", "1.0", "sha256:material-direct"),
            new("ownership", 1, DateTimeOffset.UtcNow.AddMinutes(5), 15));
        var session = new AwpAssignmentSession(client, assignment, TestNow, TimeProvider.System,
            TimeSpan.FromSeconds(5));
        var executor = new AwpAssignmentExecutor(NullLoggerFactory.Instance,
            NullLogger<AwpAssignmentExecutor>.Instance);
        var material = new AwpRootFlowExecutionMaterial("material-direct", "1.0", "sha256:material-direct",
            "flowrun-direct", "direct", "default", "1.0", "sha256:direct",
            JsonSerializer.SerializeToElement(new { prompt = "Use the Tool" }),
            JsonSerializer.SerializeToElement(definition, JsonOptions), [agent], Guid.NewGuid(),
            CorrelationId: "direct-scope-correlation");

        var result = await executor.ExecuteAsync(session, material, CancellationToken.None);

        Assert.AreEqual("agent-result", result.Output?.GetString());
        var step = handler.StepRequests.Single(value => value.StepDefinitionId == "Agent");
        Assert.AreEqual("flowrun-direct", step.FlowRunId);
        Assert.AreEqual("1.0", step.FlowVersion);
        Assert.AreEqual("sha256:direct", step.FlowDefinitionHash);
        var turn = handler.TurnRequests.Single();
        Assert.AreEqual("flowrun-direct", turn.RunId);
        Assert.AreEqual("Agent", turn.FlowStep?.StepDefinitionId);
        Assert.AreEqual("assistant-participant", turn.ParticipantId);
        Assert.AreEqual("assistant-participant", handler.ModelRequests.Single().ParticipantId);
        var completed = handler.Events.Single(value => value.Kind == AwpExecutionEventKind.StepCompleted
            && value.Location.FlowStep?.StepDefinitionId == "Agent");
        Assert.AreEqual("assistant", completed.Payload?.GetProperty("agentResourceId").GetString());
        Assert.AreEqual(3, completed.Payload?.GetProperty("agentVersion").GetInt64());
    }

    [TestMethod]
    public async Task RepeatFailsWithStableCodeWhenTheIterationLimitIsReached()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "repeat",
            Steps =
            [
                new RepeatFlowStepDefinition
                {
                    Name = "repeat",
                    Flow = new("child"),
                    InputMapping = JsonSerializer.SerializeToElement(new { done = false }),
                    NextInputMapping = JsonSerializer.SerializeToElement(new { done = false }),
                    Until = "${steps.repeat.output.done == true}",
                    MaximumIterations = 2
                },
                new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success }
            ],
            Transitions = [new("repeat-completed", "repeat", "completed", "completed")]
        };
        var handler = new GraphHandler(TestNow)
        {
            ChildOutputFactory = _ => JsonSerializer.SerializeToElement(new { done = false })
        };

        var exception = await Assert.ThrowsAsync<AwpExecutionNotSupportedException>(() =>
            ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { }), handler));

        Assert.AreEqual("flow_repeat_limit_exceeded", exception.Code);
        Assert.HasCount(2, handler.ChildRequests);
    }

    [TestMethod]
    public async Task RootFlowExpressionsResolveAuthoritativeExecutionContextOnTheWorker()
    {
        var graph = ExecutionContextGraph();

        var (result, _) = await ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { }),
            runId: "flowrun-context", rootRunId: "flowrun-context",
            correlationId: "correlation-context");

        Assert.AreEqual("flowrun-context", result.Output?.GetProperty("flowRunId").GetString());
        Assert.AreEqual("flowrun-context", result.Output?.GetProperty("rootFlowRunId").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.Output?.GetProperty("parentFlowRunId").ValueKind);
        Assert.AreEqual("capture", result.Output?.GetProperty("stepName").GetString());
        Assert.AreEqual("correlation-context", result.Output?.GetProperty("correlationId").GetString());
    }

    [TestMethod]
    public async Task ChildFlowExpressionsResolveRootParentAndCurrentRunOnTheWorker()
    {
        var parent = new FlowGraphDefinition
        {
            EntryStep = "child",
            Steps =
            [
                new FlowCallStepDefinition { Name = "child", Flow = new("context-child") },
                new OutputFlowStepDefinition
                {
                    Name = "output",
                    Outcome = FlowOutputOutcome.Success,
                    OutputMapping = JsonSerializer.SerializeToElement("${steps.child.output}")
                }
            ],
            Transitions = [new("child-output", "child", "output", "output")]
        };
        var handler = new GraphHandler(TestNow)
        {
            ChildGraph = ExecutionContextGraph(),
            ChildCorrelationId = "correlation-child"
        };

        var (result, _) = await ExecuteGraphAsync(parent, JsonSerializer.SerializeToElement(new { }),
            handler, correlationId: "correlation-child");

        StringAssert.StartsWith(result.Output?.GetProperty("flowRunId").GetString(), "flowrun-child-");
        Assert.AreEqual("flowrun-root", result.Output?.GetProperty("rootFlowRunId").GetString());
        Assert.AreEqual("flowrun-root", result.Output?.GetProperty("parentFlowRunId").GetString());
        Assert.AreEqual("correlation-child", result.Output?.GetProperty("correlationId").GetString());
    }

    [TestMethod]
    public async Task GraphExecutesGovernedToolRouteCapturesArtifactAndReturnsNamedOutput()
    {
        var now = DateTimeOffset.Parse("2026-10-10T08:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        using var credential = new AwpClientCredential(Guid.NewGuid(), Guid.NewGuid(), "test-instance",
            System.Text.Encoding.UTF8.GetBytes("test-worker-secret-material-at-least-32-bytes"));
        var handler = new GraphHandler(now);
        using var client = new AwpClient(new Uri("https://authority.test"), credential,
            new(Guid.NewGuid()), TimeProvider.System, 0, handler);
        var assignment = new AwpRunAssignment(new(Guid.NewGuid()), new(Guid.NewGuid()),
            new(Guid.NewGuid(), Guid.NewGuid().ToString("D")), new AwpRootFlowRunTarget("flowrun-root"),
            new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", "1.0"),
            new("material-1", "1.0", "sha256:material"),
            new("ownership", 1, DateTimeOffset.UtcNow.AddMinutes(5), 15));
        var session = new AwpAssignmentSession(client, assignment, now, TimeProvider.System,
            TimeSpan.FromSeconds(5));
        var executor = new AwpAssignmentExecutor(NullLoggerFactory.Instance,
            NullLogger<AwpAssignmentExecutor>.Instance);
        var graph = new FlowGraphDefinition
        {
            EntryStep = "lookup",
            Steps =
            [
                new ToolRouteFlowStepDefinition
                {
                    Name = "lookup",
                    ToolSet = new("knowledge", "1.0"),
                    Capability = "search",
                    ArgumentsMapping = JsonSerializer.SerializeToElement(new { query = "Agentstration" }),
                    ArtifactOutput = new()
                    {
                        FileName = "result.json",
                        MediaType = "application/json",
                        ContentMapping = JsonSerializer.SerializeToElement("${step.output}"),
                        Clean = FlowStepArtifactCleanupMode.Never
                    }
                },
                new OutputFlowStepDefinition
                {
                    Name = "completed",
                    Outcome = FlowOutputOutcome.Success,
                    OutputMapping = JsonSerializer.SerializeToElement("${steps.lookup.output}")
                }
            ],
            Transitions = [new("lookup-completed", "lookup", "success", "completed")]
        };
        var definition = new FlowVersion(new WorkspaceId(Guid.NewGuid()),
            new FlowId("flow", ResourceNamespace.Default), "1.0", null,
            new DirectFlowDefinition(new(FlowTargetKind.Agent, "unused")),
            new Dictionary<string, string>(), now, graph, "sha256:flow");
        var material = new AwpRootFlowExecutionMaterial("material-1", "1.0", "sha256:material",
            "flowrun-root", "flow", "default", "1.0", "sha256:flow",
            JsonSerializer.SerializeToElement(new { query = "Agentstration" }),
            JsonSerializer.SerializeToElement(definition, JsonOptions),
            [], Guid.NewGuid());

        var result = await executor.ExecuteAsync(session, material, CancellationToken.None);

        Assert.AreEqual("completed", result.OutputName);
        Assert.AreEqual("success", result.Outcome);
        Assert.AreEqual("found", result.Output?.GetProperty("answer").GetString());
        Assert.AreEqual("result.json", handler.CaptureRequest?.FileName);
        Assert.AreEqual("found", handler.CaptureRequest?.Content.GetProperty("answer").GetString());
        var lookup = handler.Events.Single(value => value.Kind == AwpExecutionEventKind.StepCompleted
            && value.Location.FlowStep?.StepDefinitionId == "lookup");
        Assert.AreEqual("knowledge-search", lookup.Payload?.GetProperty("tool")
            .GetProperty("ToolName").GetString());
        Assert.AreEqual("artifact-1", lookup.Payload?.GetProperty("artifacts")[0]
            .GetProperty("ArtifactId").GetString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ArtifactStorageFlowProducesDurableProjectionAndCleansTheStagedArtifact(bool failAfterStorage)
    {
        var terminal = failAfterStorage
            ? (FlowStepDefinition)new FailureFlowStepDefinition
            {
                Name = "terminal",
                Code = "after_storage_failed",
                Message = "The parent failed after Artifact storage."
            }
            : new OutputFlowStepDefinition
            {
                Name = "terminal",
                Outcome = FlowOutputOutcome.Success,
                OutputMapping = JsonSerializer.SerializeToElement("${transition.output}")
            };
        var graph = new FlowGraphDefinition
        {
            EntryStep = "lookup",
            Steps =
            [
                new ToolRouteFlowStepDefinition
                {
                    Name = "lookup",
                    DisplayName = "Produced result",
                    ToolSet = new("knowledge", "1.0"),
                    Capability = "search",
                    ArtifactOutput = new()
                    {
                        FileName = "${step.displayName}",
                        MediaType = "application/json",
                        ContentMapping = JsonSerializer.SerializeToElement("${step.output}"),
                        StorageFlow = new("artifact-storage", FlowCallVersionStrategy.Exact, "1.0")
                    }
                },
                terminal
            ],
            Transitions = [new("lookup-terminal", "lookup", "success", "terminal")]
        };
        var handler = new GraphHandler(TestNow)
        {
            ChildOutput = JsonSerializer.SerializeToElement(new
            {
                flowRunArtifactId = "11111111111111111111111111111111"
            })
        };

        var (result, _) = await ExecuteGraphAsync(graph, JsonSerializer.SerializeToElement(new { }), handler);

        Assert.AreEqual(failAfterStorage ? "error" : "success", result.Outcome);
        Assert.AreEqual(1, handler.FlowToolInvocationCount);
        Assert.AreEqual("Produced result", handler.CaptureRequest?.FileName);
        Assert.AreEqual("found", handler.CaptureRequest?.Content.GetProperty("answer").GetString());
        var storage = handler.ChildRequests.Single();
        Assert.AreEqual("artifactStorage", storage.Purpose);
        Assert.AreEqual("artifact-1", storage.Input.GetProperty("stagedArtifactId").GetString());
        Assert.AreEqual("flowrun-root", storage.Input.GetProperty("producerFlowRunId").GetString());
        Assert.AreEqual("lookup", storage.Input.GetProperty("producerFlowStepId").GetString());
        var projected = handler.Events.Single(value => value.Kind == AwpExecutionEventKind.StepCompleted
            && value.Location.FlowStep?.StepDefinitionId == "lookup");
        Assert.AreEqual("11111111111111111111111111111111", projected.Payload?
            .GetProperty("artifacts")[0].GetProperty("ArtifactId").GetString());
        Assert.AreEqual("durable", projected.Payload?.GetProperty("artifacts")[0]
            .GetProperty("Kind").GetString());
        Assert.HasCount(1, handler.CleanupRequests);
        Assert.AreEqual("artifact-1", handler.CleanupRequests.Single().Artifact.ArtifactId);
    }

    [TestMethod]
    public async Task CancellationDuringArtifactStorageCleansTheStagedArtifact()
    {
        using var cancellation = new CancellationTokenSource();
        var graph = new FlowGraphDefinition
        {
            EntryStep = "lookup",
            Steps =
            [
                new ToolRouteFlowStepDefinition
                {
                    Name = "lookup",
                    ToolSet = new("knowledge", "1.0"),
                    Capability = "search",
                    ArtifactOutput = new()
                    {
                        FileName = "result.json",
                        StorageFlow = new("artifact-storage", FlowCallVersionStrategy.Exact, "1.0")
                    }
                },
                new OutputFlowStepDefinition { Name = "completed" }
            ],
            Transitions = [new("lookup-completed", "lookup", "success", "completed")]
        };
        var handler = new GraphHandler(TestNow) { OnChildCreated = cancellation.Cancel };

        await Assert.ThrowsAsync<OperationCanceledException>(() => ExecuteGraphAsync(graph,
            JsonSerializer.SerializeToElement(new { }), handler, cancellationToken: cancellation.Token));

        Assert.HasCount(1, handler.CleanupRequests);
        Assert.AreEqual("artifact-1", handler.CleanupRequests.Single().Artifact.ArtifactId);
    }

    [TestMethod]
    public async Task RepeatCreatesIndependentChildForEveryIterationAndUsesChildNamedOutput()
    {
        var now = DateTimeOffset.Parse("2026-10-10T08:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        using var credential = new AwpClientCredential(Guid.NewGuid(), Guid.NewGuid(), "test-instance",
            System.Text.Encoding.UTF8.GetBytes("test-worker-secret-material-at-least-32-bytes"));
        var handler = new GraphHandler(now);
        using var client = new AwpClient(new Uri("https://authority.test"), credential,
            new(Guid.NewGuid()), TimeProvider.System, 0, handler);
        var assignment = new AwpRunAssignment(new(Guid.NewGuid()), new(Guid.NewGuid()),
            new(Guid.NewGuid(), Guid.NewGuid().ToString("D")), new AwpRootFlowRunTarget("flowrun-root"),
            new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", "1.0"),
            new("material-1", "1.0", "sha256:material"),
            new("ownership", 1, DateTimeOffset.UtcNow.AddMinutes(5), 15));
        var session = new AwpAssignmentSession(client, assignment, now, TimeProvider.System,
            TimeSpan.FromSeconds(5));
        var executor = new AwpAssignmentExecutor(NullLoggerFactory.Instance,
            NullLogger<AwpAssignmentExecutor>.Instance);
        var graph = new FlowGraphDefinition
        {
            EntryStep = "repeat",
            Steps =
            [
                new RepeatFlowStepDefinition
                {
                    Name = "repeat",
                    Flow = new("child"),
                    InputMapping = JsonSerializer.SerializeToElement(new { done = false }),
                    NextInputMapping = JsonSerializer.SerializeToElement(new { done = true }),
                    Until = "${steps.repeat.output.done == true}",
                    MaximumIterations = 2
                },
                new OutputFlowStepDefinition
                {
                    Name = "completed",
                    Outcome = FlowOutputOutcome.Success,
                    OutputMapping = JsonSerializer.SerializeToElement("${steps.repeat.output}")
                }
            ],
            Transitions = [new("repeat-completed", "repeat", "completed", "completed")]
        };
        var definition = Version("root", graph, now);
        var material = new AwpRootFlowExecutionMaterial("material-1", "1.0", "sha256:material",
            "flowrun-root", "root", "default", "1.0", "sha256:root",
            JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(definition, JsonOptions),
            [], Guid.NewGuid());

        var result = await executor.ExecuteAsync(session, material, CancellationToken.None);

        Assert.AreEqual("completed", result.OutputName);
        Assert.IsTrue(result.Output?.GetProperty("done").GetBoolean());
        Assert.HasCount(2, handler.ChildRequests);
        CollectionAssert.AreEqual(new int?[] { 1, 2 }, handler.ChildRequests.Select(value => value.Iteration).ToArray());
        Assert.IsTrue(handler.ChildRequests.All(value => value.Purpose == "repeat"));
        var repeated = handler.Events.Single(value => value.Kind == AwpExecutionEventKind.StepCompleted
            && value.Location.RunId == "flowrun-root" && value.Location.FlowStep?.StepDefinitionId == "repeat");
        Assert.AreEqual(2, repeated.Payload?.GetProperty("childFlowRunIds").GetArrayLength());
        Assert.AreEqual(2, repeated.Payload?.GetProperty("repeatIteration").GetInt32());
    }

    private static FlowVersion Version(string name, FlowGraphDefinition graph, DateTimeOffset publishedAt) =>
        new(new WorkspaceId(Guid.NewGuid()), new FlowId(name, ResourceNamespace.Default), "1.0", null,
            new DirectFlowDefinition(new(FlowTargetKind.Agent, "unused")),
            new Dictionary<string, string>(), publishedAt, graph, $"sha256:{name}");

    private static readonly DateTimeOffset TestNow = DateTimeOffset.Parse("2026-10-10T08:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<(AwpAssignmentExecutionResult Result, GraphHandler Handler)> ExecuteGraphAsync(
        FlowGraphDefinition graph,
        JsonElement input,
        GraphHandler? configuredHandler = null,
        string runId = "flowrun-root",
        string? rootRunId = null,
        string? parentRunId = null,
        string? correlationId = null,
        AwpExecutionAgentMaterial? agentMaterial = null,
        CancellationToken cancellationToken = default)
    {
        using var credential = new AwpClientCredential(Guid.NewGuid(), Guid.NewGuid(), "test-instance",
            System.Text.Encoding.UTF8.GetBytes("test-worker-secret-material-at-least-32-bytes"));
        var handler = configuredHandler ?? new GraphHandler(TestNow);
        using var client = new AwpClient(new Uri("https://authority.test"), credential,
            new(Guid.NewGuid()), TimeProvider.System, 0, handler);
        var assignment = new AwpRunAssignment(new(Guid.NewGuid()), new(Guid.NewGuid()),
            new(Guid.NewGuid(), Guid.NewGuid().ToString("D")), new AwpRootFlowRunTarget(rootRunId ?? runId),
            new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", "1.0"),
            new("material-1", "1.0", "sha256:material"),
            new("ownership", 1, DateTimeOffset.UtcNow.AddMinutes(5), 15));
        var session = new AwpAssignmentSession(client, assignment, TestNow, TimeProvider.System,
            TimeSpan.FromSeconds(5));
        var executor = new AwpAssignmentExecutor(NullLoggerFactory.Instance,
            NullLogger<AwpAssignmentExecutor>.Instance);
        var definition = Version("root", graph, TestNow);
        var material = new AwpRootFlowExecutionMaterial("material-1", "1.0", "sha256:material",
            runId, "root", "default", "1.0", "sha256:root", input,
            JsonSerializer.SerializeToElement(definition, JsonOptions), [agentMaterial ?? AgentMaterial()], Guid.NewGuid(),
            RootFlowRunId: rootRunId ?? runId, ParentFlowRunId: parentRunId,
            CorrelationId: correlationId);

        return (await executor.ExecuteAsync(session, material, cancellationToken), handler);
    }

    private static FlowGraphDefinition ExecutionContextGraph() => new()
    {
        EntryStep = "capture",
        Steps =
        [
            new TransformFlowStepDefinition
            {
                Name = "capture",
                Mapping = JsonSerializer.SerializeToElement(new
                {
                    flowRunId = "${execution.flowRunId}",
                    rootFlowRunId = "${execution.rootFlowRunId}",
                    parentFlowRunId = "${execution.parentFlowRunId}",
                    stepName = "${execution.stepName}",
                    correlationId = "${execution.correlationId}"
                })
            },
            new OutputFlowStepDefinition
            {
                Name = "output",
                Outcome = FlowOutputOutcome.Success,
                OutputMapping = JsonSerializer.SerializeToElement("${steps.capture.output}")
            }
        ],
        Transitions = [new("capture-output", "capture", "completed", "output")]
    };

    private static AwpExecutionAgentMaterial AgentMaterial() => new(
        "agent-material", "agent", Guid.NewGuid(), "echo-agent", 1, "revision", "sha256:agent",
        "prompt-agent", "Echo agent", "Echo agent", "Return an answer.", "default-reasoning", "default", []);

    private sealed class GraphHandler(DateTimeOffset now) : HttpMessageHandler
    {
        public List<AwpExecutionEvent> Events { get; } = [];
        public List<AwpCreateChildFlowRequest> ChildRequests { get; } = [];
        public List<AwpCleanupFlowArtifactRequest> CleanupRequests { get; } = [];
        public List<AwpInvokeModelRequest> ModelRequests { get; } = [];
        public List<AwpInvokeFlowToolRequest> FlowToolRequests { get; } = [];
        public List<AwpStoreCheckpointRequest> CheckpointRequests { get; } = [];
        public List<AwpOpenStepExecutionRequest> StepRequests { get; } = [];
        public List<AwpOpenTurnRequest> TurnRequests { get; } = [];
        public int FlowToolInvocationCount { get; private set; }
        public AwpCaptureFlowArtifactRequest? CaptureRequest { get; private set; }
        public string? ChildOutputName { get; init; }
        public string ChildOutcome { get; init; } = "success";
        public JsonElement? ChildOutput { get; init; }
        public Func<AwpCreateChildFlowRequest, JsonElement>? ChildOutputFactory { get; init; }
        public FlowGraphDefinition? ChildGraph { get; init; }
        public FlowDefinition? ChildDefinition { get; init; }
        public IReadOnlyList<AwpExecutionAgentMaterial> ChildAgents { get; init; } = [];
        public TimeSpan ChildOrchestrationTimeout { get; init; }
        public string? ChildCorrelationId { get; init; }
        public Action? OnChildCreated { get; init; }
        public string ModelOutput { get; init; } = "agent-result";
        public string? ModelFailureMessage { get; init; }
        public bool StallModel { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            if (StallModel && request.RequestUri!.AbsolutePath == AwpProtocol.InvokeModelPath)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            using var document = JsonDocument.Parse(bytes);
            var messageId = document.RootElement.GetProperty("messageId").GetGuid();
            object payload = request.RequestUri!.AbsolutePath switch
            {
                AwpProtocol.OpenStepExecutionPath => OpenStep(bytes),
                AwpProtocol.OpenTurnPath => OpenTurn(bytes),
                AwpProtocol.AppendEventsPath => AppendEvents(bytes),
                AwpProtocol.StoreCheckpointPath => StoreCheckpoint(bytes),
                AwpProtocol.InvokeModelPath => InvokeModel(bytes),
                AwpProtocol.InvokeFlowToolPath => InvokeFlowTool(bytes),
                AwpProtocol.CaptureFlowArtifactPath => Capture(bytes),
                AwpProtocol.CleanupFlowArtifactPath => Cleanup(bytes),
                AwpProtocol.CreateChildFlowPath => CreateChild(bytes),
                _ => throw new AssertFailedException($"Unexpected AWP request '{request.RequestUri.AbsolutePath}'.")
            };
            var responseType = typeof(AwpEnvelope<>).MakeGenericType(payload.GetType());
            var envelope = Activator.CreateInstance(responseType, AwpProtocol.Version, messageId, now, payload)!;
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(envelope, responseType, options: AwpProtocol.JsonOptions)
            };
        }

        private AwpOpenStepExecutionResponse OpenStep(byte[] bytes)
        {
            var request = Payload<AwpOpenStepExecutionRequest>(bytes);
            StepRequests.Add(request);
            var position = request.StepDefinitionId is "lookup" or "repeat" ? 0 : 1;
            var type = request.StepDefinitionId switch
            {
                "lookup" => "toolRoute",
                "repeat" => "repeat",
                _ => "output"
            };
            return new(now, new(request.FlowRunId, request.FlowVersion, request.FlowDefinitionHash,
                request.StepDefinitionId, request.StepDefinitionId, type, position, new(Guid.NewGuid())));
        }

        private AwpAppendEventsResponse AppendEvents(byte[] bytes)
        {
            var request = Payload<AwpAppendEventsRequest>(bytes);
            Events.AddRange(request.Events);
            return new(now, request.Events[^1].AttemptEventSequence, []);
        }

        private AwpCheckpointResponse StoreCheckpoint(byte[] bytes)
        {
            var request = Payload<AwpStoreCheckpointRequest>(bytes);
            CheckpointRequests.Add(request);
            return new(now, request.CheckpointId, request.SchemaVersion, request.CompatibilityKey,
                request.Payload, now);
        }

        private AwpOpenTurnResponse OpenTurn(byte[] bytes)
        {
            var request = Payload<AwpOpenTurnRequest>(bytes);
            TurnRequests.Add(request);
            return new(now, new(new(Guid.NewGuid()), new(new(Guid.NewGuid()), 1), request.ParticipantId));
        }

        private AwpInvokeModelResponse InvokeModel(byte[] bytes)
        {
            if (ModelFailureMessage is not null) throw new InvalidOperationException(ModelFailureMessage);
            var request = Payload<AwpInvokeModelRequest>(bytes);
            ModelRequests.Add(request);
            return new(now, [new AwpModelTextContent(ModelOutput)], "deterministic", "stop", 1, 1);
        }

        private AwpCaptureFlowArtifactResponse Capture(byte[] bytes)
        {
            CaptureRequest = Payload<AwpCaptureFlowArtifactRequest>(bytes);
            return new(now, new("artifact-1", "result.json", "application/json"));
        }

        private AwpInvokeFlowToolResponse InvokeFlowTool(byte[] bytes)
        {
            FlowToolRequests.Add(Payload<AwpInvokeFlowToolRequest>(bytes));
            FlowToolInvocationCount++;
            return new(now, JsonSerializer.SerializeToElement(new { answer = "found" }),
                "knowledge-search", "default", Guid.NewGuid(), 3,
                "builtin", "default", "Internal", "knowledge.search",
                new("knowledge", "default", "1.0", "search", "default"));
        }

        private AwpCleanupFlowArtifactResponse Cleanup(byte[] bytes)
        {
            CleanupRequests.Add(Payload<AwpCleanupFlowArtifactRequest>(bytes));
            return new(now);
        }

        private AwpChildFlowResponse CreateChild(byte[] bytes)
        {
            var request = Payload<AwpCreateChildFlowRequest>(bytes);
            ChildRequests.Add(request);
            var runId = $"flowrun-child-{request.Iteration}";
            var graph = ChildGraph ?? new FlowGraphDefinition
            {
                EntryStep = "completed",
                Steps =
                [
                    new OutputFlowStepDefinition
                    {
                        Name = "completed",
                        Outcome = ChildOutcome == "error" ? FlowOutputOutcome.Error : FlowOutputOutcome.Success,
                        Code = ChildOutcome == "error" ? "child_failed" : null,
                        Message = ChildOutcome == "error" ? "The child failed." : null,
                        OutputMapping = ChildOutputFactory?.Invoke(request) ?? ChildOutput
                            ?? JsonSerializer.SerializeToElement("${input}")
                    }
                ]
            };
            if (ChildOutputName is not null)
                graph = graph with
                {
                    EntryStep = ChildOutputName,
                    Steps =
                    [
                        new OutputFlowStepDefinition
                        {
                            Name = ChildOutputName,
                            Outcome = ChildOutcome == "error" ? FlowOutputOutcome.Error : FlowOutputOutcome.Success,
                            Code = ChildOutcome == "error" ? "child_failed" : null,
                            Message = ChildOutcome == "error" ? "The child failed." : null,
                            OutputMapping = ChildOutputFactory?.Invoke(request) ?? ChildOutput
                                ?? JsonSerializer.SerializeToElement("${input}")
                        }
                    ]
                };
            var definition = ChildDefinition is null
                ? Version("child", graph, now)
                : new FlowVersion(new WorkspaceId(Guid.NewGuid()),
                    new FlowId("child", ResourceNamespace.Default), "1.0", null, ChildDefinition,
                    new Dictionary<string, string>(), now, null, "sha256:child");
            var material = new AwpRootFlowExecutionMaterial($"material-{runId}", "1.0", "sha256:material",
                runId, "child", "default", "1.0", "sha256:child", request.Input,
                JsonSerializer.SerializeToElement(definition, JsonOptions), ChildAgents, Guid.NewGuid(),
                RootFlowRunId: "flowrun-root", ParentFlowRunId: "flowrun-root",
                CorrelationId: ChildCorrelationId,
                OrchestrationTimeoutMilliseconds: checked((int)ChildOrchestrationTimeout.TotalMilliseconds));
            OnChildCreated?.Invoke();
            return new(now, runId, "Pending", null, material);
        }

        private static T Payload<T>(byte[] bytes) where T : class =>
            JsonSerializer.Deserialize<AwpEnvelope<T>>(bytes, AwpProtocol.JsonOptions)!.Payload;
    }
}
