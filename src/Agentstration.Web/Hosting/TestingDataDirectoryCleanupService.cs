using Microsoft.Data.Sqlite;

namespace Agentstration.Web.Hosting;

internal sealed class TestingDataDirectoryCleanupService(string directory, bool deleteOnShutdown) : IHostedService
{
    internal string DirectoryPath { get; } = directory;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ClearPoolsInDirectory(DirectoryPath);
        if (!deleteOnShutdown) return;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }

    private static void ClearPoolsInDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var databasePath in Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories))
        {
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            SqliteConnection.ClearPool(connection);
        }
    }
}
