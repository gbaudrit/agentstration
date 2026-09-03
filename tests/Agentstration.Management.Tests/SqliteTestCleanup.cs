using Microsoft.Data.Sqlite;

namespace Agentstration.Management.Tests;

internal static class SqliteTestCleanup
{
    public static void ClearPoolsInDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var databasePath in Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories))
        {
            ClearPool(databasePath);
        }
    }

    public static void ClearPool(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        SqliteConnection.ClearPool(connection);
    }
}
