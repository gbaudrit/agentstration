using Agentstration.Application;
using Agentstration.Domain;

namespace Agentstration.Infrastructure.Persistence;

public class InMemoryPlatformStore : IPlatformStore
{
    protected readonly object Gate = new();
    protected PlatformState State { get; set; } = new();

    protected virtual Task ChangedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<Workspace>> ListWorkspacesAsync(CancellationToken cancellationToken) => ReadAsync<IReadOnlyList<Workspace>>(state => state.Workspaces.OrderBy(x => x.Name).ToArray());
    public Task<Workspace?> GetWorkspaceAsync(WorkspaceId id, CancellationToken cancellationToken) => ReadAsync(state => state.Workspaces.FirstOrDefault(x => x.Id == id));
    public Task<IReadOnlyList<Inbox>> ListInboxesAsync(WorkspaceId id, CancellationToken cancellationToken) => ReadAsync<IReadOnlyList<Inbox>>(state => state.Inboxes.Where(x => x.WorkspaceId == id).OrderBy(x => x.Name).ToArray());
    public Task<Inbox?> GetInboxAsync(WorkspaceId workspaceId, InboxId id, CancellationToken cancellationToken) => ReadAsync(state => state.Inboxes.FirstOrDefault(x => x.WorkspaceId == workspaceId && x.Id == id));
    public Task<Item?> FindItemByHashAsync(WorkspaceId workspaceId, InboxId inboxId, string hash, CancellationToken cancellationToken) => ReadAsync(state => state.Items.FirstOrDefault(x => x.WorkspaceId == workspaceId && x.InboxId == inboxId && x.ContentHash == hash));
    public Task<Item?> GetItemAsync(WorkspaceId workspaceId, ItemId id, CancellationToken cancellationToken) => ReadAsync(state => state.Items.FirstOrDefault(x => x.WorkspaceId == workspaceId && x.Id == id));
    public Task<RawContent?> GetRawContentAsync(WorkspaceId workspaceId, ItemId id, CancellationToken cancellationToken) => ReadAsync(state => state.RawContents.FirstOrDefault(x => x.WorkspaceId == workspaceId && x.ItemId == id));
    public Task<NormalizedContent?> GetNormalizedContentAsync(WorkspaceId workspaceId, ItemId id, CancellationToken cancellationToken) => ReadAsync(state => state.NormalizedContents.FirstOrDefault(x => x.WorkspaceId == workspaceId && x.ItemId == id));
    public Task<IReadOnlyList<MemoryEntry>> GetItemMemoryAsync(WorkspaceId workspaceId, ItemId id, CancellationToken cancellationToken) => ReadAsync<IReadOnlyList<MemoryEntry>>(state => state.MemoryEntries.Where(x => x.WorkspaceId == workspaceId && x.ItemId == id).OrderByDescending(x => x.CreatedAt).ToArray());
    public Task<IReadOnlyList<Mission>> ListMissionsAsync(WorkspaceId id, CancellationToken cancellationToken) => ReadAsync<IReadOnlyList<Mission>>(state => state.Missions.Where(x => x.WorkspaceId == id).OrderBy(x => x.Name).ToArray());
    public Task<Mission?> GetMissionAsync(WorkspaceId workspaceId, MissionId id, CancellationToken cancellationToken) => ReadAsync(state => state.Missions.FirstOrDefault(x => x.WorkspaceId == workspaceId && x.Id == id));
    public Task<IReadOnlyList<MissionRun>> ListMissionRunsAsync(WorkspaceId workspaceId, MissionId missionId, CancellationToken cancellationToken) => ReadAsync<IReadOnlyList<MissionRun>>(state => state.MissionRuns.Where(x => x.WorkspaceId == workspaceId && x.MissionId == missionId).OrderByDescending(x => x.StartedAt).ToArray());
    public Task<IReadOnlyList<Notification>> ListNotificationsAsync(WorkspaceId workspaceId, MissionId missionId, CancellationToken cancellationToken) => ReadAsync<IReadOnlyList<Notification>>(state => state.Notifications.Where(x => x.WorkspaceId == workspaceId && x.MissionId == missionId).OrderByDescending(x => x.CreatedAt).ToArray());

    public Task<IReadOnlyList<MemoryEntry>> SearchMemoryAsync(WorkspaceId workspaceId, string query, int limit, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<MemoryEntry>>(state => state.MemoryEntries
            .Where(x => x.WorkspaceId == workspaceId && (string.IsNullOrEmpty(query) || x.Content.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Categories.Any(c => c.Contains(query, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.CreatedAt).Take(limit).ToArray());

    public Task AddWorkspaceAsync(Workspace value, CancellationToken token) => MutateAsync(state => state.Workspaces.Add(value), token);
    public Task AddInboxAsync(Inbox value, CancellationToken token) => MutateAsync(state => state.Inboxes.Add(value), token);
    public Task AddNormalizedContentAsync(NormalizedContent value, CancellationToken token) => MutateAsync(state => { state.NormalizedContents.RemoveAll(x => x.WorkspaceId == value.WorkspaceId && x.ItemId == value.ItemId); state.NormalizedContents.Add(value); }, token);
    public Task AddMemoryEntryAsync(MemoryEntry value, CancellationToken token) => MutateAsync(state => state.MemoryEntries.Add(value), token);
    public Task AddMissionAsync(Mission value, CancellationToken token) => MutateAsync(state => state.Missions.Add(value), token);
    public Task AddMissionRunAsync(MissionRun value, CancellationToken token) => MutateAsync(state => state.MissionRuns.Add(value), token);
    public Task AddNotificationAsync(Notification value, CancellationToken token) => MutateAsync(state => state.Notifications.Add(value), token);
    public Task AddAuditEntryAsync(AuditEntry value, CancellationToken token) => MutateAsync(state => state.AuditEntries.Add(value), token);

    public Task AddItemAsync(Item item, RawContent raw, CancellationToken token) => MutateAsync(state => { state.Items.Add(item); state.RawContents.Add(raw); }, token);
    public Task SetItemStatusAsync(WorkspaceId workspaceId, ItemId itemId, ItemStatus status, string? error, CancellationToken token) => MutateAsync(state =>
    {
        var index = state.Items.FindIndex(x => x.WorkspaceId == workspaceId && x.Id == itemId);
        if (index >= 0) state.Items[index] = state.Items[index] with { Status = status, Error = error };
    }, token);
    public Task UpdateMissionAsync(Mission value, CancellationToken token) => MutateAsync(state =>
    {
        var index = state.Missions.FindIndex(x => x.WorkspaceId == value.WorkspaceId && x.Id == value.Id);
        if (index >= 0) state.Missions[index] = value;
    }, token);
    public Task UpdateMissionRunAsync(MissionRun value, CancellationToken token) => MutateAsync(state =>
    {
        var index = state.MissionRuns.FindIndex(x => x.WorkspaceId == value.WorkspaceId && x.Id == value.Id);
        if (index >= 0) state.MissionRuns[index] = value;
    }, token);

    private Task<T> ReadAsync<T>(Func<PlatformState, T> read)
    {
        lock (Gate) return Task.FromResult(read(State));
    }

    private async Task MutateAsync(Action<PlatformState> mutate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Gate) mutate(State);
        await ChangedAsync(cancellationToken);
    }
}

public sealed class PlatformState
{
    public List<Workspace> Workspaces { get; init; } = [];
    public List<Inbox> Inboxes { get; init; } = [];
    public List<Item> Items { get; init; } = [];
    public List<RawContent> RawContents { get; init; } = [];
    public List<NormalizedContent> NormalizedContents { get; init; } = [];
    public List<MemoryEntry> MemoryEntries { get; init; } = [];
    public List<Mission> Missions { get; init; } = [];
    public List<MissionRun> MissionRuns { get; init; } = [];
    public List<Notification> Notifications { get; init; } = [];
    public List<AuditEntry> AuditEntries { get; init; } = [];
}
