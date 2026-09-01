using Agentstration.Management.Abstractions;
using Agentstration.Management.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SecurityAuditPersistenceTests
{
    [TestMethod]
    public async Task AuditEventsSurviveControlPlaneProviderRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "control-plane.db");
        var expected = new SecurityAuditEvent(
            Guid.NewGuid(),
            SecurityAuditActions.LocalAccountDisabled,
            SecurityAuditOutcome.Succeeded,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            "0123456789abcdef",
            DateTimeOffset.UtcNow);

        try
        {
            await using (var provider = Services(databasePath).BuildServiceProvider())
            {
                await InitializeAsync(provider);
                await provider.GetRequiredService<ISecurityAuditStore>().AppendAsync(expected, default);
            }

            await using (var restarted = Services(databasePath).BuildServiceProvider())
            {
                await InitializeAsync(restarted);
                var events = await restarted.GetRequiredService<ISecurityAuditStore>().ListLatestAsync(10, default);
                Assert.HasCount(1, events);
                Assert.AreEqual(expected, events[0]);
            }
        }
        finally
        {
            SqliteTestCleanup.ClearPoolsInDirectory(directory);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static ServiceCollection Services(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSqliteControlPlane($"Data Source={databasePath}");
        return services;
    }

    private static Task InitializeAsync(IServiceProvider provider) =>
        ((SqliteControlPlaneStore)provider.GetRequiredService<IControlPlaneStore>()).InitializeAsync(default);
}
