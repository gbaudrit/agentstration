using System.Text.Json;
using Agentstration.Awp.Abstractions;

namespace Agentstration.Awp.Tests;

[TestClass]
public sealed class AwpContractTests
{
    [TestMethod]
    public void ProtocolEnvelopeUsesExplicitVersionAndWebJsonShape()
    {
        var request = CreateRegistrationRequest();
        var envelope = new AwpEnvelope<AwpWorkerRegistrationRequest>(
            AwpProtocol.Version,
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            At(),
            request);

        var json = JsonSerializer.Serialize(envelope, AwpProtocol.JsonOptions);
        var actual = JsonSerializer.Deserialize<AwpEnvelope<AwpWorkerRegistrationRequest>>(json, AwpProtocol.JsonOptions)!;

        Assert.Contains("\"protocolVersion\":\"1.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"runtimeKind\":\"microsoft-agent-framework\"", json, StringComparison.Ordinal);
        Assert.AreEqual(AwpProtocol.Version, actual.ProtocolVersion);
        Assert.AreEqual(request.Worker.WorkerId, actual.Payload.Worker.WorkerId);
    }

    [TestMethod]
    public void VersionDimensionsRemainIndependent()
    {
        var request = CreateRegistrationRequest();
        var json = JsonSerializer.Serialize(request, AwpProtocol.JsonOptions);
        var actual = JsonSerializer.Deserialize<AwpWorkerRegistrationRequest>(json, AwpProtocol.JsonOptions)!;
        var capability = actual.Worker.Capabilities.Single();

        CollectionAssert.AreEqual(new[] { "1.0" }, actual.SupportedProtocolVersions.ToArray());
        Assert.AreEqual("4.0.0", actual.Worker.SoftwareVersion);
        Assert.AreEqual("2.0", capability.CapabilityVersion);
        Assert.AreEqual("5.1.0", capability.RuntimeImplementationVersion);
        CollectionAssert.AreEqual(new[] { "3.0", "3.1" }, capability.ExecutionMaterialVersions.ToArray());
    }

    [TestMethod]
    public void ProtocolIdentifiersUseCanonicalStringRepresentation()
    {
        var identifier = new AwpWorkerId(Guid.Parse("10000000-0000-0000-0000-000000000001"));

        var json = JsonSerializer.Serialize(identifier, AwpProtocol.JsonOptions);
        var actual = JsonSerializer.Deserialize<AwpWorkerId>(json, AwpProtocol.JsonOptions);

        Assert.AreEqual("\"10000000-0000-0000-0000-000000000001\"", json);
        Assert.AreEqual(identifier, actual);
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Serialize(new AwpWorkerId(Guid.Empty), AwpProtocol.JsonOptions));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<AwpWorkerId>("\"not-a-guid\"", AwpProtocol.JsonOptions));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AssignmentTargetRoundTripsAsAnExplicitVariant(bool directAgentRun)
    {
        AwpAssignmentTarget target = directAgentRun
            ? new AwpDirectAgentRunTarget("runtime-run-1")
            : new AwpRootFlowRunTarget("flow-run-1");
        var assignment = CreateAssignment(target);

        var json = JsonSerializer.Serialize(assignment, AwpProtocol.JsonOptions);
        var actual = JsonSerializer.Deserialize<AwpRunAssignment>(json, AwpProtocol.JsonOptions)!;

        Assert.Contains(directAgentRun ? "\"kind\":\"directAgentRun\"" : "\"kind\":\"rootFlowRun\"", json, StringComparison.Ordinal);
        if (directAgentRun) Assert.IsInstanceOfType<AwpDirectAgentRunTarget>(actual.Target);
        else Assert.IsInstanceOfType<AwpRootFlowRunTarget>(actual.Target);
        Assert.AreEqual("opaque-ownership-token", actual.Ownership.Token);
        Assert.AreEqual(7, actual.Ownership.FencingGeneration);
    }

    [TestMethod]
    public void OneAgentStepCanContainMultipleTurnsWithInitialAttempts()
    {
        var step = new AwpFlowStepLocation(
            "flow-run-1",
            "v3",
            "sha256:flow",
            "agent-step",
            "Generate answer",
            "Agent",
            4,
            new(Guid.Parse("30000000-0000-0000-0000-000000000001")));
        var firstTurn = new AwpAgentTurnLocation(
            new(Guid.Parse("40000000-0000-0000-0000-000000000001")),
            new(new(Guid.Parse("50000000-0000-0000-0000-000000000001")), 1));
        var secondTurn = new AwpAgentTurnLocation(
            new(Guid.Parse("40000000-0000-0000-0000-000000000002")),
            new(new(Guid.Parse("50000000-0000-0000-0000-000000000002")), 1));
        var events = new[]
        {
            CreateTurnEvent(1, step, firstTurn),
            CreateTurnEvent(2, step, secondTurn)
        };

        var json = JsonSerializer.Serialize(events, AwpProtocol.JsonOptions);
        var actual = JsonSerializer.Deserialize<AwpExecutionEvent[]>(json, AwpProtocol.JsonOptions)!;

        Assert.HasCount(2, actual);
        Assert.AreEqual(actual[0].Location.FlowStep!.StepExecutionId, actual[1].Location.FlowStep!.StepExecutionId);
        Assert.AreEqual("Generate answer", actual[0].Location.FlowStep!.StepName);
        Assert.AreEqual("Agent", actual[0].Location.FlowStep!.StepType);
        Assert.AreEqual(4, actual[0].Location.FlowStep!.DefinitionPosition);
        Assert.AreNotEqual(actual[0].Location.AgentTurn!.TurnId, actual[1].Location.AgentTurn!.TurnId);
        Assert.IsTrue(actual.All(value => value.Location.AgentTurn!.Attempt.TurnAttemptNumber == 1));
    }

    [TestMethod]
    public void AwpV1RejectsASecondTurnAttempt()
    {
        var attemptId = new AwpTurnAttemptId(Guid.Parse("50000000-0000-0000-0000-000000000001"));

        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AwpTurnAttemptReference(attemptId, 2));

        Assert.AreEqual("turnAttemptNumber", exception.ParamName);
    }

    [TestMethod]
    public void EventBatchPreservesStableOrderingIdsAndPayload()
    {
        var context = CreateCommandContext();
        var eventId = new AwpEventId(Guid.Parse("60000000-0000-0000-0000-000000000001"));
        var toolCallId = new AwpToolCallId(Guid.Parse("70000000-0000-0000-0000-000000000001"));
        var payload = JsonSerializer.SerializeToElement(new { delta = "hello" });
        var request = new AwpAppendEventsRequest(context,
        [
            new(eventId, 9, At(minutes: 1), AwpExecutionEventKind.ResponseDelta,
                new("runtime-run-1"), toolCallId, payload)
        ]);

        var json = JsonSerializer.Serialize(request, AwpProtocol.JsonOptions);
        var actual = JsonSerializer.Deserialize<AwpAppendEventsRequest>(json, AwpProtocol.JsonOptions)!;
        var executionEvent = actual.Events.Single();

        Assert.AreEqual(eventId, executionEvent.EventId);
        Assert.AreEqual(9, executionEvent.AttemptEventSequence);
        Assert.AreEqual(toolCallId, executionEvent.ToolCallId);
        Assert.AreEqual("hello", executionEvent.Payload!.Value.GetProperty("delta").GetString());
    }

    [TestMethod]
    public void HeartbeatAndTerminalContractsCarryCurrentOwnership()
    {
        var context = CreateCommandContext();
        var heartbeat = new AwpHeartbeatRequest(context, 12);
        var completion = new AwpCompleteAssignmentRequest(
            context,
            new(Guid.Parse("60000000-0000-0000-0000-000000000002")),
            JsonSerializer.SerializeToElement(new { result = "done" }));

        var heartbeatJson = JsonSerializer.Serialize(heartbeat, AwpProtocol.JsonOptions);
        var completionJson = JsonSerializer.Serialize(completion, AwpProtocol.JsonOptions);
        var actualHeartbeat = JsonSerializer.Deserialize<AwpHeartbeatRequest>(heartbeatJson, AwpProtocol.JsonOptions)!;
        var actualCompletion = JsonSerializer.Deserialize<AwpCompleteAssignmentRequest>(completionJson, AwpProtocol.JsonOptions)!;

        Assert.AreEqual(context, actualHeartbeat.Context);
        Assert.AreEqual(12, actualHeartbeat.LastEventSequence);
        Assert.AreEqual(context.AssignmentId, actualCompletion.Context.AssignmentId);
        Assert.AreEqual("done", actualCompletion.Output!.Value.GetProperty("result").GetString());
    }

    [TestMethod]
    public void StableErrorCodesAreUniqueLowerSnakeCaseValues()
    {
        var values = typeof(AwpErrorCodes).GetFields()
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.AreEqual(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(values.All(value => value.All(character => char.IsLower(character) || character == '_')));
    }

    [TestMethod]
    public void SerializerAcceptsAdditivePropertiesAndRejectsUnknownEnums()
    {
        const string compatibleResponse = """
            {
              "selectedProtocolVersion": "1.0",
              "registeredAt": "2026-10-03T08:00:00+00:00",
              "serverTime": "2026-10-03T08:00:01+00:00",
              "futureProperty": true
            }
            """;
        const string incompatibleTerminal = """
            {
              "serverTime": "2026-10-03T08:00:01+00:00",
              "state": "futureState",
              "lastEventSequence": 1
            }
            """;

        var response = JsonSerializer.Deserialize<AwpWorkerRegistrationResponse>(compatibleResponse, AwpProtocol.JsonOptions);

        Assert.IsNotNull(response);
        Assert.AreEqual(AwpProtocol.Version, response.SelectedProtocolVersion);
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<AwpTerminalResponse>(incompatibleTerminal, AwpProtocol.JsonOptions));
    }

    private static AwpWorkerRegistrationRequest CreateRegistrationRequest() => new(
        [AwpProtocol.Version],
        new(
            new(Guid.Parse("10000000-0000-0000-0000-000000000001")),
            new(Guid.Parse("10000000-0000-0000-0000-000000000002")),
            "4.0.0",
            2,
            [new(AwpRuntimeKinds.MicrosoftAgentFramework, "2.0", ["3.0", "3.1"], "5.1.0")]));

    private static AwpRunAssignment CreateAssignment(AwpAssignmentTarget target) => new(
        new(Guid.Parse("20000000-0000-0000-0000-000000000001")),
        new(Guid.Parse("20000000-0000-0000-0000-000000000002")),
        new(Guid.Parse("20000000-0000-0000-0000-000000000003"), "workspace-1"),
        target,
        new(AwpRuntimeKinds.MicrosoftAgentFramework, "2.0", "3.1"),
        new("material-1", "3.1", "sha256:material"),
        new("opaque-ownership-token", 7, At(minutes: 5), 20));

    private static AwpAssignmentCommandContext CreateCommandContext() => new(
        new(Guid.Parse("10000000-0000-0000-0000-000000000001")),
        new(Guid.Parse("10000000-0000-0000-0000-000000000002")),
        new(Guid.Parse("20000000-0000-0000-0000-000000000001")),
        new(Guid.Parse("20000000-0000-0000-0000-000000000002")),
        "opaque-ownership-token",
        7);

    private static AwpExecutionEvent CreateTurnEvent(
        long sequence,
        AwpFlowStepLocation step,
        AwpAgentTurnLocation turn) => new(
            new(Guid.Parse($"60000000-0000-0000-0000-{sequence:D12}")),
            sequence,
            At(minutes: 1).AddSeconds(sequence),
            AwpExecutionEventKind.TurnStarted,
            new("flow-run-1", step, turn));

    private static DateTimeOffset At(int minutes = 0) => new(2026, 10, 3, 8, minutes, 0, TimeSpan.Zero);
}
