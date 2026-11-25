using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Work;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task DeliveryRecoversCodePullRequestProgressAndUsageWithoutRepeatingExecutionOrAcceptingWork()
    {
        await using var scenario = await DeliveryScenario.CreateAsync(WorkPlanAttemptStatus.Completed);
        scenario.Code.LoseResponse = true;
        var retained = DeliverySuccess(await scenario.Service().BeginAsync(scenario.Fixture.Workspace, scenario.Project.Id, scenario.Command(), default));
        Assert.AreEqual(WorkTaskDeliveryStage.CodePublication, retained.Stage); Assert.AreEqual(WorkTaskDeliveryStatus.Uncertain, retained.Status);
        Assert.AreEqual(1, scenario.Code.Publications); Assert.AreEqual(0, scenario.Provider.PullPosts);
        Assert.HasCount(1, (await scenario.Usage().ListAsync(scenario.Fixture.Workspace, scenario.Project.Id, 0, 25, default)).Value!.Items);
        await scenario.Fixture.ReopenAsync(); scenario.Provider.LoseResponse = true;
        retained = DeliverySuccess(await scenario.Service().ResumeAsync(scenario.Fixture.Workspace, scenario.Project.Id, retained.Id, default));
        Assert.AreEqual(WorkTaskDeliveryStage.PullRequest, retained.Stage); Assert.AreEqual(1, scenario.Provider.PullPosts);
        await scenario.Fixture.ReopenAsync(); scenario.Shared.LoseResponse = true;
        retained = DeliverySuccess(await scenario.Service().ResumeAsync(scenario.Fixture.Workspace, scenario.Project.Id, retained.Id, default));
        Assert.AreEqual(WorkTaskDeliveryStage.Contribution, retained.Stage);
        await scenario.Fixture.ReopenAsync(); scenario.Shared.LoseResponse = true;
        retained = DeliverySuccess(await scenario.Service().ResumeAsync(scenario.Fixture.Workspace, scenario.Project.Id, retained.Id, default));
        Assert.AreEqual(WorkTaskDeliveryStage.Usage, retained.Stage); Assert.IsTrue(retained.ContributionPublished);
        await scenario.Fixture.ReopenAsync();
        retained = DeliverySuccess(await scenario.Service().ResumeAsync(scenario.Fixture.Workspace, scenario.Project.Id, retained.Id, default));
        Assert.AreEqual(WorkTaskDeliveryStatus.Delivered, retained.Status); Assert.IsFalse(retained.PullRequest!.Merged); Assert.IsTrue(retained.PullRequest.Draft);
        Assert.AreEqual(1, scenario.Code.Publications); Assert.AreEqual(1, scenario.Provider.PullPosts); Assert.HasCount(1, scenario.Harness.Execution.ExecutionIds);
        Assert.AreEqual(1, scenario.Source.Reads);
        Assert.AreEqual(WorkTaskDeliveryStatus.Delivered, DeliverySuccess(await scenario.Service().BeginAsync(scenario.Fixture.Workspace, scenario.Project.Id, scenario.Command(), default)).Status);
        Assert.AreEqual("coding_delivery_command_conflict", (await scenario.Service().BeginAsync(scenario.Fixture.Workspace, scenario.Project.Id, scenario.Command() with { Body = "Changed review" }, default)).ErrorCode);
        var current = Success(await scenario.Fixture.Plans().GetAsync(scenario.Fixture.Workspace, scenario.Plan.Plan.Id, default));
        Assert.AreEqual(WorkTaskOutcome.Pending, current.Tasks.Single().Outcome); Assert.AreEqual("open", scenario.Native.Tasks["1"].State);
        await using var second = await Fixture.CreateAsync();
        var (sharing, other) = await SharingAsync(second, scenario.Shared, scenario.Project.Definition.Sharing!.ProjectId, new(Guid.NewGuid()));
        var pulled = (await sharing.PullAsync(second.Workspace, other.Id, null, default)).Value!;
        Assert.AreEqual(1, pulled.Records.Count(value => value.Key.Domain == WorkSharingDomain.Contribution));
        Assert.AreEqual(1, pulled.Records.Count(value => value.Key.Domain == WorkSharingDomain.Usage));
        var totals = (await UsageService(second, scenario.Shared, new UsageReader()).SummarizeAsync(second.Workspace, other.Id,
            new([], Source: WorkUsageReadSource.Shared), default)).Value!;
        Assert.AreEqual(30L, totals.Canonical.Inclusive.TotalTokens); Assert.AreEqual(1, totals.OriginalRecords);
        var wire = JsonSerializer.Serialize(pulled.Records, SharingJson);
        Assert.IsFalse(wire.Contains(scenario.Fixture.Workspace.ToString(), StringComparison.Ordinal));
        Assert.IsFalse(wire.Contains(scenario.Fixture.Principal.ToString(), StringComparison.Ordinal));
    }
    [TestMethod]
    [DataRow(WorkPlanAttemptStatus.Failed)]
    [DataRow(WorkPlanAttemptStatus.Cancelled)]
    public async Task DeliverySharesFailedOrCancelledEvidenceAndExplicitRetryKeepsBothAttempts(WorkPlanAttemptStatus outcome)
    {
        await using var scenario = await DeliveryScenario.CreateAsync(outcome);
        var receipt = DeliverySuccess(await scenario.Service().BeginAsync(scenario.Fixture.Workspace, scenario.Project.Id, scenario.Command(), default));
        Assert.AreEqual(outcome == WorkPlanAttemptStatus.Failed ? WorkTaskDeliveryStatus.Failed : WorkTaskDeliveryStatus.Cancelled, receipt.Status);
        Assert.AreEqual(0, scenario.Code.Publications); Assert.AreEqual(0, scenario.Provider.PullPosts); Assert.IsNull(receipt.PullRequest);
        var old = scenario.Plan.Plan.Attempts.Single().Id;
        var view = Success(await scenario.Fixture.Plans().GetAsync(scenario.Fixture.Workspace, scenario.Plan.Plan.Id, default));
        view = Success(await scenario.Harness.Service().RetryAsync(scenario.Fixture.Workspace, view.Plan.Id, new(Guid.NewGuid(), view.Plan.Version, view.Tasks.Single().TaskId), default));
        view = Success(await scenario.Harness.AdvanceAsync(view)); var attempt = view.Plan.Attempts.Last();
        await scenario.Harness.CompleteAsync(attempt, FlowRunStatus.Succeeded); scenario.Plan = Success(await scenario.Harness.AdvanceAsync(view));
        var next = scenario.Command() with { Id = Guid.NewGuid(), AttemptId = attempt.Id, ExpectedPlanVersion = scenario.Plan.Plan.Version, ExpectedTaskVersion = scenario.Plan.Tasks.Single().Version };
        receipt = DeliverySuccess(await scenario.Service().BeginAsync(scenario.Fixture.Workspace, scenario.Project.Id, next, default));
        Assert.AreEqual(WorkTaskDeliveryStatus.Delivered, receipt.Status); Assert.AreNotEqual(old, receipt.Intent.Source.AttemptId);
        var summary = (await scenario.Usage().SummarizeAsync(scenario.Fixture.Workspace, scenario.Project.Id, new([view.Plan.Id]), default)).Value!;
        Assert.AreEqual(2, summary.OriginalRecords); Assert.AreEqual(60L, summary.Canonical.Inclusive.TotalTokens);
        Assert.AreEqual(1, scenario.Code.Publications); Assert.AreEqual(1, scenario.Provider.PullPosts); Assert.HasCount(2, scenario.Harness.Execution.ExecutionIds);
    }
    [TestMethod]
    public async Task DeliveryRejectsStaleReviewsAndBaseBeforePublishingAndResumesRestoredRepositoryPermission()
    {
        await using var scenario = await DeliveryScenario.CreateAsync(WorkPlanAttemptStatus.Completed);
        var command = scenario.Command(); var service = scenario.Service();
        Assert.AreEqual("coding_delivery_review_stale", (await service.BeginAsync(scenario.Fixture.Workspace, scenario.Project.Id, command with { ExpectedPlanVersion = command.ExpectedPlanVersion + 1 }, default)).ErrorCode);
        Assert.AreEqual("coding_delivery_shared_plan_stale", (await service.BeginAsync(scenario.Fixture.Workspace, scenario.Project.Id, command with { ExpectedSharedPlanVersion = 9 }, default)).ErrorCode);
        scenario.Provider.Identity = scenario.Provider.Identity with { Revision = new string('b', 40) };
        Assert.AreEqual("coding_delivery_base_stale", (await service.BeginAsync(scenario.Fixture.Workspace, scenario.Project.Id, command, default)).ErrorCode);
        Assert.AreEqual(0, scenario.Code.Publications); Assert.AreEqual(0, scenario.Provider.PullPosts);
        scenario.Provider.Identity = scenario.Provider.Identity with { Revision = new string('a', 40) }; scenario.Code.Denied = true;
        var pending = DeliverySuccess(await service.BeginAsync(scenario.Fixture.Workspace, scenario.Project.Id, command, default));
        Assert.AreEqual(WorkTaskDeliveryStatus.Uncertain, pending.Status); Assert.AreEqual("coding_provider_denied", pending.ErrorCode);
        scenario.Code.Denied = false;
        Assert.AreEqual(WorkTaskDeliveryStatus.Delivered, DeliverySuccess(await service.ResumeAsync(scenario.Fixture.Workspace, scenario.Project.Id, pending.Id, default)).Status);
        Assert.IsFalse((await service.GetAsync(new(Guid.NewGuid()), scenario.Project.Id, pending.Id, default)).IsSuccess);
    }
    [TestMethod]
    public async Task DeliveryCancellationAfterNativeCodeWriteRecoversWithoutPublishingAnotherCommit()
    {
        await using var scenario = await DeliveryScenario.CreateAsync(WorkPlanAttemptStatus.Completed); using var cancellation = new CancellationTokenSource();
        scenario.Code.CancelAfterWrite = cancellation;
        await Assert.ThrowsAsync<OperationCanceledException>(() => scenario.Service().BeginAsync(scenario.Fixture.Workspace, scenario.Project.Id, scenario.Command(), cancellation.Token));
        Assert.AreEqual(1, scenario.Code.Publications); await scenario.Fixture.ReopenAsync(); scenario.Code.CancelAfterWrite = null;
        var resumed = DeliverySuccess(await scenario.Service().ResumeAsync(scenario.Fixture.Workspace, scenario.Project.Id, new(scenario.Command().Id), default));
        Assert.AreEqual(WorkTaskDeliveryStatus.Delivered, resumed.Status); Assert.AreEqual(1, scenario.Code.Publications); Assert.AreEqual(1, scenario.Provider.PullPosts);
    }
    private static WorkTaskDeliveryOperation DeliverySuccess(Result<WorkTaskDeliveryOperation> result)
    { Assert.IsTrue(result.IsSuccess, result.ErrorCode); return result.Value!; }

    private sealed class DeliveryScenario : IAsyncDisposable
    {
        public Fixture Fixture { get; private set; } = null!;
        public SharingProvider Shared { get; } = new(); public IssueProvider Native { get; } = new();
        public DeliveryProvider Provider { get; } = new(); public DeliveryCode Code { get; } = new(); public DeliverySource Source { get; } = new();
        public WorkProjectSnapshot Project { get; private set; } = null!; public WorkPlanView Plan { get; set; } = null!;
        public ExecutionHarness Harness { get; private set; } = null!;
        private BeginCodingDeliveryRequest command = null!;
        public BeginCodingDeliveryRequest Command() => command;
        public WorkUsageService Usage() => UsageService(Fixture, Shared, new UsageReader());
        public CodingDeliveryService Service() => new(Fixture.Deliveries, ProjectService(Fixture), Fixture.Plans(), Fixture.Tasks, Source, Code,
            new(ProjectService(Fixture), Fixture.Trees(), Fixture.Plans(), new Context(Fixture.Workspace, Fixture.Principal), Provider, new Clock(Now)),
            Issues(Fixture, Shared, Native), SharingService(Fixture, Shared), Usage(),
            new(Fixture.Store, Fixture.Tasks, Fixture.Workplace, Harness, new Context(Fixture.Workspace, Fixture.Principal), new Clock(Now), NullLogger<WorkTaskExecutionEvidenceService>.Instance),
            Fixture.Workplace, new Context(Fixture.Workspace, Fixture.Principal), new Clock(Now), NullLogger<CodingDeliveryService>.Instance);
        public static async Task<DeliveryScenario> CreateAsync(WorkPlanAttemptStatus outcome)
        {
            var scenario = new DeliveryScenario { Fixture = await Fixture.CreateAsync() }; var fixture = scenario.Fixture;
            scenario.Native.Put("1"); var project = await IssueProjectAsync(fixture, scenario.Shared, scenario.Native, new(Guid.NewGuid()));
            var code = CodingBinding(fixture) with { Name = "code", Purpose = WorkProjectBindingPurpose.CodePublication };
            project = ProjectSuccess(await ProjectService(fixture).UpdateAsync(fixture.Workspace, project.Id, new(Guid.NewGuid(), project.Version,
                project.Definition with { Bindings = [.. project.Definition.Bindings, CodingBinding(fixture), code] }), default));
            var plan = await IssuePlanAsync(fixture, project.Id, "1");
            plan = Success(await fixture.Plans().TransitionAsync(fixture.Workspace, plan.Plan.Id, Phase(plan, WorkPlanAction.Validate), default));
            plan = Success(await fixture.Plans().TransitionAsync(fixture.Workspace, plan.Plan.Id, Phase(plan, WorkPlanAction.Activate), default));
            var intake = await Issues(fixture, scenario.Shared, scenario.Native).AcknowledgeAsync(fixture.Workspace, project.Id,
                await IssueCommandAsync(fixture, project.Id, plan, "1"), default);
            Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, intake.Value!.Status, intake.ErrorCode ?? intake.Value.ErrorCode);
            project = ProjectSuccess(await ProjectService(fixture).GetAsync(fixture.Workspace, project.Id, default));
            scenario.Harness = new(fixture)
            {
                Gates = [new WorkProjectSharingAdmissionGate(SharingService(fixture, scenario.Shared)),
                new CodingIssueAdmissionGate(Issues(fixture, scenario.Shared, scenario.Native)), new DeliveryAdmission(scenario.Source.Configuration, scenario.Provider)]
            };
            await scenario.Harness.InitializeAsync();
            var task = plan.Revision.Definition.TaskIds.Single();
            var start = new StartWorkPlanExecution(Guid.NewGuid(), plan.Plan.Version, plan.Revision.Number, plan.Revision.Digest,
                [new(task, new(new FlowId("deterministic")), JsonSerializer.SerializeToElement(new CodingTaskInput(new string('a', 40), new Dictionary<string, string>()), SharingJson))])
            { ProjectId = project.Id };
            plan = Success(await scenario.Harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, start, default));
            plan = Success(await scenario.Harness.AdvanceAsync(plan)); var attempt = plan.Plan.Attempts.Single();
            await scenario.Harness.CompleteAsync(attempt, outcome == WorkPlanAttemptStatus.Completed ? FlowRunStatus.Succeeded : outcome == WorkPlanAttemptStatus.Failed ? FlowRunStatus.Failed : FlowRunStatus.Cancelled);
            scenario.Plan = Success(await scenario.Harness.AdvanceAsync(plan)); scenario.Project = project;
            var shared = (await SharingService(fixture, scenario.Shared).PullAsync(fixture.Workspace, project.Id, null, default)).Value!.Records.Single(value => value.Key.Domain == WorkSharingDomain.Plan);
            scenario.command = new(Guid.NewGuid(), plan.Plan.Id.Value, attempt.Id, project.Version, scenario.Plan.Plan.Version, scenario.Plan.Tasks.Single().Version, shared.Version, shared.Digest,
                "feat(fixture): deliver issue", "## Summary\nDeliver fixture issue.\n\n## Changes\n- Deterministic code.\n\n## Validation\n- Fixture check.\n\n## Breaking changes\nNone.");
            await scenario.Service().InitializeAsync(default); await scenario.Usage().InitializeAsync(default); return scenario;
        }
        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }
    private sealed class DeliveryAdmission(CodingExecutionConfiguration configuration, DeliveryProvider provider) : IWorkPlanAdmissionGate
    {
        public Task<Result<IReadOnlyList<WorkPlanAdmissionEvidence>>> ObserveAsync(FlowRunScope scope, WorkPlanSnapshot plan, WorkPlanRevision revision, WorkTaskId task, CancellationToken token)
        {
            var input = plan.Execution!.Targets.Single(value => value.TaskId == task).Input.Deserialize<CodingTaskInput>(SharingJson)!;
            return Task.FromResult(Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Success([new(CodingExecutionProtocol.AdmissionKind, task.ToString(),
                CodingExecutionValidation.AdmissionDigest(configuration, input, provider.Identity), Now)]));
        }
    }
    private sealed class DeliverySource : ICodingDeliveryExecutionReader
    {
        public int Reads { get; private set; }
        public CodingExecutionConfiguration Configuration { get; } = new(new("coding", default) { Uid = Guid.NewGuid(), Generation = 1 },
            new("coder", default, "1.0.0", new string('a', 64)) { Uid = Guid.NewGuid(), Generation = 1 }, "fixture/repository")
        { Checks = [new("check", "fixture-check", [])] };
        public Task<Result<CodingDeliveryExecution>> ReadAsync(FlowRunScope scope, WorkProjectId project, WorkPlanId plan, WorkPlanAttempt attempt, CancellationToken token)
        {
            Reads++; var invocation = attempt.Id;
            var coordinates = new CodingInvocationCoordinates(scope.WorkspaceId, scope.PrincipalId, project, plan, attempt.PlanRevision, attempt.PlanDigest, attempt.TaskId,
                attempt.TaskVersion, attempt.TaskDigest, attempt.Id, attempt.WorkItemId, attempt.FlowRunId, attempt.Id, invocation, invocation);
            var result = new CodingNativeResult(coordinates, new string('a', 40), null, null, "Fixture", attempt.Status == WorkPlanAttemptStatus.Completed ? 0 : 17,
                attempt.Status == WorkPlanAttemptStatus.Completed ? [new("check", 0, true)] : [], [], attempt.Status == WorkPlanAttemptStatus.Completed ? null
                    : attempt.Status == WorkPlanAttemptStatus.Cancelled ? "coding_execution_cancelled" : "coding_cli_failed")
            { CodeChanges = attempt.Status == WorkPlanAttemptStatus.Completed ? [new("answer.txt", "NDIK")] : null };
            return Task.FromResult(Result<CodingDeliveryExecution>.Success(new(result, Configuration, WorkPlanDefinition.Digest(result))));
        }
    }
    private sealed class DeliveryCode : ICodingCodePublicationGateway
    {
        private readonly Dictionary<string, WorkCodePublicationReceipt> receipts = [];
        public int Publications { get; private set; }
        public bool LoseResponse { get; set; }
        public bool Denied { get; set; }
        public CancellationTokenSource? CancelAfterWrite { get; set; }
        public Task<Result<WorkCodePublicationReceipt>> PublishAsync(WorkspaceId workspace, Guid principal, WorkProjectContextBinding binding, WorkTaskDeliveryIntent intent,
            string key, string digest, bool recover, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Denied) return Task.FromResult(Result<WorkCodePublicationReceipt>.Failure("coding_provider_denied"));
            if (receipts.TryGetValue(key, out var previous)) return Task.FromResult(Result<WorkCodePublicationReceipt>.Success(previous));
            var receipt = new WorkCodePublicationReceipt(key, digest, WorkTaskDeliveryEffectStatus.Missing, intent.HeadRef, intent.BaseRevision);
            if (recover) return Task.FromResult(Result<WorkCodePublicationReceipt>.Success(receipt));
            receipt = receipt with { Status = WorkTaskDeliveryEffectStatus.Succeeded, Revision = new string('e', 40) }; receipts[key] = receipt; Publications++;
            CancelAfterWrite?.Cancel(); token.ThrowIfCancellationRequested();
            if (LoseResponse) { LoseResponse = false; receipt = receipt with { Status = WorkTaskDeliveryEffectStatus.Uncertain }; }
            return Task.FromResult(Result<WorkCodePublicationReceipt>.Success(receipt));
        }
    }
    private sealed class DeliveryProvider : ICodingProviderGateway
    {
        public CodingRepositoryIdentity Identity { get; set; } = new("fixture", "fixture/repository", "42", new("https://fixture.test/repository.git"), new("https://fixture.test/repository"), "main", new string('a', 40), ["pull-request.create", "pull-request.read"]);
        private readonly Dictionary<string, CodingProviderResponse> receipts = [];
        public int PullPosts { get; private set; }
        public bool LoseResponse { get; set; }
        public Task<Result<CodingProviderResponse>> InvokeAsync(WorkspaceId workspace, Guid principal, WorkProjectContextBinding binding, CodingProviderRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            CodingProviderResponse response;
            if (request.Operation == CodingProviderProtocol.ResolveRepositories) response = new(request.OperationKey, request.IntentDigest, CodingProviderStatus.Succeeded, [Identity]);
            else if (receipts.TryGetValue(request.OperationKey, out var previous)) response = previous;
            else if (request.Mode == "recover") response = new(request.OperationKey, request.IntentDigest, CodingProviderStatus.Missing);
            else
            {
                var input = request.Request.Deserialize<CodingPullRequestInput>(SharingJson)!;
                response = new(request.OperationKey, request.IntentDigest, CodingProviderStatus.Succeeded, PullRequest: new(Identity.Provider, input.Repository, "7", "7",
                    new("https://fixture.test/pull/7"), "open", true, false, input.Head!, input.ExpectedHeadRevision!, input.Base!, input.ExpectedBaseRevision!, "fixture-revision"));
                receipts[request.OperationKey] = response; PullPosts++;
                if (LoseResponse) { LoseResponse = false; response = response with { Status = CodingProviderStatus.Uncertain, PullRequest = null }; }
            }
            return Task.FromResult(Result<CodingProviderResponse>.Success(response));
        }
    }
}
