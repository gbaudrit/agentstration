using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Sqlite;
using Agentstration.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.ResourcePlanning.Tests;

[TestClass]
public sealed class ResourcePlanMaterializationServiceTests
{
    private static readonly ResourcePlanScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222")));
    private static readonly Guid Actor = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [TestMethod]
    public async Task MaterializationIsDeterministicOrderedAndDoesNotMutateState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var repository = new SqliteResourcePlanRepository(new Factory(new DbContextOptionsBuilder<ResourcePlanningDbContext>().UseSqlite(connection).Options));
        var validator = new FunctionalResourcePlanValidator();
        var plans = new ResourcePlanService(repository, TimeProvider.System, validator);
        await plans.InitializeAsync(default);
        var created = await plans.CreateAsync(Scope, new(
            "Support solution",
            "Create a coordinated support experience",
            null,
            FunctionalResourcePlanSerializer.Serialize(new()
            {
                Solution = new("Coordinate support", ["Resolve requests"]),
                Roles = [new("triage", "Triage", "Classify", ["Classify requests"], ["Text analysis"])],
                Workflows = [new("support", "Support", "Resolve", ["triage"], PlanningCollaborationStyle.Ordered)],
                Experiences = [new("support-chat", "Support chat", "Help users", "support", ["employees"])]
            })), Actor, default);
        var ready = await plans.ChangeStatusAsync(Scope, created.Value.Id, new(ResourcePlanStatus.Ready), created.ETag, Actor, default);
        var reader = new RecordingStateReader();
        var service = new ResourcePlanMaterializationService(plans, validator, reader, new());
        var first = await service.MaterializeAsync(Scope, ready.Value.Id, default);
        var second = await service.MaterializeAsync(Scope, ready.Value.Id, default);
        Assert.AreEqual(first.Digest, second.Digest);
        CollectionAssert.AreEqual(new[] { "triage", "support", "support-chat" }, first.Proposals.Select(value => value.LogicalId).ToArray());
        Assert.IsTrue(first.Proposals.All(value => value.Operation == ResourcePlanProposedOperation.Create));
        Assert.AreEqual(6, reader.Reads);
        Assert.IsTrue(first.CanCreateChangeSet);
    }

    [TestMethod]
    public async Task ExistingEquivalentResourceBecomesNoOp()
    {
        var (connection, plans, validator, ready) = await CreateReadyPlanAsync();
        await using var ownedConnection = connection;
        var empty = new RecordingStateReader();
        var service = new ResourcePlanMaterializationService(plans, validator, empty, new());
        var initial = await service.MaterializeAsync(Scope, ready.Value.Id, default);
        var agent = initial.Proposals.Single();
        var reader = new RecordingStateReader(new CurrentResourceEvidence(Guid.NewGuid(), 4, "\"etag\"", agent.Resource.Definition, agent.ProposedDigest));
        var repeated = await new ResourcePlanMaterializationService(plans, validator, reader, new()).MaterializeAsync(Scope, ready.Value.Id, default);
        Assert.AreEqual(ResourcePlanProposedOperation.NoOp, repeated.Proposals.Single().Operation);
    }

    private static async Task<(SqliteConnection Connection, ResourcePlanService Service, FunctionalResourcePlanValidator Validator, ResourcePlanSnapshot Ready)> CreateReadyPlanAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var repository = new SqliteResourcePlanRepository(new Factory(new DbContextOptionsBuilder<ResourcePlanningDbContext>().UseSqlite(connection).Options));
        var validator = new FunctionalResourcePlanValidator();
        var plans = new ResourcePlanService(repository, TimeProvider.System, validator);
        await plans.InitializeAsync(default);
        var created = await plans.CreateAsync(Scope, new("Agent", "Create one agent", null,
            FunctionalResourcePlanSerializer.Serialize(new()
            {
                Solution = new("One agent", ["Answer users"]),
                Roles = [new("assistant", "Assistant", "Answer", ["Answer users"], ["Text generation"])]
            })), Actor, default);
        var ready = await plans.ChangeStatusAsync(Scope, created.Value.Id, new(ResourcePlanStatus.Ready), created.ETag, Actor, default);
        return (connection, plans, validator, ready);
    }

    private sealed class RecordingStateReader(CurrentResourceEvidence? current = null) : IResourcePlanningStateReader
    {
        public int Reads { get; private set; }
        public Task<CurrentResourceEvidence?> GetAsync(PlannedResourceDocument resource, CancellationToken cancellationToken) { Reads++; return Task.FromResult(current); }
    }

    private sealed class Factory(DbContextOptions<ResourcePlanningDbContext> options) : IDbContextFactory<ResourcePlanningDbContext>
    {
        public ResourcePlanningDbContext CreateDbContext() => new(options);
    }
}
