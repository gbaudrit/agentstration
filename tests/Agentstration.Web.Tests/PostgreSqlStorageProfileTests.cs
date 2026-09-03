using System.Net;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

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
            await StartAndAssertReadyAsync(connectionString, dataDirectory, exerciseWorkplace: true);
            await StartAndAssertReadyAsync(connectionString, dataDirectory, exerciseWorkplace: false);
            Assert.IsFalse(Directory.EnumerateFiles(dataDirectory, "*.db", SearchOption.AllDirectories).Any(), "The PostgreSQL profile must not create SQLite database files.");
        }
        finally
        {
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
        }
    }

    private static async Task StartAndAssertReadyAsync(string connectionString, string dataDirectory, bool exerciseWorkplace)
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
        if (exerciseWorkplace)
        {
            await AssertWorkplaceDefaultReplacementAsync(host.Services);
            await AssertPrincipalPreferencesRoundTripAsync(host.Services);
        }
    }

    private static async Task AssertWorkplaceDefaultReplacementAsync(IServiceProvider services)
    {
        var repository = services.GetRequiredService<IWorkplaceRepository>();
        var workspaceId = new WorkspaceId(Guid.NewGuid());
        var publishedAt = DateTimeOffset.UtcNow;
        var home = new WorkplaceDashboard
        {
            Id = new("home"),
            WorkspaceId = workspaceId,
            Name = "home",
            DisplayName = "Home",
            IsDefault = true,
            PublishedAt = publishedAt
        };
        var replacement = home with
        {
            Id = new("operations"),
            Name = "operations",
            DisplayName = "Operations",
            PublishedAt = publishedAt.AddSeconds(1)
        };

        await repository.ReplaceDefaultDashboardAsync(home, default);
        await repository.ReplaceDefaultDashboardAsync(replacement, default);

        var dashboards = await repository.ListDashboardsAsync(workspaceId, default);
        Assert.HasCount(2, dashboards);
        Assert.AreEqual(replacement.Id, dashboards.Single(value => value.IsDefault).Id);
    }

    private static async Task AssertPrincipalPreferencesRoundTripAsync(IServiceProvider services)
    {
        var store = services.GetRequiredService<IIdentityStore>();
        var now = DateTimeOffset.UtcNow;
        var principal = new Principal(Guid.NewGuid(), PrincipalKind.Human, "PostgreSQL preferences", null, PrincipalStatus.Active, now);
        var expected = new PrincipalPreferences(
            principal.Id,
            ThemePreference.Dark,
            now,
            "fr-FR",
            Guid.NewGuid(),
            Guid.NewGuid());

        await store.AddPrincipalAsync(principal, default);
        await store.UpsertPrincipalPreferencesAsync(expected, default);

        Assert.AreEqual(expected, await store.GetPrincipalPreferencesAsync(principal.Id, default));
    }
}
