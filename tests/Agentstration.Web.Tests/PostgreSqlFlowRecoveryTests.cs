using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Flows.Storage.PostgreSql;
using Agentstration.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class PostgreSqlFlowRecoveryTests
{
    [TestMethod]
    public async Task FlowRecoveryRepairsLegacyStatusesAndDrainsChangingPages()
    {
        var connectionString = Environment.GetEnvironmentVariable("AGENTSTRATION_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Inconclusive("Set AGENTSTRATION_TEST_POSTGRES to run PostgreSQL integration tests.");

        // An isolated database lets this test exercise an actual upgrade without touching other stores.
        var databaseName = $"flow_recovery_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var isolated = new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName, Pooling = false };
            await AssertRecoveryAsync(isolated.ConnectionString);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task AssertRecoveryAsync(string connectionString)
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new RecoveryClock(now));
        services.AddPostgreSqlFlowStorage(connectionString);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<FlowDbContext>>();
        await using var context = await factory.CreateDbContextAsync();
        await context.GetService<IMigrator>().MigrateAsync("20261024063319_AddHierarchicalFlowScopes");
        var repository = provider.GetRequiredService<IFlowRepository>();
        await repository.InitializeAsync(default);
        var principal = Guid.NewGuid();
        var scopes = new[] { FlowRunScope.Instance(principal), FlowRunScope.Tenant(Guid.NewGuid(), principal), FlowRunScope.Workspace(Guid.NewGuid(), new(Guid.NewGuid()), principal) };
        var expected = new List<FlowRunKey>();
        var original = new List<StoredFlowRun>();
        for (var index = 0; index < 621; index++)
        {
            var scope = scopes[index % scopes.Length];
            var status = index < 200 ? FlowRunStatus.Succeeded : (FlowRunStatus)(index % Enum.GetValues<FlowRunStatus>().Length);
            var stored = await repository.CreateRunAsync(new FlowRun
            {
                Id = $"same-id-{index / scopes.Length:D4}",
                Scope = scope,
                WorkspaceId = scope.WorkspaceId,
                FlowId = new("recovery"),
                FlowVersion = "1.0.0",
                DefinitionSnapshot = new(scope.ScopeRef, new("recovery"), "1.0.0", null,
                    new DirectFlowDefinition(new(FlowTargetKind.Agent, "assistant")), new Dictionary<string, string>(), now),
                Status = status,
                CreatedAt = now,
                Input = JsonSerializer.SerializeToElement(new { })
            }, default);
            original.Add(stored);
            if (!status.IsTerminal()) expected.Add(new(scope.ScopeRef, stored.Value.Id));
        }

        // Model the old PostgreSQL bug in both directions: active payload/terminal metadata and vice versa.
        await context.Database.ExecuteSqlRawAsync("UPDATE flow.\"FlowResources\" SET \"Status\" = CASE WHEN \"Status\" IN (0, 1, 2, 7) THEN 3 ELSE 0 END WHERE \"Kind\" = 'run'");
        await context.Database.MigrateAsync();
        foreach (var stored in original)
        {
            var repaired = (await repository.GetRunExactAsync(stored.Value.Scope.ScopeRef, stored.Value.Id, default))!;
            Assert.AreEqual(stored.ETag, repaired.ETag);
            Assert.AreEqual(stored.UpdatedAt, repaired.UpdatedAt);
            Assert.AreEqual(JsonSerializer.Serialize(stored.Value), JsonSerializer.Serialize(repaired.Value), "The upgrade must preserve run payloads.");
        }

        var actual = new List<FlowRunKey>();
        FlowRunKey? after = null;
        while (true)
        {
            var page = await repository.ListRecoverableRunsAsync(after, 200, default);
            actual.AddRange(page);
            foreach (var key in page)
            {
                var stored = (await repository.GetRunExactAsync(key.ScopeRef, key.RunId, default))!;
                var terminal = await repository.UpdateRunAsync(stored.Value with { Status = FlowRunStatus.Succeeded }, stored.ETag, default);
                await repository.DeleteRunExactAsync(key.ScopeRef, key.RunId, terminal.ETag, default);
            }
            if (page.Count < 200) break;
            after = page[^1];
        }
        CollectionAssert.AreEquivalent(expected.ToArray(), actual.ToArray());
        Assert.IsEmpty(await repository.ListRecoverableRunsAsync(null, 200, default));

        // Updates must maintain metadata after the repair, including transitions back to an active state.
        var candidate = original.First(value => value.Value.Status.IsTerminal());
        var pending = await repository.UpdateRunAsync(candidate.Value with { Status = FlowRunStatus.Pending }, candidate.ETag, default);
        CollectionAssert.AreEqual(new[] { new FlowRunKey(pending.Value.Scope.ScopeRef, pending.Value.Id) }, (await repository.ListRecoverableRunsAsync(null, 200, default)).ToArray());
        await repository.UpdateRunAsync(pending.Value with { Status = FlowRunStatus.Failed }, pending.ETag, default);
        Assert.IsEmpty(await repository.ListRecoverableRunsAsync(null, 200, default));
    }

    private sealed class RecoveryClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
