using System.Collections.Concurrent;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task TaskProviderRecoversLostPublicationAfterRestartWithoutActivatingPlanOrPublishingChildren()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (project, plan) = await ProviderProjectAsync(fixture);
        var gateway = new TaskProviderFixture { LoseResponse = true };
        var service = TaskProviders(fixture, gateway); await service.InitializeAsync(default);
        var command = Publish(project, plan);
        var uncertain = Operation(await service.BeginAsync(fixture.Workspace, project.Id, command, default));
        Assert.AreEqual(WorkTaskOperationStatus.Uncertain, uncertain.Status);
        Assert.AreEqual(1, gateway.Effects);
        await fixture.ReopenAsync();
        service = TaskProviders(fixture, gateway); await service.InitializeAsync(default);
        var recovered = Operation(await service.ResumeAsync(fixture.Workspace, project.Id, command.Id, default));
        Assert.AreEqual(WorkTaskOperationStatus.Succeeded, recovered.Status);
        Assert.AreEqual("issue-1", recovered.Task!.TaskId);
        Assert.AreEqual(1, gateway.Effects);
        Assert.AreEqual(1, gateway.Recoveries);
        Assert.AreEqual(recovered.Version, Operation(await service.BeginAsync(fixture.Workspace, project.Id, command, default)).Version);
        Assert.AreEqual(WorkPlanStatus.Draft, Success(await fixture.Plans().GetAsync(fixture.Workspace, plan.Plan.Id, default)).Plan.Status);
        Assert.AreEqual(project.Version, ProjectSuccess(await ProjectService(fixture).GetAsync(fixture.Workspace, project.Id, default)).Version);
        var tree = (await fixture.Trees().GetAsync(fixture.Workspace, plan.Revision.Definition.TaskIds[0], default)).Value!;
        Assert.IsTrue(tree.Tasks.All(value => value.Status == WorkItemStatus.Pending));
        var receipts = (await service.ListAsync(fixture.Workspace, project.Id, 0, 1, default)).Value!;
        Assert.HasCount(1, receipts.Items); Assert.IsFalse(receipts.HasMore);
        Assert.AreEqual("task_provider_operation_not_found", (await TaskProviders(fixture, gateway, owner: Guid.NewGuid()).GetAsync(fixture.Workspace, project.Id, command.Id, default)).ErrorCode);
        Assert.AreEqual("task_provider_operation_not_found", (await service.ResumeAsync(WorkspaceId.New(), project.Id, command.Id, default)).ErrorCode);
    }

    [TestMethod]
    public async Task TaskProviderRejectsChangedReplayStaleWorkAndInvalidBindingsWithoutEffects()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (project, plan) = await ProviderProjectAsync(fixture);
        var gateway = new TaskProviderFixture(); var service = TaskProviders(fixture, gateway); await service.InitializeAsync(default);
        var command = Publish(project, plan);
        Assert.AreEqual("work_project_conflict", (await service.BeginAsync(fixture.Workspace, project.Id, command with { ExpectedProjectVersion = 2 }, default)).ErrorCode);
        Assert.AreEqual("task_provider_binding_missing", (await service.BeginAsync(fixture.Workspace, project.Id, command with { BindingName = "absent" }, default)).ErrorCode);
        Assert.AreEqual("work_plan_revision_conflict", (await service.BeginAsync(fixture.Workspace, project.Id, command with { PlanRevision = 2 }, default)).ErrorCode);
        Assert.AreEqual("task_provider_command_invalid", (await service.BeginAsync(fixture.Workspace, project.Id,
            command with { Input = command.Input with { Body = new string('x', 32_769) } }, default)).ErrorCode);
        gateway.ResolutionError = "task_provider_capability_missing";
        Assert.AreEqual(gateway.ResolutionError, (await service.BeginAsync(fixture.Workspace, project.Id, command, default)).ErrorCode);
        Assert.AreEqual(0, gateway.Effects);
        gateway.ResolutionError = null;
        _ = Operation(await service.BeginAsync(fixture.Workspace, project.Id, command, default));
        Assert.AreEqual("task_provider_operation_conflict", (await service.BeginAsync(fixture.Workspace, project.Id,
            command with { Input = command.Input with { Title = "Different intent" } }, default)).ErrorCode);
        Assert.AreEqual("task_provider_page_invalid", (await service.ListAsync(fixture.Workspace, project.Id, -1, 10, default)).ErrorCode);
        Assert.AreEqual(1, gateway.Effects);
    }

    [TestMethod]
    public async Task TaskProviderConcurrentReplaysHaveOneExternalEffectAndOneLocalReceipt()
    {
        await using var fixture = await Fixture.CreateAsync(); var (project, plan) = await ProviderProjectAsync(fixture);
        var gateway = new TaskProviderFixture(); var service = TaskProviders(fixture, gateway); await service.InitializeAsync(default);
        var command = Publish(project, plan);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.BeginAsync(fixture.Workspace, project.Id, command, default)));
        Assert.IsTrue(results.All(result => result.IsSuccess), string.Join(",", results.Select(value => value.ErrorCode)));
        Assert.AreEqual(1, gateway.Effects);
        Assert.HasCount(1, (await service.ListAsync(fixture.Workspace, project.Id, 0, 100, default)).Value!.Items);
        Assert.AreEqual(WorkTaskOperationStatus.Succeeded, Operation(await service.GetAsync(fixture.Workspace, project.Id, command.Id, default)).Status);
    }

    [TestMethod]
    public async Task TaskProviderRecoversPendingIntentAfterLocalReceiptWriteFailure()
    {
        await using var fixture = await Fixture.CreateAsync(); var (project, plan) = await ProviderProjectAsync(fixture);
        var gateway = new TaskProviderFixture(); var store = new FailingOperationReceipt(fixture.Operations);
        var service = TaskProviders(fixture, gateway, store); await service.InitializeAsync(default);
        var command = Publish(project, plan);
        await Assert.ThrowsAsync<IOException>(() => service.BeginAsync(fixture.Workspace, project.Id, command, default));
        Assert.AreEqual(WorkTaskOperationStatus.Pending, (await fixture.Operations.GetAsync(fixture.Workspace, fixture.Principal, command.Id, default))!.Status);
        await fixture.ReopenAsync();
        var recovered = Operation(await TaskProviders(fixture, gateway).ResumeAsync(fixture.Workspace, project.Id, command.Id, default));
        Assert.AreEqual(WorkTaskOperationStatus.Succeeded, recovered.Status);
        Assert.AreEqual(1, gateway.Effects);
    }

    [TestMethod]
    public async Task TaskProviderCancellationAfterEffectRetainsUncertaintyAndAllowsExplicitRecovery()
    {
        await using var fixture = await Fixture.CreateAsync(); var (project, plan) = await ProviderProjectAsync(fixture);
        using var cancellation = new CancellationTokenSource();
        var gateway = new TaskProviderFixture { CancelAfterWrite = cancellation };
        var service = TaskProviders(fixture, gateway); await service.InitializeAsync(default);
        var command = Publish(project, plan);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.BeginAsync(fixture.Workspace, project.Id, command, cancellation.Token));
        var pending = Operation(await service.GetAsync(fixture.Workspace, project.Id, command.Id, default));
        Assert.AreEqual(WorkTaskOperationStatus.Uncertain, pending.Status);
        gateway.CancelAfterWrite = null;
        Assert.AreEqual(WorkTaskOperationStatus.Succeeded, Operation(await service.ResumeAsync(fixture.Workspace, project.Id, command.Id, default)).Status);
        Assert.AreEqual(1, gateway.Effects);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TaskProviderNeverReissuesAnObsoleteIntentOrChangedBinding(bool changeBinding)
    {
        await using var fixture = await Fixture.CreateAsync(); var (project, plan) = await ProviderProjectAsync(fixture);
        var gateway = new TaskProviderFixture { SuppressEffect = true };
        var service = TaskProviders(fixture, gateway); await service.InitializeAsync(default);
        var command = Publish(project, plan);
        _ = Operation(await service.BeginAsync(fixture.Workspace, project.Id, command, default));
        if (changeBinding)
            _ = ProjectSuccess(await ProjectService(fixture).UpdateAsync(fixture.Workspace, project.Id, new(Guid.NewGuid(), project.Version,
                project.Definition with { Bindings = [] }), default));
        else
            _ = Success(await fixture.Plans().ReviseAsync(fixture.Workspace, plan.Plan.Id, new(Guid.NewGuid(), plan.Plan.Version,
                plan.Revision.Definition with { Intent = "New intent" }), default));
        gateway.SuppressEffect = false;
        var rejected = await service.ResumeAsync(fixture.Workspace, project.Id, command.Id, default);
        Assert.AreEqual(changeBinding ? "task_provider_binding_changed" : "task_provider_work_changed", rejected.ErrorCode);
        Assert.AreEqual(0, gateway.Effects);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TaskProviderAdmissionRollsBackWhenObservedProjectOrPlanChanges(bool changeProject)
    {
        await using var fixture = await Fixture.CreateAsync(); var (project, plan) = await ProviderProjectAsync(fixture);
        var gateway = new TaskProviderFixture();
        gateway.BeforeResolve = async () =>
        {
            if (changeProject)
                _ = ProjectSuccess(await ProjectService(fixture).UpdateAsync(fixture.Workspace, project.Id,
                    new(Guid.NewGuid(), project.Version, project.Definition with { Description = "Concurrent edit" }), default));
            else
                _ = Success(await fixture.Plans().ReviseAsync(fixture.Workspace, plan.Plan.Id,
                    new(Guid.NewGuid(), plan.Plan.Version, plan.Revision.Definition with { Intent = "Concurrent revision" }), default));
        };
        var service = TaskProviders(fixture, gateway); await service.InitializeAsync(default);
        var command = Publish(project, plan);
        Assert.AreEqual("task_provider_work_changed", (await service.BeginAsync(fixture.Workspace, project.Id, command, default)).ErrorCode);
        Assert.IsNull(await fixture.Operations.GetAsync(fixture.Workspace, fixture.Principal, command.Id, default));
        Assert.AreEqual(0, gateway.Effects);
    }

    [TestMethod]
    public async Task TaskProviderDoesNotAcceptAReceiptForAnotherOperation()
    {
        await using var fixture = await Fixture.CreateAsync(); var (project, plan) = await ProviderProjectAsync(fixture);
        var gateway = new TaskProviderFixture { WrongCoordinate = true };
        var service = TaskProviders(fixture, gateway); await service.InitializeAsync(default);
        var receipt = Operation(await service.BeginAsync(fixture.Workspace, project.Id, Publish(project, plan), default));
        Assert.AreEqual(WorkTaskOperationStatus.Uncertain, receipt.Status);
        Assert.AreEqual("task_provider_response_invalid", receipt.ErrorCode);
        Assert.IsNull(receipt.Task);
    }

    [TestMethod]
    public async Task TaskProviderNeverReplaysAnUncorrelatedMissingReceipt()
    {
        await using var fixture = await Fixture.CreateAsync(); var (project, plan) = await ProviderProjectAsync(fixture);
        var gateway = new TaskProviderFixture { SuppressEffect = true, WrongCoordinate = true };
        var service = TaskProviders(fixture, gateway); await service.InitializeAsync(default);
        var command = Publish(project, plan);
        _ = Operation(await service.BeginAsync(fixture.Workspace, project.Id, command, default));
        gateway.SuppressEffect = false;
        var resumed = Operation(await service.ResumeAsync(fixture.Workspace, project.Id, command.Id, default));
        Assert.AreEqual(WorkTaskOperationStatus.Uncertain, resumed.Status);
        Assert.AreEqual("task_provider_response_invalid", resumed.ErrorCode); Assert.AreEqual(0, gateway.Effects);
    }

    private static WorkTaskOperation Operation(Result<WorkTaskOperation> result) { Assert.IsTrue(result.IsSuccess, result.ErrorCode); return result.Value!; }
    private static BeginWorkTaskOperation Publish(WorkProjectSnapshot project, WorkPlanView plan) => new(new(Guid.NewGuid()), project.Version, "tracker",
        new(ExternalTaskOperationKind.Publish, "fixture/repository", Title: "Parent work", Body: "Keep children local"), plan.Plan.Id, plan.Revision.Number, plan.Revision.Digest);
    private static async Task<(WorkProjectSnapshot Project, WorkPlanView Plan)> ProviderProjectAsync(Fixture fixture)
    {
        var tree = await fixture.CreateTreeAsync(); var plan = Success(await fixture.Plans().CreateAsync(fixture.Workspace, WorkPlanId.New(), Command(tree), default));
        var projects = ProjectService(fixture); await projects.InitializeAsync(default);
        var binding = new WorkProjectContextBinding("tracker", WorkProjectBindingPurpose.TaskOperations,
            new(ScopedResourceAddress.Create(ResourceScopeRef.Workspace(fixture.Workspace.Value), default, "ToolProvider", "fixture"), Guid.NewGuid(), 1));
        var project = ProjectSuccess(await projects.CreateAsync(fixture.Workspace, WorkProjectId.New(), "provider-project", default,
            new(Guid.NewGuid(), 0, new("Provider Project", null, [plan.Plan.Id], []) { Bindings = [binding] }), default));
        return (project, plan);
    }
    private static WorkTaskProviderService TaskProviders(Fixture fixture, IWorkTaskProviderGateway gateway, IWorkTaskOperationRepository? store = null, Guid? owner = null) =>
        new(store ?? fixture.Operations, fixture.Projects, fixture.Plans(owner: owner), gateway, new Context(fixture.Workspace, owner ?? fixture.Principal),
            [new ProviderScope(new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), fixture.Workspace, owner ?? fixture.Principal))], new Clock(Now), NullLogger<WorkTaskProviderService>.Instance);
    private sealed record ProviderScope(FlowRunScope? Current) : IWorkExecutionScopeAccessor;

    private sealed class TaskProviderFixture : IWorkTaskProviderGateway
    {
        private readonly ConcurrentDictionary<string, ExternalTaskProviderResponse> receipts = new(StringComparer.Ordinal);
        private readonly Guid toolUid = Guid.NewGuid();
        public int Effects => receipts.Count;
        public int Recoveries { get; private set; }
        public bool LoseResponse { get; set; }
        public bool SuppressEffect { get; set; }
        public string? ResolutionError { get; set; }
        public bool WrongCoordinate { get; set; }
        public Func<Task>? BeforeResolve { get; set; }
        public CancellationTokenSource? CancelAfterWrite { get; set; }
        public async Task<Result<WorkTaskProviderRoute>> ResolveAsync(WorkspaceId workspace, Guid principal, WorkProjectContextBinding binding, ExternalTaskOperationKind operation, CancellationToken token)
        {
            if (BeforeResolve is { } before) { BeforeResolve = null; await before(); }
            return ResolutionError is { } error ? Result<WorkTaskProviderRoute>.Failure(error) : Result<WorkTaskProviderRoute>.Success(new(binding,
                new(ScopedResourceAddress.Create(binding.Target.Address.ScopeRef, default, "Tool", "fixture.tasks"), toolUid, 1),
                new(WorkTaskProviderProtocol.Version, [ExternalTaskOperationKind.Read, ExternalTaskOperationKind.Update, ExternalTaskOperationKind.Publish], true, true)));
        }
        public Task<ExternalTaskProviderResponse> InvokeAsync(WorkTaskOperation operation, bool recover, CancellationToken token)
        {
            if (recover)
            {
                Recoveries++;
                return Task.FromResult(receipts.TryGetValue(operation.OperationKey, out var prior) ? prior
                    : new ExternalTaskProviderResponse(WrongCoordinate ? "foreign" : operation.OperationKey, operation.IntentDigest, ExternalTaskProviderStatus.Missing));
            }
            if (SuppressEffect) return Task.FromResult(new ExternalTaskProviderResponse(operation.OperationKey, operation.IntentDigest, ExternalTaskProviderStatus.Uncertain));
            var response = receipts.GetOrAdd(operation.OperationKey, _ => new(operation.OperationKey, operation.IntentDigest, ExternalTaskProviderStatus.Succeeded,
                new(operation.Input.Repository, "issue-1", "revision-1", operation.Input.Title!, operation.Input.Body, "open", new("https://fixture.test/issues/1"), [], [])));
            if (CancelAfterWrite is { } cancellation) { cancellation.Cancel(); return Task.FromCanceled<ExternalTaskProviderResponse>(cancellation.Token); }
            if (WrongCoordinate) response = response with { OperationKey = "another-operation" };
            return Task.FromResult(LoseResponse ? response with { Status = ExternalTaskProviderStatus.Uncertain, Task = null } : response);
        }
    }

    private sealed class FailingOperationReceipt(IWorkTaskOperationRepository inner) : IWorkTaskOperationRepository
    {
        public Task InitializeAsync(CancellationToken token) => inner.InitializeAsync(token);
        public Task<WorkTaskOperation?> GetAsync(WorkspaceId workspace, Guid principal, WorkTaskOperationId id, CancellationToken token) => inner.GetAsync(workspace, principal, id, token);
        public Task<WorkTaskOperationPage> ListAsync(WorkspaceId workspace, Guid principal, WorkProjectId project, int skip, int take, CancellationToken token) => inner.ListAsync(workspace, principal, project, skip, take, token);
        public Task<WorkTaskOperationSaveStatus> TryCreateAsync(WorkTaskOperationAdmission admission, CancellationToken token) => inner.TryCreateAsync(admission, token);
        public Task<bool> TryUpdateAsync(WorkTaskOperation operation, long expectedVersion, CancellationToken token) => throw new IOException("Injected receipt write failure.");
    }
}
