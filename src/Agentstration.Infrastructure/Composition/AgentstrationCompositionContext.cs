namespace Agentstration.Infrastructure;

internal sealed class AgentstrationCompositionContext
{
    internal AgentstrationCompositionContext(AgentstrationCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataDirectory);
        ArgumentNullException.ThrowIfNull(options.Ai);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Ai.Provider);
        ArgumentNullException.ThrowIfNull(options.Storage);
        ArgumentNullException.ThrowIfNull(options.SourceVerificationIndex);
        ArgumentNullException.ThrowIfNull(options.SourceRegistryTransport);
        ArgumentNullException.ThrowIfNull(options.RuntimeWorkerLease);
        ArgumentNullException.ThrowIfNull(options.RuntimeWorkerDispatch);
        StorageProvider = options.Storage.GetProvider();
        options.SourceRegistryTransport.Validate();
        options.RuntimeWorkerLease.Validate();
        options.RuntimeWorkerDispatch.Validate();
        if (options.SourceVerificationIndex.TimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException("Source verification index timeout must be between 1 and 60 seconds.");
        if (options.SourceVerificationIndex.MaximumBytes is < 1024 or > Agentstration.Sources.SourceVerificationIndexReader.MaximumIndexBytes)
            throw new InvalidOperationException($"Source verification index maximum bytes must be between 1024 and {Agentstration.Sources.SourceVerificationIndexReader.MaximumIndexBytes}.");
        Options = options;
    }

    internal AgentstrationCompositionOptions Options { get; }
    internal AgentstrationStorageProvider StorageProvider { get; }
    internal bool UseManagedProfileResolver => string.Equals(Options.Ai.Provider, "Managed", StringComparison.OrdinalIgnoreCase);
    internal string ControlPlaneConnectionString => Options.ControlPlaneConnectionString ?? SqliteConnectionString("control-plane.db");
    internal string WorkPlaneConnectionString => Options.WorkPlaneConnectionString ?? SqliteConnectionString("work-plane.db");
    internal string FlowConnectionString => Options.FlowConnectionString ?? SqliteConnectionString("flow-plane.db");
    internal string RuntimeConnectionString => Options.RuntimeConnectionString ?? SqliteConnectionString("runtime-plane.db");
    internal string ResourcePlanningConnectionString => SqliteConnectionString("resource-planning.db") + ";Pooling=False";
    internal string SchedulerConnectionString => StorageProvider == AgentstrationStorageProvider.PostgreSql
        ? Options.Storage.ConnectionString!
        : SqliteConnectionString("scheduler.db") + ";Pooling=False";

    private string SqliteConnectionString(string fileName) => $"Data Source={Path.Combine(Options.DataDirectory, fileName)}";
}
