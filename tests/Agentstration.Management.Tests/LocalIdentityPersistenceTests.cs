using Agentstration.Security.AspNetCoreIdentity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class LocalIdentityPersistenceTests
{
    [TestMethod]
    public async Task FreshDatabaseUsesMigrationAndDataProtectionKeysSurviveProviderRestart()
    {
        var root = TemporaryDirectory();
        try
        {
            var protectedValue = string.Empty;
            await using (var provider = Services(root).BuildServiceProvider())
            {
                await provider.GetRequiredService<LocalIdentityDatabaseInitializer>().InitializeAsync(default);
                await using var scope = provider.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<LocalIdentityDbContext>();
                CollectionAssert.Contains(
                    (await database.Database.GetAppliedMigrationsAsync()).ToArray(),
                    LocalIdentityDatabaseInitializer.InitialMigration);
                Assert.IsEmpty((await database.Database.GetPendingMigrationsAsync()).ToArray());
                protectedValue = provider.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("persistence-test")
                    .Protect("durable-session");
            }

            await using (var restarted = Services(root).BuildServiceProvider())
            {
                var value = restarted.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("persistence-test")
                    .Unprotect(protectedValue);
                Assert.AreEqual("durable-session", value);
            }
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public async Task LegacyEnsureCreatedDatabaseIsBaselinedWithoutLosingAccounts()
    {
        var root = TemporaryDirectory();
        try
        {
            await using var provider = Services(root).BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<LocalIdentityDbContext>();
                await database.Database.EnsureCreatedAsync();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalIdentityUser>>();
                var result = await users.CreateAsync(
                    new LocalIdentityUser { Id = Guid.NewGuid(), UserName = "legacy-admin" },
                    "A-strong-legacy-password-42!");
                Assert.IsTrue(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
            }

            await provider.GetRequiredService<LocalIdentityDatabaseInitializer>().InitializeAsync(default);

            await using var verification = provider.CreateAsyncScope();
            var migrated = verification.ServiceProvider.GetRequiredService<LocalIdentityDbContext>();
            CollectionAssert.Contains(
                (await migrated.Database.GetAppliedMigrationsAsync()).ToArray(),
                LocalIdentityDatabaseInitializer.InitialMigration);
            var usersAfterMigration = verification.ServiceProvider.GetRequiredService<UserManager<LocalIdentityUser>>();
            Assert.IsNotNull(await usersAfterMigration.FindByNameAsync("legacy-admin"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public async Task PartialLegacyIdentitySchemaFailsStartupValidation()
    {
        var root = TemporaryDirectory();
        try
        {
            await using var provider = Services(root).BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<LocalIdentityDbContext>();
                await database.Database.ExecuteSqlRawAsync("CREATE TABLE AspNetUsers (Id TEXT NOT NULL PRIMARY KEY)");
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.GetRequiredService<LocalIdentityDatabaseInitializer>().InitializeAsync(default));
            StringAssert.Contains(exception.Message, "partial legacy schema");
            StringAssert.Contains(exception.Message, "AgentstrationBootstrap");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public void DataProtectionKeysPathMustBeAWritableDirectory()
    {
        var root = TemporaryDirectory();
        try
        {
            var file = Path.Combine(root, "not-a-directory");
            File.WriteAllText(file, "occupied");
            var services = new ServiceCollection();

            var exception = Assert.Throws<InvalidOperationException>(() =>
                services.AddAgentstrationLocalIdentity(
                    $"Data Source={Path.Combine(root, "identity.db")}",
                    file));

            StringAssert.Contains(exception.Message, "not a writable directory");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static ServiceCollection Services(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentstrationLocalIdentity(
            $"Data Source={Path.Combine(root, "identity.db")}",
            Path.Combine(root, "data-protection-keys"));
        return services;
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentstration-identity-persistence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        SqliteTestCleanup.ClearPoolsInDirectory(path);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
