using System.Security.Claims;
using Agentstration.Management.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentstration.Security.AspNetCoreIdentity;

public sealed class LocalBootstrapLock
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
}

public sealed class LocalIdentityDatabaseInitializer(IServiceScopeFactory scopeFactory)
{
    public const string InitialMigration = "20260816100707_InitialIdentity";
    private static readonly string[] LegacyTables =
    [
        "AgentstrationBootstrap", "AspNetRoleClaims", "AspNetRoles", "AspNetUserClaims",
        "AspNetUserLogins", "AspNetUserRoles", "AspNetUsers", "AspNetUserTokens"
    ];

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LocalIdentityDbContext>();
        await BaselineLegacyDatabaseAsync(database, cancellationToken);
        await database.Database.MigrateAsync(cancellationToken);
    }

    private static async Task BaselineLegacyDatabaseAsync(LocalIdentityDbContext database, CancellationToken cancellationToken)
    {
        if (!database.Database.IsSqlite()) return;

        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var existing = new HashSet<string>(StringComparer.Ordinal);
            await using (var command = database.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(0));
            }

            if (!existing.Contains("AspNetUsers")) return;
            var missing = LegacyTables.Where(table => !existing.Contains(table)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    $"The existing Identity database has a partial legacy schema. Missing tables: {string.Join(", ", missing)}.");

            await database.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                """,
                cancellationToken);
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({InitialMigration}, {ProductInfo.GetVersion()})",
                cancellationToken);
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
        }
    }
}

public static class LocalIdentityServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationLocalIdentity(
        this IServiceCollection services,
        string connectionString,
        string dataProtectionKeysPath,
        bool useDevelopmentPasswordPolicy = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return services.AddAgentstrationIdentity(
            options => options.UseSqlite(connectionString),
            dataProtectionKeysPath,
            useDevelopmentPasswordPolicy);
    }

    public static IServiceCollection AddAgentstrationIdentity(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDatabase,
        string dataProtectionKeysPath,
        bool useDevelopmentPasswordPolicy = false)
    {
        ArgumentNullException.ThrowIfNull(configureDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataProtectionKeysPath);
        var keysDirectory = PrepareDataProtectionKeysDirectory(dataProtectionKeysPath);
        services.AddDataProtection()
            .SetApplicationName("Agentstration")
            .PersistKeysToFileSystem(keysDirectory);
        services.AddDbContext<LocalIdentityDbContext>(configureDatabase);
        services.AddIdentityCore<LocalIdentityUser>(options =>
            {
                options.Password.RequiredLength = useDevelopmentPasswordPolicy ? 5 : 12;
                options.Password.RequireDigit = !useDevelopmentPasswordPolicy;
                options.Password.RequireLowercase = !useDevelopmentPasswordPolicy;
                options.Password.RequireUppercase = !useDevelopmentPasswordPolicy;
                options.Password.RequireNonAlphanumeric = !useDevelopmentPasswordPolicy;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.User.RequireUniqueEmail = false;
            })
            .AddSignInManager()
            .AddEntityFrameworkStores<LocalIdentityDbContext>()
            .AddDefaultTokenProviders();
        services.AddScoped<IUserClaimsPrincipalFactory<LocalIdentityUser>, LocalIdentityClaimsPrincipalFactory>();
        services.AddScoped<LocalBootstrapCoordinator>();
        services.AddScoped<ILocalAccountPrincipalResolver, LocalAccountPrincipalResolver>();
        services.AddScoped<IBootstrapResourceHandler, PlatformAdministratorBootstrapHandler>();
        services.AddScoped<LocalAccountAdministrationService>();
        services.AddScoped<LocalAccountSecurityService>();
        services.AddScoped<LocalAuthenticationService>();
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        services.AddSingleton<LocalBootstrapLock>();
        services.AddSingleton<LocalIdentityDatabaseInitializer>();
        return services;
    }

    private static DirectoryInfo PrepareDataProtectionKeysDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            directory.Create();
            directory.Refresh();
            var probe = Path.Combine(directory.FullName, $".agentstration-write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 1,
                Options = FileOptions.DeleteOnClose
            })) { }
            return directory;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException("The configured Data Protection keys path is not a writable directory.", exception);
        }
    }
}

