using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Work.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class WorkItemExecutionScopePersistenceTests
{
    [TestMethod]
    public async Task SqliteKeepsIdenticalWorkItemIdsIsolatedByExactExecutionScope()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-work-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddSqliteWorkPlane($"Data Source={Path.Combine(directory, "work.db")};Pooling=False");
            await using var provider = services.BuildServiceProvider();
            var repository = provider.GetRequiredService<IWorkItemRepository>();
            await repository.InitializeAsync(default);

            var id = WorkItemId.New();
            var principalId = Guid.NewGuid();
            var tenantId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var now = DateTimeOffset.Parse("2026-10-15T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            var scopes = new[]
            {
                ExactExecutionScope.Instance(principalId),
                ExactExecutionScope.Tenant(tenantId, principalId),
                ExactExecutionScope.Workspace(tenantId, workspaceId, principalId)
            };

            foreach (var scope in scopes)
                await repository.CreateAsync(WorkItem.Create(id, scope, "test", scope.ScopeRef.Value, now), default);

            foreach (var scope in scopes)
            {
                var stored = await repository.GetExactAsync(scope.ScopeRef, principalId, id, default);
                Assert.IsNotNull(stored);
                Assert.AreEqual(scope.ScopeRef, stored.Value.ExecutionScopeRef);
                Assert.AreEqual(scope.ScopeRef.Value, stored.Value.Instruction);
            }

            Assert.IsNull(await repository.GetExactAsync(ResourceScopeRef.Tenant(Guid.NewGuid()), principalId, id, default));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
