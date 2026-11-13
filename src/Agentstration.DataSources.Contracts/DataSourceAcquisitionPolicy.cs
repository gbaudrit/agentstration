namespace Agentstration.DataSources.Contracts;

/// <summary>Freshness is measured from successful completion; history is a separate retention rule.</summary>
public sealed record DataSourceAcquisitionPolicy
{
    public bool AllowDirect { get; init; }
    public TimeSpan? TimeToLive { get; init; }
    public bool KeepHistory { get; init; }
    public TimeSpan? HistoryRetention { get; init; }
}

public sealed record DirectDataSourceResult(long Length, string Sha256, DateTimeOffset ExpiresAt, string RequestId);
