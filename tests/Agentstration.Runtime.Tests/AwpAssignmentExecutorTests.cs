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

    private sealed class GraphHandler(DateTimeOffset now) : HttpMessageHandler
    {
        public List<AwpExecutionEvent> Events { get; } = [];
        public List<AwpCreateChildFlowRequest> ChildRequests { get; } = [];
        public AwpCaptureFlowArtifactRequest? CaptureRequest { get; private set; }

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
            var graph = new FlowGraphDefinition
            {
                EntryStep = "completed",
                Steps =
                [
                    new OutputFlowStepDefinition
                    {
                        Name = "completed",
                        Outcome = FlowOutputOutcome.Success,
                        OutputMapping = JsonSerializer.SerializeToElement("${input}")
                    }
                ]
            };
            var definition = Version("child", graph, now);
            var material = new AwpRootFlowExecutionMaterial($"material-{runId}", "1.0", "sha256:material",
                runId, "child", "default", "1.0", "sha256:child", request.Input,
                JsonSerializer.SerializeToElement(definition, JsonOptions), [], Guid.NewGuid(),
                RootFlowRunId: "flowrun-root", ParentFlowRunId: "flowrun-root");
            return new(now, runId, "Pending", null, material);
        }

        private static T Payload<T>(byte[] bytes) where T : class =>
            JsonSerializer.Deserialize<AwpEnvelope<T>>(bytes, AwpProtocol.JsonOptions)!.Payload;
    }
}
