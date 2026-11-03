using Microsoft.Data.Sqlite;

namespace Agentstration.Management.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SqlitePoolOwnershipTests
{
    [TestMethod]
    public async Task ConcurrentPooledConnectionsKeepSeparateHandlesWhileReadersAreActive()
    {
        const int width = 8;
        const int waves = 64;
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-sqlite-pool", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "pool.db"),
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
        using var pool = new SqliteConnection(connectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            for (var wave = 0; wave < waves; wave++)
            {
                // Exercise checkout from an empty pool without disturbing other databases.
                pool.Open();
                pool.Close();
                SqliteConnection.ClearPool(pool);
                using var start = new ManualResetEventSlim();
                using var opened = new Barrier(width);
                var handles = new object?[width];
                var readers = Enumerable.Range(0, width).Select(index => Task.Factory.StartNew(() =>
                {
                    try
                    {
                        start.Wait(timeout.Token);
                        using var connection = new SqliteConnection(connectionString);
                        connection.Open();
                        connection.CreateCollation("pool_probe", string.CompareOrdinal);
                        handles[index] = connection.Handle;
                        using var command = connection.CreateCommand();
                        command.CommandText = "SELECT 1";
                        using var reader = command.ExecuteReader();
                        Assert.IsTrue(reader.Read());
                        Assert.AreEqual(1, reader.GetInt32(0));
                        opened.SignalAndWait(timeout.Token);
                    }
                    finally
                    {
                        opened.RemoveParticipant();
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                start.Set();
                await Task.WhenAll(readers);
                Assert.IsTrue(handles.All(handle => handle is not null));
                Assert.AreEqual(width, handles.Distinct(ReferenceEqualityComparer.Instance).Count(),
                    $"Wave {wave} lent the same native SQLite handle to concurrent owners.");
            }
        }
        finally
        {
            SqliteConnection.ClearPool(pool);
            Directory.Delete(directory, recursive: true);
        }
    }
}
