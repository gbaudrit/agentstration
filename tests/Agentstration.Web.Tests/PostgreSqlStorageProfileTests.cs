using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class PostgreSqlStorageProfileTests
{
    [TestMethod]
    public async Task EmptyDatabaseMigratesAndRemainsReadyAfterRestart()
    {
        var connectionString = Environment.GetEnvironmentVariable("AGENTSTRATION_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Inconclusive("Set AGENTSTRATION_TEST_POSTGRES to run PostgreSQL integration tests.");

        var dataDirectory = Path.Combine(Path.GetTempPath(), $"agentstration-postgresql-profile-{Guid.NewGuid():N}");
        try
        {
            await StartAndAssertReadyAsync(connectionString, dataDirectory);
            await StartAndAssertReadyAsync(connectionString, dataDirectory);
            Assert.IsFalse(Directory.EnumerateFiles(dataDirectory, "*.db", SearchOption.AllDirectories).Any(), "The PostgreSQL profile must not create SQLite database files.");
        }
        finally
        {
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
        }
    }

    private static async Task StartAndAssertReadyAsync(string connectionString, string dataDirectory)
    {
        await using var host = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Storage:Provider", "PostgreSql");
            builder.UseSetting("ConnectionStrings:Agentstration", connectionString);
            builder.UseSetting("Data:Directory", dataDirectory);
            builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "false");
        });
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
