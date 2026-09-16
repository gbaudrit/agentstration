using Agentstration.Models;
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
    private static ResourcePlanMaterializationRequest Bindings(string role) => new([new(role, new("model-a"), new("runtime-a"))]);

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
        var first = await service.MaterializeAsync(Scope, ready.Value.Id, Bindings("triage"), default);
        var second = await service.MaterializeAsync(Scope, ready.Value.Id, Bindings("triage"), default);
        Assert.AreEqual(first.Digest, second.Digest);
        CollectionAssert.AreEqual(new[] { "triage", "support", "support-chat" }, first.Proposals.Select(value => value.LogicalId).ToArray());
        Assert.IsTrue(first.Proposals.All(value => value.Operation == ResourcePlanProposedOperation.Create));
        Assert.AreEqual(6, reader.Reads);
        Assert.IsTrue(first.CanCreateChangeSet);
        Assert.HasCount(2, first.ResolvedBindings!);
    }

    [TestMethod]
    public async Task ExistingEquivalentResourceBecomesNoOp()
    {
        var (connection, plans, validator, ready) = await CreateReadyPlanAsync();
        await using var ownedConnection = connection;
        var empty = new RecordingStateReader();
        var service = new ResourcePlanMaterializationService(plans, validator, empty, new());
        var initial = await service.MaterializeAsync(Scope, ready.Value.Id, Bindings("assistant"), default);
        var agent = initial.Proposals.Single();
        var reader = new RecordingStateReader(new CurrentResourceEvidence(Guid.NewGuid(), 4, "\"etag\"", agent.Resource.Definition, agent.ProposedDigest));
        var repeated = await new ResourcePlanMaterializationService(plans, validator, reader, new()).MaterializeAsync(Scope, ready.Value.Id, Bindings("assistant"), default);
        Assert.AreEqual(ResourcePlanProposedOperation.NoOp, repeated.Proposals.Single().Operation);
    }

    [TestMethod]
    public async Task MissingOrInvisibleBindingBlocksChangeSetAndSelectionChangesDigest()
    {
        var (connection, plans, validator, ready) = await CreateReadyPlanAsync();
        await using var ownedConnection = connection;
        var reader = new RecordingStateReader();
        var service = new ResourcePlanMaterializationService(plans, validator, reader, new());
        var missing = await service.MaterializeAsync(Scope, ready.Value.Id, default);
        Assert.IsFalse(missing.CanCreateChangeSet);
        Assert.IsTrue(missing.Diagnostics.Any(value => value.Code == "planning_binding_required"));
        var selected = await service.MaterializeAsync(Scope, ready.Value.Id, Bindings("assistant"), default);
        Assert.IsTrue(selected.CanCreateChangeSet);
        Assert.AreEqual(ResourceNamespace.Default, selected.ResolvedBindings!.Single(value => value.Field == "modelProfile").Reference.Namespace);
        Assert.AreNotEqual(selected.Digest, (await service.MaterializeAsync(Scope, ready.Value.Id,
            new([new("assistant", new("model-b"), new("runtime-a"))]), default)).Digest);
        reader.MissingKind = "ModelProfile";
        var invisible = await service.MaterializeAsync(Scope, ready.Value.Id, Bindings("assistant"), default);
        Assert.IsFalse(invisible.CanCreateChangeSet);
        Assert.IsTrue(invisible.Diagnostics.Any(value => value.Code == "planning_binding_not_found"));
        reader.MissingKind = null;
        reader.IncompatibleKind = "ModelProfile";
        var incompatible = await service.MaterializeAsync(Scope, ready.Value.Id, Bindings("assistant"), default);
        Assert.IsTrue(incompatible.Diagnostics.Any(value => value.Code == "planning_binding_incompatible"));
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
        public string? MissingKind { get; set; }
        public string? IncompatibleKind { get; set; }
        public Task<CurrentResourceEvidence?> GetAsync(PlannedResourceDocument resource, CancellationToken cancellationToken) { Reads++; return Task.FromResult(current); }
        public Task<CurrentResourceEvidence?> ResolveBindingAsync(ResourcePlanScope scope, string kind, ResourceReference reference, CancellationToken cancellationToken)
        {
            if (kind == IncompatibleKind) throw new ModelProfileValidationException("model_profile_invalid", "Invalid profile configuration.");
            return Task.FromResult<CurrentResourceEvidence?>(kind == MissingKind ? null : new(Guid.Parse("44444444-4444-4444-4444-444444444444"), 1, "\"binding\"", System.Text.Json.JsonSerializer.SerializeToElement(new { kind, reference.Name, scope.WorkspaceId }), $"{kind}:{reference.Name}"));
        }
    }

    private sealed class Factory(DbContextOptions<ResourcePlanningDbContext> options) : IDbContextFactory<ResourcePlanningDbContext>
    {
        public ResourcePlanningDbContext CreateDbContext() => new(options);
    }
}
