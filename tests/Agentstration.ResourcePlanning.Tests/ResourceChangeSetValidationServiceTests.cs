using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Sqlite;
using Agentstration.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.ResourcePlanning.Tests;

[TestClass]
public sealed class ResourceChangeSetValidationServiceTests
{
    [TestMethod]
    public async Task PersistsReadinessAgainstThePinnedChangeSet()
    {
        var scope = new ResourcePlanScope(Guid.NewGuid(), WorkspaceId.New());
        var actor = Guid.NewGuid();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var repository = new SqliteResourcePlanRepository(new Factory(new DbContextOptionsBuilder<ResourcePlanningDbContext>().UseSqlite(connection).Options));
        var contentValidator = new FunctionalResourcePlanValidator();
        var plans = new ResourcePlanService(repository, TimeProvider.System, contentValidator);
        await plans.InitializeAsync(default);
        var created = await plans.CreateAsync(scope, new("Assistant", "Create an assistant", null,
            FunctionalResourcePlanSerializer.Serialize(new()
            {
                Solution = new("Assistant", ["Answer users"]),
                Roles = [new("assistant", "Assistant", "Answer", ["Answer users"], ["Text generation"])]
            })), actor, default);
        var ready = await plans.ChangeStatusAsync(scope, created.Value.Id, new(ResourcePlanStatus.Ready), created.ETag, actor, default);
        var state = new EmptyStateReader();
        var materializer = new ResourcePlanMaterializationService(plans, contentValidator, state, new());
        var changeSets = new ResourceChangeSetService(materializer, repository, TimeProvider.System);
        var changeSet = await changeSets.CreateAsync(scope, ready.Value.Id, actor, new([new("assistant", new("model-a"), new("runtime-a"))]), default);
        var service = new ResourceChangeSetValidationService(changeSets, repository, state, [new AcceptingValidator()], TimeProvider.System);

        var result = await service.ValidateAsync(scope, changeSet.Value.Id, actor, default);

        Assert.AreEqual(ResourceChangeSetReadiness.Ready, result.Readiness);
        Assert.AreEqual(changeSet.Value.Digest, result.ChangeSetDigest);
        Assert.AreEqual(ready.Value.Revision, result.PlanRevision);
        Assert.AreEqual(ResourceChangeSetStatus.Validated, (await changeSets.GetAsync(scope, changeSet.Value.Id, default)).Value.Status);
        Assert.AreEqual(1, (await service.ListAsync(scope, changeSet.Value.Id, default)).Count);
        state.BindingChanged = true;
        var stale = await service.ValidateAsync(scope, changeSet.Value.Id, actor, default);
        Assert.AreEqual(ResourceChangeSetReadiness.Blocked, stale.Readiness);
        Assert.IsTrue(stale.Issues.Any(value => value.Code == "resource_change_binding_stale"));
    }

    private sealed class EmptyStateReader : IResourcePlanningStateReader
    {
        public bool BindingChanged { get; set; }
        public Task<CurrentResourceEvidence?> GetAsync(PlannedResourceDocument resource, CancellationToken cancellationToken) => Task.FromResult<CurrentResourceEvidence?>(null);
        public Task<CurrentResourceEvidence?> ResolveBindingAsync(ResourcePlanScope scope, string kind, ResourceReference reference, CancellationToken cancellationToken) =>
            Task.FromResult<CurrentResourceEvidence?>(new(Guid.Empty, 1, "\"profile\"", System.Text.Json.JsonSerializer.SerializeToElement(new { kind, reference.Name }), $"{kind}:{reference.Name}{(BindingChanged ? ":changed" : string.Empty)}"));
    }

    private sealed class AcceptingValidator : IPlannedResourceValidator
    {
        public bool Supports(string kind) => true;
        public Task<IReadOnlyList<ResourceChangeSetValidationIssue>> ValidateAsync(ResourceChange change, ResourcePlanScope scope, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceChangeSetValidationIssue>>([]);
    }

    private sealed class Factory(DbContextOptions<ResourcePlanningDbContext> options) : IDbContextFactory<ResourcePlanningDbContext>
    {
        public ResourcePlanningDbContext CreateDbContext() => new(options);
    }
}
