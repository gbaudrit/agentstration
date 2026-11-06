using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SqliteConnectionPoolTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ConcurrentPooledConnectionsRetainDistinctHandlesWithActiveReaders()
    {
        const int workerCount = 16;
        const int rounds = 500;
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-sqlite-pool", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "pool.db"),
            Pooling = true
        }.ToString();
        using var poolOwner = new SqliteConnection(connectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var barrier = new Barrier(workerCount);
        var handles = new nint[workerCount];
        try
        {
            poolOwner.Open();
            using (var version = poolOwner.CreateCommand())
            {
                version.CommandText = "SELECT sqlite_version()";
                TestContext.WriteLine($"Driver: {typeof(SqliteConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}; native SQLite: {version.ExecuteScalar()}");
            }
            poolOwner.Close();

            // Dedicated threads keep pool checkout concurrent without starving the thread pool at the barriers.
            var workers = Enumerable.Range(0, workerCount).Select(worker => Task.Factory.StartNew(() =>
            {
                try
                {
                    for (var round = 0; round < rounds; round++)
                    {
                        // Alternate cold and warm waves: new internal connections also exercise owner publication.
                        // The previous wave's final barrier guarantees that every connection is already disposed.
                        if (worker == 0 && round % 2 == 0)
                        {
                            SqliteConnection.ClearPool(poolOwner);
                        }
                        barrier.SignalAndWait(timeout.Token);
                        using (var connection = new SqliteConnection(connectionString))
                        {
                            connection.Open();
                            handles[worker] = connection.Handle!.DangerousGetHandle();
                            using var command = connection.CreateCommand();
                            command.CommandText = "SELECT $value UNION ALL SELECT $value + 1";
                            var value = worker * rounds + round;
                            command.Parameters.AddWithValue("$value", value);
                            using var reader = command.ExecuteReader();
                            Assert.IsTrue(reader.Read());
                            Assert.AreEqual(value, reader.GetInt32(0));

                            // All readers and logical connections remain alive during the ownership check.
                            barrier.SignalAndWait(timeout.Token);
                            Assert.AreEqual(workerCount, handles.Distinct().Count(), $"Round {round}: live logical connections share a native SQLite handle.");
                            Assert.IsTrue(reader.Read());
                            Assert.AreEqual(value + 1, reader.GetInt32(0));
                            Assert.IsFalse(reader.Read());
                            barrier.SignalAndWait(timeout.Token);
                        }
                        barrier.SignalAndWait(timeout.Token);
                    }
                }
                catch
                {
                    timeout.Cancel();
                    throw;
                }
            }, timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

            await Task.WhenAll(workers);
        }
        finally
        {
            SqliteConnection.ClearPool(poolOwner);
            Directory.Delete(directory, true);
        }
    }
}
