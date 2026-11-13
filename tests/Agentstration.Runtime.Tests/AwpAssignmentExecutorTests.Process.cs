using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Flows;
using Agentstration.Runtime.Worker.MicrosoftAgentFramework;

namespace Agentstration.Runtime.Tests;

public sealed partial class AwpAssignmentExecutorTests
{
    [TestMethod]
    [DataRow(FlowProcessPartition.Batches)]
    [DataRow(FlowProcessPartition.TextChunks)]
    public async Task GenericProcessRunsWholeChainsInStableUnitOrderAndClosesAdmission(FlowProcessPartition partition)
    {
        var graph = ProcessGraph(new() { Partition = partition, UnitSize = 2, MaximumConcurrency = 3 });
        var handler = new GraphHandler(TestNow);
        var input = partition == FlowProcessPartition.TextChunks
            ? JsonSerializer.SerializeToElement("abcdef") : JsonSerializer.SerializeToElement(new[] { 1, 2, 3, 4, 5 });
        var (result, _) = await ExecuteGraphAsync(graph, input, handler);
        Assert.AreEqual("success", result.Outcome);
        Assert.AreEqual(3, result.Output?.GetArrayLength());
        Assert.HasCount(3, handler.ChildRequests);
        Assert.IsTrue(handler.ChildRequests.All(request => request.Purpose == "process"));
        CollectionAssert.AreEquivalent(new[] { "unit-0", "unit-1", "unit-2" }, handler.ChildRequests.Select(request => request.ProcessUnit!.Key).ToArray());
        Assert.HasCount(1, handler.CloseProcessRequests);
        if (partition == FlowProcessPartition.TextChunks)
            CollectionAssert.AreEqual(new[] { "ab", "cd", "ef" }, result.Output!.Value.EnumerateArray().Select(value => value.GetString()).ToArray());
        else Assert.AreEqual(5, result.Output!.Value[2][0].GetInt32());
        foreach (var child in handler.ChildRequests)
            Assert.HasCount(1, handler.Events.Where(value => value.Kind == AwpExecutionEventKind.RunCompleted && value.Location.RunId == $"flowrun-child-{child.Iteration}").ToArray());
    }

    [TestMethod]
    public async Task EmptyProcessClosesAdmissionWithoutInventingAChildRun()
    {
        var (result, handler) = await ExecuteGraphAsync(ProcessGraph(new()), JsonSerializer.SerializeToElement(Array.Empty<int>()));
        Assert.AreEqual("success", result.Outcome);
        Assert.AreEqual(0, result.Output?.GetArrayLength());
        Assert.IsEmpty(handler.ChildRequests);
        Assert.HasCount(1, handler.CloseProcessRequests);
    }

    [TestMethod]
    public async Task CompletedProcessUnitsAreReusedWithoutEventsOrSideEffects()
    {
        var handler = new GraphHandler(TestNow) { ChildReplay = request => new(TestNow, "completed-unit", "Succeeded",
            JsonSerializer.SerializeToElement("cached")) { Outcome = "success" } };
        var (result, _) = await ExecuteGraphAsync(ProcessGraph(new()), JsonSerializer.SerializeToElement(new[] { 1 }), handler);
        Assert.AreEqual("cached", result.Output!.Value[0].GetString());
        Assert.IsFalse(handler.Events.Any(value => value.Location.RunId == "completed-unit"));
        Assert.IsEmpty(handler.FlowToolRequests);
    }

    [TestMethod]
    public async Task FailedProcessReplayPreservesTheOriginalModelFailureCoordinates()
    {
        var original = new FlowFailureOrigin("failed-unit", "correction", "unit-0");
        var handler = new GraphHandler(TestNow) { ChildReplay = request => new(TestNow, "failed-unit", "Failed", null)
        { Outcome = "error", ErrorCode = "model_output_token_limit", ErrorMessage = "No final response.",
            ErrorOrigin = JsonSerializer.SerializeToElement(original, JsonOptions) } };
        var (result, _) = await ExecuteGraphAsync(ProcessGraph(new()), JsonSerializer.SerializeToElement(new[] { 1 }), handler);
        Assert.AreEqual("model_output_token_limit", result.ErrorCode);
        Assert.AreEqual(original, result.ErrorOrigin);
        Assert.IsFalse(handler.Events.Any(value => value.Location.RunId == "failed-unit"));
    }

    [TestMethod]
    public async Task AssignmentProcessCapacityBoundsOverlapAndNestedProcessCannotStarve()
    {
        using var credential = new AwpClientCredential(Guid.NewGuid(), Guid.NewGuid(), "test-instance",
            System.Text.Encoding.UTF8.GetBytes("test-worker-secret-material-at-least-32-bytes"));
        using var client = new AwpClient(new Uri("https://authority.test"), credential, new(Guid.NewGuid()), TimeProvider.System, 0, new GraphHandler(TestNow));
        var assignment = new AwpRunAssignment(new(Guid.NewGuid()), new(Guid.NewGuid()), new(Guid.NewGuid(), Guid.NewGuid().ToString()),
            new AwpRootFlowRunTarget("root"), new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", "1.0"),
            new("material", "1.0", "digest"), new("ownership", 1, DateTimeOffset.UtcNow.AddMinutes(5), 15));
        var session = new AwpAssignmentSession(client, assignment, TestNow, TimeProvider.System, TimeSpan.FromSeconds(5), 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tasks = Enumerable.Range(0, 4).Select(_ => session.ExecuteProcessUnitAsync(async () =>
        {
            var count = Interlocked.Increment(ref active);
            InterlockedExtensionsMax(ref maximum, count);
            if (count == 2) entered.TrySetResult();
            await release.Task.WaitAsync(timeout.Token);
            // The nested unit executes on the enclosing lane even though both slots are occupied.
            var nested = await session.ExecuteProcessUnitAsync(() => Task.FromResult(session.IsInsideProcess), timeout.Token);
            Assert.IsTrue(nested);
            Interlocked.Decrement(ref active);
            return true;
        }, timeout.Token)).ToArray();
        await entered.Task.WaitAsync(timeout.Token);
        Assert.AreEqual(2, active);
        release.SetResult();
        await Task.WhenAll(tasks);
        Assert.AreEqual(2, maximum);
        Assert.AreEqual(0, active);
    }

    private static void InterlockedExtensionsMax(ref int target, int value)
    {
        int current;
        do { current = Volatile.Read(ref target); if (current >= value) return; }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static FlowGraphDefinition ProcessGraph(TransformProcessDefinition process) => new()
    {
        EntryStep = "partition",
        Steps = [new TransformFlowStepDefinition { Name = "partition", Processing = process },
            new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success },
            new OutputFlowStepDefinition { Name = "failed", Outcome = FlowOutputOutcome.Error }],
        Transitions = [new("done", "partition", "completed", "completed"), new("error", "partition", "error", "failed")]
    };
}
