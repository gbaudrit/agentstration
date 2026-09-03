using Npgsql;

namespace Agentstration.Infrastructure;

public enum AgentstrationStorageProvider
{
    Sqlite,
    PostgreSql
}

public sealed record AgentstrationStorageOptions
{
    public const string SectionName = "Agentstration:Storage";
    public string Provider { get; init; } = nameof(AgentstrationStorageProvider.Sqlite);
    public string? ConnectionString { get; init; }

    public AgentstrationStorageProvider GetProvider()
    {
        if (!Enum.TryParse<AgentstrationStorageProvider>(Provider, true, out var provider))
            throw new InvalidOperationException("Agentstration:Storage:Provider must be either 'Sqlite' or 'PostgreSql'.");
        if (provider == AgentstrationStorageProvider.PostgreSql && string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("ConnectionStrings:Agentstration is required when Agentstration:Storage:Provider is PostgreSql.");
        if (provider == AgentstrationStorageProvider.PostgreSql)
        {
            try
            {
                var connection = new NpgsqlConnectionStringBuilder(ConnectionString);
                if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database))
                    throw new ArgumentException();
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException("ConnectionStrings:Agentstration is not a valid PostgreSQL connection string; configure at least Host and Database.", exception);
            }
        }
        return provider;
    }
}
