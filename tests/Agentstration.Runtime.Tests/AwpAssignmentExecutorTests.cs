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
        string? correlationId = null)
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
            JsonSerializer.SerializeToElement(definition, JsonOptions), [], Guid.NewGuid(),
            RootFlowRunId: rootRunId ?? runId, ParentFlowRunId: parentRunId,
            CorrelationId: correlationId);

        return (await executor.ExecuteAsync(session, material, CancellationToken.None), handler);
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

    private sealed class GraphHandler(DateTimeOffset now) : HttpMessageHandler
    {
        public List<AwpExecutionEvent> Events { get; } = [];
        public List<AwpCreateChildFlowRequest> ChildRequests { get; } = [];
        public AwpCaptureFlowArtifactRequest? CaptureRequest { get; private set; }
        public string? ChildOutputName { get; init; }
        public string ChildOutcome { get; init; } = "success";
        public JsonElement? ChildOutput { get; init; }
        public Func<AwpCreateChildFlowRequest, JsonElement>? ChildOutputFactory { get; init; }
        public FlowGraphDefinition? ChildGraph { get; init; }
        public string? ChildCorrelationId { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            using var document = JsonDocument.Parse(bytes);
            var messageId = document.RootElement.GetProperty("messageId").GetGuid();
            object payload = request.RequestUri!.AbsolutePath switch
            {
                AwpProtocol.OpenStepExecutionPath => OpenStep(bytes),
                AwpProtocol.AppendEventsPath => AppendEvents(bytes),
                AwpProtocol.InvokeFlowToolPath => new AwpInvokeFlowToolResponse(now,
                    JsonSerializer.SerializeToElement(new { answer = "found" }),
                    "knowledge-search", "default", Guid.NewGuid(), 3,
                    "builtin", "default", "Internal", "knowledge.search",
                    new("knowledge", "default", "1.0", "search", "default")),
                AwpProtocol.CaptureFlowArtifactPath => Capture(bytes),
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

        private AwpCaptureFlowArtifactResponse Capture(byte[] bytes)
        {
            CaptureRequest = Payload<AwpCaptureFlowArtifactRequest>(bytes);
            return new(now, new("artifact-1", "result.json", "application/json"));
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
            var definition = Version("child", graph, now);
            var material = new AwpRootFlowExecutionMaterial($"material-{runId}", "1.0", "sha256:material",
                runId, "child", "default", "1.0", "sha256:child", request.Input,
                JsonSerializer.SerializeToElement(definition, JsonOptions), [], Guid.NewGuid(),
                RootFlowRunId: "flowrun-root", ParentFlowRunId: "flowrun-root",
                CorrelationId: ChildCorrelationId);
            return new(now, runId, "Pending", null, material);
        }

        private static T Payload<T>(byte[] bytes) where T : class =>
            JsonSerializer.Deserialize<AwpEnvelope<T>>(bytes, AwpProtocol.JsonOptions)!.Payload;
    }
}
