using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task StartupRecoveryReachesEveryActiveStateAfterTerminalHistory()
    {
        var clock = new AdvancingTimeProvider(Now);
        await using var fixture = await FlowFixture.CreateAsync(timeProvider: clock);
        for (var index = 0; index < 200; index++)
            await fixture.Repository.CreateRunAsync(CreateStoredRun($"history-{index:D4}", new("recovery"), FlowRunStatus.Succeeded), default);
        clock.Advance(TimeSpan.FromSeconds(1));

        var expectedQueue = new List<FlowRunKey>();
        var expectedInputs = new List<FlowRunKey>();
        var scopes = new[] { TestScope, FlowRunScope.Instance(TestScope.PrincipalId), FlowRunScope.Tenant(TestScope.TenantId, TestScope.PrincipalId) };
        var statuses = new[] { FlowRunStatus.Pending, FlowRunStatus.Running, FlowRunStatus.WaitingForInput, FlowRunStatus.WaitingForChild };
        for (var index = 0; index < 221; index++)
        {
            var scope = scopes[index % scopes.Length];
            var run = CreateStoredRun($"active-{index / scopes.Length:D4}", new("recovery"), statuses[index % statuses.Length]) with
            {
                Scope = scope,
                WorkspaceId = scope.WorkspaceId,
                ExecutionLeaseExpiresAt = Now
            };
            await fixture.Repository.CreateRunAsync(run, default);
            var key = new FlowRunKey(scope.ScopeRef, run.Id);
            if (run.Status == FlowRunStatus.WaitingForInput)
            {
                expectedInputs.Add(key);
                await fixture.Repository.CreateInputRequestAsync(RecoveryInput(run, clock.GetUtcNow().AddMinutes(1)), default);
            }
            else expectedQueue.Add(key);
        }

        var leased = CreateStoredRun("leased", new("recovery"), FlowRunStatus.Running) with { ExecutionLeaseExpiresAt = Now.AddHours(1) };
        await fixture.Repository.CreateRunAsync(leased, default);
        var queue = new TestFlowRunQueue();
        var inputs = new RecoveryInputSink();
        var service = new FlowRunService(fixture.Repository, queue, new TestCancellationRegistry(),
            new NullFlowRunEventSink(), clock, inputRequestSink: inputs);

        await service.InitializeAsync(default);

        CollectionAssert.AreEquivalent(expectedQueue.ToArray(), queue.Enqueued.Select(value => new FlowRunKey(value.Scope.ScopeRef, value.RunId)).ToArray());
        CollectionAssert.AreEquivalent(expectedInputs.ToArray(), inputs.Published.ToArray());
        Assert.AreEqual(FlowRunStatus.Running, (await fixture.Repository.GetRunAsync(TestScope, leased.Id, default))!.Value.Status);
    }

    [TestMethod]
    [DataRow(true, 0)]
    [DataRow(false, 0)]
    [DataRow(true, 200)]
    [DataRow(false, 200)]
    public async Task InputExpiryVisitsLaterPagesWhileEarlierRunsBecomeTerminal(bool atStartup, int terminalHistory)
    {
        var clock = new AdvancingTimeProvider(Now);
        await using var fixture = await FlowFixture.CreateAsync(timeProvider: clock);
        for (var index = 0; index < terminalHistory; index++)
            await fixture.Repository.CreateRunAsync(CreateStoredRun($"history-{index:D4}", new("expiry"), FlowRunStatus.Succeeded), default);
        clock.Advance(TimeSpan.FromSeconds(1));
        var ids = Enumerable.Range(0, 221).Select(index => $"waiting-{index:D4}").ToArray();
        foreach (var id in ids)
        {
            var run = CreateStoredRun(id, new("expiry"), FlowRunStatus.WaitingForInput);
            await fixture.Repository.CreateRunAsync(run, default);
            await fixture.Repository.CreateInputRequestAsync(RecoveryInput(run, Now), default);
        }
        var future = CreateStoredRun("future-input", new("expiry"), FlowRunStatus.WaitingForInput);
        await fixture.Repository.CreateRunAsync(future, default);
        await fixture.Repository.CreateInputRequestAsync(RecoveryInput(future, Now.AddHours(1)), default);
        clock.Advance(TimeSpan.FromSeconds(1));
        var queue = new TestFlowRunQueue();
        var service = new FlowRunService(fixture.Repository, queue, new TestCancellationRegistry(), new NullFlowRunEventSink(), clock);

        if (atStartup) await service.InitializeAsync(default);
        else await service.ExpireDueInputsAsync(default);

        foreach (var id in ids)
        {
            var run = await fixture.Repository.GetRunAsync(TestScope, id, default);
            Assert.AreEqual(FlowRunStatus.TimedOut, run!.Value.Status, id);
            var input = await fixture.Repository.GetInputRequestAsync(TestScope.WorkspaceId, id, "input", default);
            Assert.AreEqual(InputRequestStatus.Expired, input!.Value.Status, id);
        }
        Assert.AreEqual(FlowRunStatus.WaitingForInput, (await fixture.Repository.GetRunAsync(TestScope, future.Id, default))!.Value.Status);
        Assert.IsEmpty(queue.Enqueued);
    }

    private static InputRequest RecoveryInput(FlowRun run, DateTimeOffset expiresAt) => new()
    {
        ScopeRef = run.Scope.ScopeRef,
        Id = "input",
        RunId = run.Id,
        RuntimeRequestId = "runtime-input",
        Prompt = "Continue?",
        Type = InputRequestType.Text,
        Source = "assistant",
        CreatedAt = Now,
        ExpiresAt = expiresAt
    };

    private sealed class RecoveryInputSink : IFlowInputRequestSink
    {
        public List<FlowRunKey> Published { get; } = [];
        public Task PublishRequestedAsync(FlowRun run, InputRequest request, CancellationToken cancellationToken)
        {
            Published.Add(new(run.Scope.ScopeRef, run.Id));
            return Task.CompletedTask;
        }
    }
}
