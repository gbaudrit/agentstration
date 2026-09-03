using Microsoft.Data.Sqlite;

namespace Agentstration.Web.Hosting;

internal sealed class TestingDataDirectoryCleanup(
    string directory,
    IReadOnlyList<string> connectionStrings,
    ILogger<TestingDataDirectoryCleanup> logger) : IDisposable
{
    private int disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        try
        {
            foreach (var connectionString in connectionStrings)
            {
                using var connection = new SqliteConnection(connectionString);
                SqliteConnection.ClearPool(connection);
            }

            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not delete testing data directory {DataDirectory}", directory);
        }
    }
}
