using System.Reflection;
using Agentstration.Security.AspNetCoreIdentity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Agentstration.Infrastructure;

public interface IAgentstrationStorageInitializer
{
    bool IsReady { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
}

internal sealed class SqliteStorageInitializer : IAgentstrationStorageInitializer
{
    public bool IsReady { get; private set; }
    public Task InitializeAsync(CancellationToken cancellationToken) { IsReady = true; return Task.CompletedTask; }
}

internal sealed class PostgreSqlStorageInitializer(
    IServiceProvider services,
    AgentstrationStorageOptions options,
    TimeProvider timeProvider) : IAgentstrationStorageInitializer
{
    private const long MigrationLockId = 0x4167656E74737472;
    public bool IsReady { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var deadline = timeProvider.GetUtcNow().AddSeconds(30);
        var acquired = false;
        while (!acquired && timeProvider.GetUtcNow() < deadline)
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@id)", connection);
            command.Parameters.AddWithValue("id", MigrationLockId);
            acquired = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
            if (!acquired) await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        if (!acquired) throw new InvalidOperationException("Timed out waiting for the Agentstration PostgreSQL migration lock.");

        try
        {
            await using (var command = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS management; CREATE SCHEMA IF NOT EXISTS work; CREATE SCHEMA IF NOT EXISTS flow; CREATE SCHEMA IF NOT EXISTS runtime; CREATE SCHEMA IF NOT EXISTS identity; CREATE SCHEMA IF NOT EXISTS scheduler;", connection))
                await command.ExecuteNonQueryAsync(cancellationToken);

            await MigrateFactoryAsync<Agentstration.Management.Storage.PostgreSql.ControlPlaneDbContext>(services, cancellationToken);
            await MigrateFactoryAsync<Agentstration.Work.Storage.PostgreSql.WorkDbContext>(services, cancellationToken);
            await MigrateFactoryAsync<Agentstration.Flow.Storage.PostgreSql.FlowDbContext>(services, cancellationToken);
            await MigrateFactoryAsync<Agentstration.Runtime.Storage.PostgreSql.RuntimeRunDbContext>(services, cancellationToken);
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<LocalIdentityDbContext>().Database.MigrateAsync(cancellationToken);
            await InitializeQuartzAsync(connection, cancellationToken);
            IsReady = true;
        }
        finally
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@id)", connection);
            command.Parameters.AddWithValue("id", MigrationLockId);
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private static async Task InitializeQuartzAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string resourceName = "Agentstration.Infrastructure.Triggers.QuartzPostgreSqlSchema.sql";
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Quartz schema '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MigrateFactoryAsync<TContext>(IServiceProvider services, CancellationToken cancellationToken) where TContext : DbContext
    {
        var factory = services.GetRequiredService<IDbContextFactory<TContext>>();
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
    }
}
