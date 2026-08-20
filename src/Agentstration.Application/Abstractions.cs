using Agentstration.Domain;

namespace Agentstration.Application;

public interface IPlatformStore
{
    Task<IReadOnlyList<Workspace>> ListWorkspacesAsync(CancellationToken cancellationToken);
    Task AddWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken);
    Task<Workspace?> GetWorkspaceAsync(WorkspaceId workspaceId, CancellationToken cancellationToken);
    Task AddInboxAsync(Inbox inbox, CancellationToken cancellationToken);
    Task<IReadOnlyList<Inbox>> ListInboxesAsync(WorkspaceId workspaceId, CancellationToken cancellationToken);
    Task<Inbox?> GetInboxAsync(WorkspaceId workspaceId, InboxId inboxId, CancellationToken cancellationToken);
    Task<Item?> FindItemByHashAsync(WorkspaceId workspaceId, InboxId inboxId, string hash, CancellationToken cancellationToken);
    Task AddItemAsync(Item item, RawContent raw, CancellationToken cancellationToken);
    Task<Item?> GetItemAsync(WorkspaceId workspaceId, ItemId itemId, CancellationToken cancellationToken);
    Task<RawContent?> GetRawContentAsync(WorkspaceId workspaceId, ItemId itemId, CancellationToken cancellationToken);
    Task SetItemStatusAsync(WorkspaceId workspaceId, ItemId itemId, ItemStatus status, string? error, CancellationToken cancellationToken);
    Task AddNormalizedContentAsync(NormalizedContent content, CancellationToken cancellationToken);
    Task<NormalizedContent?> GetNormalizedContentAsync(WorkspaceId workspaceId, ItemId itemId, CancellationToken cancellationToken);
    Task AddItemAnalysisAsync(ItemAnalysis analysis, CancellationToken cancellationToken);
    Task<IReadOnlyList<ItemAnalysis>> GetItemAnalysesAsync(WorkspaceId workspaceId, ItemId itemId, CancellationToken cancellationToken);
    Task AddMissionAsync(Mission mission, CancellationToken cancellationToken);
    Task<IReadOnlyList<Mission>> ListMissionsAsync(WorkspaceId workspaceId, CancellationToken cancellationToken);
    Task<Mission?> GetMissionAsync(WorkspaceId workspaceId, MissionId missionId, CancellationToken cancellationToken);
    Task UpdateMissionAsync(Mission mission, CancellationToken cancellationToken);
    Task AddMissionRunAsync(MissionRun run, CancellationToken cancellationToken);
    Task UpdateMissionRunAsync(MissionRun run, CancellationToken cancellationToken);
    Task<IReadOnlyList<MissionRun>> ListMissionRunsAsync(WorkspaceId workspaceId, MissionId missionId, CancellationToken cancellationToken);
    Task AddNotificationAsync(Notification notification, CancellationToken cancellationToken);
    Task<IReadOnlyList<Notification>> ListNotificationsAsync(WorkspaceId workspaceId, MissionId missionId, CancellationToken cancellationToken);
    Task AddAuditEntryAsync(AuditEntry entry, CancellationToken cancellationToken);
}

public interface IItemAnalysisStore { Task AddAsync(ItemAnalysis analysis, CancellationToken cancellationToken); }
public interface IBlobStore { Task<string> PutAsync(WorkspaceId workspaceId, string name, Stream content, CancellationToken cancellationToken); }

public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken) where TEvent : IDomainEvent;
}

public interface IEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}

public sealed record RoutingContext(WorkspaceId WorkspaceId, Item Item, RawContent Content);
public sealed record RoutingDecision(string Route, bool StoreOnly, string Reason);
public sealed record RoutingRule(string Name, Func<RoutingContext, bool> Matches, RoutingDecision Decision);

public interface IIntentRouter
{
    ValueTask<RoutingDecision> RouteAsync(RoutingContext context, CancellationToken cancellationToken);
}

public sealed record AgentExecutionRequest(WorkspaceId WorkspaceId, ItemId ItemId, string Content);
public sealed record AgentExecutionResult(string Summary, IReadOnlyList<string> Categories);
public interface IAgentRuntime { Task<AgentExecutionResult> RunAsync(AgentExecutionRequest request, CancellationToken cancellationToken); }

public interface IScheduler { Task TriggerDueMissionsAsync(CancellationToken cancellationToken); }
public interface IScheduledMissionStore { Task<IReadOnlyList<Mission>> GetDueAsync(DateTimeOffset now, CancellationToken cancellationToken); }
public sealed record MissionSchedule(TimeSpan Frequency, DateTimeOffset NextRunAt);
public sealed record MissionTrigger(string Kind, decimal? Threshold);

public interface IObservationTool { Task<decimal> ObserveAsync(Mission mission, int priorRunCount, CancellationToken cancellationToken); }
public interface IContentSourceReader { Task<string> ReadUrlAsync(Uri uri, CancellationToken cancellationToken); }
public interface IItemProcessingQueue
{
    ValueTask EnqueueAsync(WorkspaceId workspaceId, ItemId itemId, CancellationToken cancellationToken);
    ValueTask<(WorkspaceId WorkspaceId, ItemId ItemId)> DequeueAsync(CancellationToken cancellationToken);
}
