using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Sources;

namespace Agentstration.Infrastructure;

public sealed record AgentstrationServiceRegistrationOptions
{
    public required string DataDirectory { get; init; }
    public AiProviderOptions? AiOptions { get; init; }
    public string? ControlPlaneConnectionString { get; init; }
    public string? WorkPlaneConnectionString { get; init; }
    public string? FlowConnectionString { get; init; }
    public string? RuntimeConnectionString { get; init; }
    public AgentstrationStorageOptions? StorageOptions { get; init; }
    public bool EnableHostedServices { get; init; } = true;
    public SourceVerificationIndexOptions? SourceVerificationIndexOptions { get; init; }
}

internal sealed record AgentstrationServiceRegistrationContext(
    string DataDirectory,
    AiProviderOptions AiOptions,
    AgentstrationStorageOptions StorageOptions,
    AgentstrationStorageProvider StorageProvider,
    string? ControlPlaneConnectionString,
    string? WorkPlaneConnectionString,
    string? FlowConnectionString,
    string? RuntimeConnectionString,
    string SchedulerConnectionString,
    bool EnableHostedServices,
    SourceVerificationIndexOptions SourceVerificationIndexOptions)
{
    public bool UseManagedProfileResolver =>
        string.Equals(AiOptions.Provider, "Managed", StringComparison.OrdinalIgnoreCase);

    public static AgentstrationServiceRegistrationContext Create(AgentstrationServiceRegistrationOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataDirectory);

        var aiOptions = options.AiOptions
            ?? new AiProviderOptions("Deterministic", new Uri("http://localhost/"), "deterministic", null);
        var storageOptions = options.StorageOptions ?? new AgentstrationStorageOptions();
        var storageProvider = storageOptions.GetProvider();
        var schedulerConnectionString = storageProvider == AgentstrationStorageProvider.PostgreSql
            ? storageOptions.ConnectionString!
            : $"Data Source={Path.Combine(options.DataDirectory, "scheduler.db")};Pooling=False";

        return new AgentstrationServiceRegistrationContext(
            options.DataDirectory,
            aiOptions,
            storageOptions,
            storageProvider,
            options.ControlPlaneConnectionString,
            options.WorkPlaneConnectionString,
            options.FlowConnectionString,
            options.RuntimeConnectionString,
            schedulerConnectionString,
            options.EnableHostedServices,
            options.SourceVerificationIndexOptions ?? new SourceVerificationIndexOptions());
    }
}
