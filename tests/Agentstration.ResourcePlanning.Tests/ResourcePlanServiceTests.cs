using System.Text.Json;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Abstractions;
using Agentstration.ResourcePlanning.Storage.Sqlite;
using Agentstration.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.ResourcePlanning.Tests;

[TestClass]
public sealed class ResourcePlanServiceTests
{
    private static readonly ResourcePlanScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222")));
    private static readonly Guid Actor = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [TestMethod]
    public async Task CreateAndRefineRetainScopeProvenanceAndHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ResourcePlanningDbContext>().UseSqlite(connection).Options;
        var repository = new SqliteResourcePlanRepository(new TestDbContextFactory(options));
        var service = new ResourcePlanService(repository, TimeProvider.System);
        await service.InitializeAsync(default);
        var created = await service.CreateAsync(Scope, Create("Initial", "work-1", "flow-1"), Actor, default);
        Assert.AreEqual(ResourcePlanStatus.Draft, created.Value.Status);
        Assert.AreEqual(1, created.Value.Revision);
        Assert.AreEqual(Scope, created.Value.Scope);
        Assert.AreEqual("work-1", created.Value.Origin.WorkItemId);
        var refined = await service.RefineAsync(Scope, created.Value.Id,
            new("Refined", "Create a coordinated assistant", null, Content("refined")), created.ETag, Actor, default);
        Assert.AreEqual(2, refined.Value.Revision);
        Assert.AreEqual("Refined", refined.Value.Title);
        Assert.AreNotEqual(created.ETag, refined.ETag);
        var activities = await service.ListActivitiesAsync(Scope, created.Value.Id, default);
        CollectionAssert.AreEqual(new[] { ResourcePlanActivityType.Created, ResourcePlanActivityType.Refined }, activities.Select(value => value.Type).ToArray());
    }

    [TestMethod]
    public async Task RepositoryRejectsCrossWorkspaceReadsAndStaleWrites()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ResourcePlanningDbContext>().UseSqlite(connection).Options;
        var repository = new SqliteResourcePlanRepository(new TestDbContextFactory(options));
        var service = new ResourcePlanService(repository, TimeProvider.System);
        await service.InitializeAsync(default);
        var created = await service.CreateAsync(Scope, Create("Plan"), Actor, default);
        var foreign = Scope with { WorkspaceId = new WorkspaceId(Guid.NewGuid()) };
        Assert.IsNull(await service.GetAsync(foreign, created.Value.Id, default));
        await Assert.ThrowsAsync<ResourcePlanConcurrencyException>(() => service.RefineAsync(
            Scope, created.Value.Id, new("Plan", "Changed", null, Content("changed")), "\"stale\"", Actor, default));
    }

    [TestMethod]
    public async Task AppliedPlanCanOnlyBeArchived()
    {
        var repository = new MemoryRepository();
        var service = new ResourcePlanService(repository, TimeProvider.System);
        var current = await service.CreateAsync(Scope, Create("Plan"), Actor, default);
        foreach (var status in new[] { ResourcePlanStatus.Ready, ResourcePlanStatus.Materialized, ResourcePlanStatus.Validated, ResourcePlanStatus.Applied })
            current = await service.ChangeStatusAsync(Scope, current.Value.Id, new(status), current.ETag, Actor, default);
        await Assert.ThrowsAsync<ResourcePlanLifecycleException>(() => service.ChangeStatusAsync(
            Scope, current.Value.Id, new(ResourcePlanStatus.Draft), current.ETag, Actor, default));
        var archived = await service.ChangeStatusAsync(Scope, current.Value.Id, new(ResourcePlanStatus.Archived), current.ETag, Actor, default);
        Assert.AreEqual(ResourcePlanStatus.Archived, archived.Value.Status);
    }

    private static CreateResourcePlanRequest Create(string title, string? workItem = null, string? flowRun = null) =>
        new(title, "Create a coordinated assistant", null, Content("initial"), workItem, flowRun);
    private static ResourcePlanContent Content(string value) => new("resource-planning.agentstration.io/v1", JsonSerializer.SerializeToElement(new { value }));

    private sealed class TestDbContextFactory(DbContextOptions<ResourcePlanningDbContext> options) : IDbContextFactory<ResourcePlanningDbContext>
    {
        public ResourcePlanningDbContext CreateDbContext() => new(options);
    }

    private sealed class MemoryRepository : IResourcePlanRepository
    {
        private readonly Dictionary<ResourcePlanId, ResourcePlanSnapshot> plans = [];
        private readonly List<ResourcePlanActivity> activities = [];
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ResourcePlanSnapshot> CreateAsync(ResourcePlan plan, CancellationToken cancellationToken) { var value = new ResourcePlanSnapshot(plan, ETag()); plans.Add(plan.Id, value); return Task.FromResult(value); }
        public Task<ResourcePlanSnapshot?> GetAsync(ResourcePlanScope scope, ResourcePlanId id, CancellationToken cancellationToken) => Task.FromResult(plans.TryGetValue(id, out var value) && value.Value.Scope == scope ? value : null);
        public Task<ResourcePlanPage> ListAsync(ResourcePlanScope scope, ResourcePlanStatus? status, int skip, int take, CancellationToken cancellationToken) => Task.FromResult(new ResourcePlanPage(plans.Values.Where(value => value.Value.Scope == scope && (status is null || value.Value.Status == status)).Skip(skip).Take(take).ToArray(), false));
        public Task<ResourcePlanSnapshot> UpdateAsync(ResourcePlan plan, string expectedETag, CancellationToken cancellationToken) { if (!plans.TryGetValue(plan.Id, out var current)) throw new ResourcePlanNotFoundException(plan.Id); if (current.ETag != expectedETag) throw new ResourcePlanConcurrencyException("stale"); var value = new ResourcePlanSnapshot(plan, ETag()); plans[plan.Id] = value; return Task.FromResult(value); }
        public Task AddActivityAsync(ResourcePlanActivity activity, CancellationToken cancellationToken) { activities.Add(activity); return Task.CompletedTask; }
        public Task<IReadOnlyList<ResourcePlanActivity>> ListActivitiesAsync(ResourcePlanScope scope, ResourcePlanId id, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ResourcePlanActivity>>(activities.Where(value => value.Scope == scope && value.PlanId == id).ToArray());
        private static string ETag() => $"\"{Guid.NewGuid():N}\"";
    }
}
