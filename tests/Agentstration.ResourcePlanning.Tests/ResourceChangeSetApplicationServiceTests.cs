using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Agentstration.ResourceManagement;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Sqlite;
using Agentstration.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.ResourcePlanning.Tests;

[TestClass]
public sealed class ResourceChangeSetApplicationServiceTests
{
    [TestMethod]
    public async Task AppliesInOrderAndReplaysWithoutWritingAgain()
    {
        var fixture = await Fixture.CreateAsync();
        await using var connection = fixture.Connection;
        var request = fixture.Request;

        var applied = await fixture.Applications.ApplyAsync(fixture.Scope, fixture.ChangeSet.Value.Id, request, fixture.Actor, default);
        var replay = await fixture.Applications.ApplyAsync(fixture.Scope, fixture.ChangeSet.Value.Id, request, fixture.Actor, default);

        Assert.AreEqual(ResourceChangeSetApplicationStatus.Applied, applied.Value.Status);
        Assert.AreEqual(applied.Value.Id, replay.Value.Id);
        Assert.AreEqual(2, fixture.Applier.Calls);
        CollectionAssert.AreEqual(fixture.ChangeSet.Value.Changes.Select(value => value.LogicalId).ToArray(), applied.Value.Operations.Select(value => value.LogicalId).ToArray());
        Assert.AreEqual(ResourcePlanStatus.Applied, (await fixture.Plans.GetAsync(fixture.Scope, fixture.ChangeSet.Value.PlanId, default))!.Value.Status);
    }

    [TestMethod]
    public async Task StopsAfterFailureAndResumesOnlyTheFailedOperation()
    {
        var fixture = await Fixture.CreateAsync();
        await using var connection = fixture.Connection;
        fixture.Applier.FailOn = fixture.ChangeSet.Value.Changes[1].LogicalId;

        var first = await fixture.Applications.ApplyAsync(fixture.Scope, fixture.ChangeSet.Value.Id, fixture.Request, fixture.Actor, default);
        Assert.AreEqual(ResourceChangeSetApplicationStatus.PartiallyApplied, first.Value.Status);
        Assert.AreEqual(ResourceChangeApplicationOutcome.Applied, first.Value.Operations[0].Outcome);
        Assert.AreEqual(ResourceChangeApplicationOutcome.Failed, first.Value.Operations[1].Outcome);

        fixture.Applier.FailOn = null;
        var resumed = await fixture.Applications.ApplyAsync(fixture.Scope, fixture.ChangeSet.Value.Id, fixture.Request, fixture.Actor, default);
        Assert.AreEqual(ResourceChangeSetApplicationStatus.Applied, resumed.Value.Status);
        Assert.AreEqual(2, resumed.Value.Attempts);
        Assert.AreEqual(2, resumed.Value.AttemptHistory!.Count);
        Assert.IsTrue(resumed.Value.AttemptHistory.All(value => value.CompletedAt is not null));
        Assert.AreEqual(3, fixture.Applier.Calls);
    }

    [TestMethod]
    public async Task RejectsMismatchedVerificationBeforeWriting()
    {
        var fixture = await Fixture.CreateAsync();
        await using var connection = fixture.Connection;
        var request = fixture.Request with { ValidationId = Guid.NewGuid() };

        var exception = await Assert.ThrowsExactlyAsync<ResourceChangeSetApplicationException>(() =>
            fixture.Applications.ApplyAsync(fixture.Scope, fixture.ChangeSet.Value.Id, request, fixture.Actor, default));
        Assert.AreEqual("resource_change_set_validation_stale", exception.Code);
        Assert.AreEqual(0, fixture.Applier.Calls);
    }

    [TestMethod]
    public async Task ResumesAFlowCreatedBeforePublishing()
    {
        var fixture = await Fixture.CreateAsync(includeFlow: true);
        await using var connection = fixture.Connection;
        fixture.Applier.FailAfterDraftOn = "support";

        var first = await fixture.Applications.ApplyAsync(fixture.Scope, fixture.ChangeSet.Value.Id, fixture.Request, fixture.Actor, default);
        Assert.AreEqual(ResourceChangeSetApplicationStatus.PartiallyApplied, first.Value.Status);
        fixture.Applier.FailAfterDraftOn = null;

        var resumed = await fixture.Applications.ApplyAsync(fixture.Scope, fixture.ChangeSet.Value.Id, fixture.Request, fixture.Actor, default);
        Assert.AreEqual(ResourceChangeSetApplicationStatus.Applied, resumed.Value.Status);
        Assert.IsTrue(fixture.Applier.ResumedFlow);
        Assert.AreEqual(4, fixture.Applier.Calls);
    }

    [TestMethod]
    public async Task RejectsAResourceCreatedAfterVerification()
    {
        var fixture = await Fixture.CreateAsync();
        await using var connection = fixture.Connection;
        fixture.State.Apply(fixture.ChangeSet.Value.Changes[0]);

        var result = await fixture.Applications.ApplyAsync(fixture.Scope, fixture.ChangeSet.Value.Id, fixture.Request, fixture.Actor, default);

        Assert.AreEqual(ResourceChangeSetApplicationStatus.Failed, result.Value.Status);
        Assert.AreEqual("resource_change_stale", result.Value.Operations[0].ErrorCode);
        Assert.AreEqual(0, fixture.Applier.Calls);
    }

    [TestMethod]
    public async Task ApplicationIsHiddenFromAnotherWorkspace()
    {
        var fixture = await Fixture.CreateAsync();
        await using var connection = fixture.Connection;
        _ = await fixture.Applications.ApplyAsync(fixture.Scope, fixture.ChangeSet.Value.Id, fixture.Request, fixture.Actor, default);

        var otherScope = fixture.Scope with { WorkspaceId = WorkspaceId.New() };
        Assert.IsNull(await fixture.Applications.GetAsync(otherScope, fixture.ChangeSet.Value.Id, default));
    }

    private sealed class Fixture
    {
        public required SqliteConnection Connection { get; init; }
        public required ResourcePlanScope Scope { get; init; }
        public required Guid Actor { get; init; }
        public required ResourcePlanService Plans { get; init; }
        public required ResourceChangeSetSnapshot ChangeSet { get; init; }
        public required ApplyResourceChangeSetRequest Request { get; init; }
        public required RecordingApplier Applier { get; init; }
        public required MutableStateReader State { get; init; }
        public required ResourceChangeSetApplicationService Applications { get; init; }

        public static async Task<Fixture> CreateAsync(bool includeFlow = false)
        {
            var scope = new ResourcePlanScope(Guid.NewGuid(), WorkspaceId.New());
            var actor = Guid.NewGuid();
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var repository = new SqliteResourcePlanRepository(new Factory(new DbContextOptionsBuilder<ResourcePlanningDbContext>().UseSqlite(connection).Options));
            var validator = new FunctionalResourcePlanValidator();
            var plans = new ResourcePlanService(repository, TimeProvider.System, validator);
            await plans.InitializeAsync(default);
            var created = await plans.CreateAsync(scope, new("Support", "Support requests", null,
                FunctionalResourcePlanSerializer.Serialize(new()
                {
                    Solution = new("Support", ["Answer requests"]),
                    Roles = [new("triage", "Triage", "Classify", ["Classify"], ["Text analysis"]),
                        new("resolution", "Resolution", "Answer", ["Answer"], ["Text generation"])],
                    Workflows = includeFlow ? [new("support", "Support", "Resolve", ["triage", "resolution"], PlanningCollaborationStyle.Ordered)] : []
                })), actor, default);
            var ready = await plans.ChangeStatusAsync(scope, created.Value.Id, new(ResourcePlanStatus.Ready), created.ETag, actor, default);
            var state = new MutableStateReader();
            var materializer = new ResourcePlanMaterializationService(plans, validator, state, new());
            var changeSets = new ResourceChangeSetService(materializer, repository, TimeProvider.System);
            var bindings = new ResourcePlanMaterializationRequest([
                new("triage", new("model-a"), new("runtime-a")),
                new("resolution", new("model-a"), new("runtime-a"))]);
            var set = await changeSets.CreateAsync(scope, ready.Value.Id, actor, bindings, default);
            var validation = await new ResourceChangeSetValidationService(changeSets, repository, state, [new AcceptingValidator()], TimeProvider.System)
                .ValidateAsync(scope, set.Value.Id, actor, default);
            var applier = new RecordingApplier(state);
            return new()
            {
                Connection = connection, Scope = scope, Actor = actor, Plans = plans, ChangeSet = set, State = state,
                Request = new(ready.Value.Id, ready.Value.Revision, set.Value.Digest, validation.Id), Applier = applier,
                Applications = new(plans, changeSets, repository, state, new AllowingScopes(), [new AcceptingValidator()], [applier], TimeProvider.System,
                    NullLogger<ResourceChangeSetApplicationService>.Instance)
            };
        }
    }

    private sealed class MutableStateReader : IResourcePlanningStateReader
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, CurrentResourceEvidence> values = [];
        public Task<CurrentResourceEvidence?> GetAsync(PlannedResourceDocument resource, CancellationToken cancellationToken) =>
            Task.FromResult(values.GetValueOrDefault(resource.Metadata.Name));
        public Task<CurrentResourceEvidence?> ResolveBindingAsync(ResourcePlanScope scope, string kind, ResourceReference reference, CancellationToken cancellationToken) =>
            Task.FromResult<CurrentResourceEvidence?>(new(Guid.Empty, 1, "\"profile\"", JsonSerializer.SerializeToElement(new { kind, reference.Name }), $"{kind}:{reference.Name}"));
        public void Apply(ResourceChange change) => values[change.Proposed.Metadata.Name] = new(Guid.NewGuid(), 1, "\"applied\"", change.Proposed.Definition, change.ProposedDigest);
        public void ApplyDraft(ResourceChange change)
        {
            var definition = JsonNode.Parse(change.Proposed.Definition.GetRawText())!.AsObject();
            definition["publish"] = false;
            definition["activate"] = false;
            var intermediate = change.Proposed with { Definition = JsonSerializer.SerializeToElement(definition) };
            var digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(intermediate, JsonOptions))))}";
            values[change.Proposed.Metadata.Name] = new(Guid.NewGuid(), 1, "\"draft\"", intermediate.Definition, digest);
        }
    }

    private sealed class RecordingApplier(MutableStateReader state) : IPlannedResourceApplier
    {
        public int Calls { get; private set; }
        public string? FailOn { get; set; }
        public string? FailAfterDraftOn { get; set; }
        public bool ResumedFlow { get; private set; }
        public bool Supports(string kind) => true;
        public Task ApplyAsync(ResourceChange change, bool resume, CancellationToken cancellationToken)
        {
            Calls++;
            if (change.LogicalId == FailAfterDraftOn) { state.ApplyDraft(change); throw new InvalidOperationException("publish failed"); }
            if (change.LogicalId == FailOn) throw new InvalidOperationException("simulated failure");
            if (change.LogicalId == "support" && resume) ResumedFlow = true;
            state.Apply(change);
            return Task.CompletedTask;
        }
    }

    private sealed class AcceptingValidator : IPlannedResourceValidator
    {
        public bool Supports(string kind) => true;
        public Task<IReadOnlyList<ResourceChangeSetValidationIssue>> ValidateAsync(ResourceChange change, ResourcePlanScope scope, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceChangeSetValidationIssue>>([]);
    }

    private sealed class AllowingScopes : IResourceScopeOperations
    {
        public ResourceScopeRef DefaultScopeRef(string kind) => throw new NotSupportedException();
        public ResourceScopeRef TargetScopeRef(ResourceScopeKind kind) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTarget>> ListTargetsAsync(string kind, string permission, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<T> WriteAsync<T>(Resource resource, ResourceScopeRef scopeRef, string permission, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
        public Task<T> WriteAsync<T>(string kind, ResourceScopeRef scopeRef, string permission, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }

    private sealed class Factory(DbContextOptions<ResourcePlanningDbContext> options) : IDbContextFactory<ResourcePlanningDbContext>
    {
        public ResourcePlanningDbContext CreateDbContext() => new(options);
    }
}
