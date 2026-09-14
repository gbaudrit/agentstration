using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Work.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class RootFlowSubmissionTests
{
    private static readonly FlowRunScope Scope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        Guid.Parse("33333333-3333-3333-3333-333333333333"));

    [TestMethod]
    public async Task RepeatedSubmissionRecoversOneWorkItemAndOneRootRun()
    {
        await using var fixture = await Fixture.CreateAsync();
        var command = Command("daily-news", JsonSerializer.SerializeToElement(new { topic = "ai" }));

        var first = await fixture.Submissions.SubmitAsync(command, default);
        var second = await fixture.Submissions.SubmitAsync(command, default);

        Assert.AreEqual(first.WorkItem.Value.Id, second.WorkItem.Value.Id);
        Assert.AreEqual(first.FlowRun.Run.Id, second.FlowRun.Run.Id);
        Assert.IsFalse(first.Recovered);
        Assert.IsTrue(second.Recovered);
        Assert.HasCount(1, (await fixture.Repository.QueryAsync(new WorkItemQuery(Scope.WorkspaceId), default)).Items);
        Assert.HasCount(1, fixture.Runs.RunIds.Distinct(StringComparer.Ordinal));
        Assert.IsNull(first.FlowRun.Run.ParentFlowRunId);
        Assert.IsNull(first.FlowRun.Run.RootFlowRunId);
        Assert.AreEqual(0, first.FlowRun.Run.NestingDepth);
        Assert.AreEqual(FlowInvocationOrigin.Api, first.FlowRun.Run.InvocationOrigin);
        Assert.AreEqual("api-user", first.FlowRun.Run.CallerId);
        Assert.AreEqual("request-42", first.FlowRun.Run.CausationId);
        Assert.AreEqual(first.WorkItem.Value.Id.Value.ToString("D"), first.FlowRun.Run.WorkItemResourceId);
    }

    [TestMethod]
    public async Task ReusingIdempotencyKeyWithDifferentInputIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Submissions.SubmitAsync(Command("same", JsonSerializer.SerializeToElement(new { value = 1 })), default);

        var exception = await Assert.ThrowsExactlyAsync<WorkValidationException>(() =>
            fixture.Submissions.SubmitAsync(Command("same", JsonSerializer.SerializeToElement(new { value = 2 })), default));

        Assert.AreEqual("flow_invocation_idempotency_conflict", exception.Code);
    }

    [TestMethod]
    public async Task SubmissionResolvesActiveTargetToImmutableVersionAndPersistsCausality()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Submissions.SubmitAsync(Command("immutable", JsonSerializer.SerializeToElement(new { })), default);

        Assert.IsFalse(result.WorkItem.Value.Flow!.UseActiveVersion);
        Assert.AreEqual("2.1.0", result.WorkItem.Value.Flow.Version);
        Assert.AreEqual("Api", result.WorkItem.Value.Metadata[RootFlowSubmissionService.OriginMetadata]);
        Assert.AreEqual("api-user", result.WorkItem.Value.Metadata[RootFlowSubmissionService.CallerMetadata]);
        Assert.AreEqual("request-42", result.WorkItem.Value.Metadata[RootFlowSubmissionService.CausationMetadata]);
        Assert.AreEqual("immutable", result.WorkItem.Value.Metadata[RootFlowSubmissionService.IdempotencyMetadata]);
        Assert.AreEqual(1, fixture.Targets.ResolutionCount);
        Assert.AreEqual(2, result.WorkItem.Value.Inputs.Count);
        Assert.AreEqual(JsonValueKind.Object, result.WorkItem.Value.Inputs[0].Structured!.Value.ValueKind);
    }

    [TestMethod]
    public async Task TriggerCanPreserveItsOccurrenceWorkItemIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var occurrenceId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var command = Command("occurrence", JsonSerializer.SerializeToElement(new { })) with
        {
            Origin = FlowInvocationOrigin.Trigger,
            Trigger = FlowRunTrigger.Schedule,
            WorkItemId = new WorkItemId(occurrenceId)
        };

        var result = await fixture.Submissions.SubmitAsync(command, default);

        Assert.AreEqual(occurrenceId, result.WorkItem.Value.Id.Value);
        Assert.AreEqual($"flowrun-root-{occurrenceId:N}", result.FlowRun.Run.Id);
    }

    [TestMethod]
    public async Task ScopeIsAuthorizedAndCannotBeSuppliedThroughInvocationInput()
    {
        await using var fixture = await Fixture.CreateAsync();
        var otherWorkspace = new WorkspaceId(Guid.NewGuid());

        var exception = await Assert.ThrowsExactlyAsync<WorkValidationException>(() => fixture.Submissions.SubmitAsync(
            Command("wrong-scope", JsonSerializer.SerializeToElement(new { tenantId = Guid.NewGuid(), workspaceId = otherWorkspace.Value })) with
            {
                WorkspaceId = otherWorkspace
            }, default));

        Assert.AreEqual("flow_invocation_scope_mismatch", exception.Code);
        Assert.AreEqual(0, fixture.Authorizer.CallCount);
    }

    private static SubmitRootFlowCommand Command(string key, JsonElement input) => new(
        Scope.WorkspaceId,
        new FlowReference(new FlowId("news")),
        input,
        FlowInvocationOrigin.Api,
        "api-user",
        FlowRunTrigger.Api,
        key,
        "request-42",
        "correlation-42",
        WorkInputs: [new WorkInput("visible request")]);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory;
        private readonly ServiceProvider provider;

        private Fixture(string directory, ServiceProvider provider, IWorkItemRepository repository, RootFlowSubmissionService submissions, TargetResolver targets, RunGateway runs, Authorizer authorizer)
        {
            this.directory = directory;
            this.provider = provider;
            Repository = repository;
            Submissions = submissions;
            Targets = targets;
            Runs = runs;
            Authorizer = authorizer;
        }

        public IWorkItemRepository Repository { get; }
        public RootFlowSubmissionService Submissions { get; }
        public TargetResolver Targets { get; }
        public RunGateway Runs { get; }
        public Authorizer Authorizer { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"agentstration-root-flow-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var services = new ServiceCollection();
            services.AddSqliteWorkPlane($"Data Source={Path.Combine(directory, "work.db")};Pooling=False");
            var provider = services.BuildServiceProvider();
            var repository = provider.GetRequiredService<IWorkItemRepository>();
            await repository.InitializeAsync(default);
            var execution = new ExecutionGateway();
            var accessor = new ScopeAccessor();
            var work = new WorkItemService(repository, execution, TimeProvider.System, NullLogger<WorkItemService>.Instance, [], [accessor]);
            var targets = new TargetResolver();
            var runs = new RunGateway();
            var authorizer = new Authorizer();
            var submissions = new RootFlowSubmissionService(work, repository, targets, runs, authorizer, [accessor]);
            return new(directory, provider, repository, submissions, targets, runs, authorizer);
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class ScopeAccessor : IWorkExecutionScopeAccessor
    {
        public FlowRunScope Current => Scope;
    }

    private sealed class Authorizer : IRootFlowSubmissionAuthorizer
    {
        public int CallCount { get; private set; }
        public Task AuthorizeAsync(FlowRunScope scope, CancellationToken cancellationToken)
        {
            CallCount++;
            Assert.AreEqual(Scope, scope);
            return Task.CompletedTask;
        }
    }

    private sealed class TargetResolver : IRootFlowTargetResolver
    {
        public int ResolutionCount { get; private set; }
        public Task<ResolvedRootFlowTarget> ResolveAsync(FlowRunScope scope, FlowReference target, JsonElement input, CancellationToken cancellationToken)
        {
            ResolutionCount++;
            return Task.FromResult(new ResolvedRootFlowTarget(new FlowReference(target.FlowId, "2.1.0", false, target.FlowId.Namespace)));
        }
    }

    private sealed class RunGateway : IRootFlowRunGateway
    {
        private readonly Dictionary<string, RootFlowRunResult> runs = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> RunIds => runs.Keys;

        public Task<RootFlowRunResult> EnsureAsync(RootFlowRunRequest request, CancellationToken cancellationToken)
        {
            if (!runs.TryGetValue(request.RunId, out var result))
            {
                var version = new FlowVersion(request.Scope.WorkspaceId, request.Target.FlowId, request.Target.Version!, null,
                    new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant")), new Dictionary<string, string>(), DateTimeOffset.UtcNow);
                var run = new FlowRun
                {
                    WorkspaceId = request.Scope.WorkspaceId,
                    Id = request.RunId,
                    FlowId = request.Target.FlowId,
                    FlowVersion = request.Target.Version!,
                    Trigger = request.Trigger,
                    InvocationOrigin = request.Origin,
                    CallerId = request.CallerId,
                    CausationId = request.CausationId,
                    IdempotencyKey = request.IdempotencyKey,
                    CorrelationId = request.CorrelationId,
                    WorkItemResourceId = request.WorkItemId.Value.ToString("D"),
                    ParentFlowRunId = request.ParentFlowRunId,
                    Scope = request.Scope,
                    Input = request.Input.Clone(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    DefinitionSnapshot = version
                };
                result = new(run, "\"1\"");
                runs.Add(request.RunId, result);
            }
            return Task.FromResult(result);
        }
    }

    private sealed class ExecutionGateway : IWorkExecutionGateway
    {
        public Task<WorkExecutionAccepted> RequestExecutionAsync(WorkExecutionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkExecutionAccepted(WorkExecutionId.New(), null, DateTimeOffset.UtcNow, Guid.NewGuid()));

        public Task ConfirmQueuedAsync(WorkExecutionAccepted accepted, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
