using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Sqlite;
using Agentstration.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.ResourcePlanning.Tests;

[TestClass]
public sealed class ResourceChangeSetServiceTests
{
    [TestMethod]
    public async Task PersistsAnIdempotentRevisionPinnedReviewBoundary()
    {
        var scope = new ResourcePlanScope(Guid.NewGuid(), WorkspaceId.New());
        var actor = Guid.NewGuid();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var repository = new SqliteResourcePlanRepository(new Factory(new DbContextOptionsBuilder<ResourcePlanningDbContext>().UseSqlite(connection).Options));
        var validator = new FunctionalResourcePlanValidator();
        var plans = new ResourcePlanService(repository, TimeProvider.System, validator);
        await plans.InitializeAsync(default);
        var created = await plans.CreateAsync(scope, new("Assistant", "Create an assistant", null,
            FunctionalResourcePlanSerializer.Serialize(new()
            {
                Solution = new("Assistant", ["Answer users"]),
                Roles = [new("assistant", "Assistant", "Answer questions", ["Answer"], ["Text generation"])]
            })), actor, default);
        var ready = await plans.ChangeStatusAsync(scope, created.Value.Id, new(ResourcePlanStatus.Ready), created.ETag, actor, default);
        var materializer = new ResourcePlanMaterializationService(plans, validator, new EmptyStateReader(), new());
        var service = new ResourceChangeSetService(materializer, repository, TimeProvider.System);
        var first = await service.CreateAsync(scope, ready.Value.Id, actor, default);
        var replay = await service.CreateAsync(scope, ready.Value.Id, actor, default);
        Assert.AreEqual(first.Value.Id, replay.Value.Id);
        Assert.AreEqual(first.Value.Digest, replay.Value.Digest);
        Assert.AreEqual(ready.Value.Revision, first.Value.PlanRevision);
        Assert.AreEqual(ResourceChangeOperation.Create, first.Value.Changes.Single().Operation);
        Assert.AreEqual(0, first.Value.Changes.Single().Order);
        Assert.IsNull(await repository.GetAsync(scope with { WorkspaceId = WorkspaceId.New() }, first.Value.Id, default));
    }

    private sealed class EmptyStateReader : IResourcePlanningStateReader
    {
        public Task<CurrentResourceEvidence?> GetAsync(PlannedResourceDocument resource, CancellationToken cancellationToken) => Task.FromResult<CurrentResourceEvidence?>(null);
    }

    private sealed class Factory(DbContextOptions<ResourcePlanningDbContext> options) : IDbContextFactory<ResourcePlanningDbContext>
    {
        public ResourcePlanningDbContext CreateDbContext() => new(options);
    }
}
