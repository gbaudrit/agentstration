namespace Agentstration.Knowledge.Contracts;

public sealed record KnowledgeProjectionPolicy
{
    public TimeSpan? TimeToLive { get; init; }
    public bool KeepHistory { get; init; }
    public TimeSpan? HistoryRetention { get; init; }
    public bool AllowExpiredOnRefreshFailure { get; init; }
}
