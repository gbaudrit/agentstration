using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Work.Storage.Sqlite;

public sealed class SqliteWorkplaceRepository(IDbContextFactory<WorkDbContext> contextFactory) : IWorkplaceRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await WorkDashboardSchema.EnsureAsync(context, cancellationToken);
        await WorkEntrySchema.EnsureAsync(context, cancellationToken);
    }

    public async Task UpsertDashboardAsync(WorkplaceDashboard dashboard, CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(dashboard);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await UpsertDashboardAsync(context, dashboard, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ReplaceDefaultDashboardAsync(WorkplaceDashboard dashboard, CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(dashboard);
        if (!dashboard.IsDefault) throw new ArgumentException("The replacement Dashboard must be marked as default.", nameof(dashboard));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var workspaceKey = dashboard.WorkspaceId.ToString();
        var documents = await context.Dashboards.Where(value => value.WorkspaceId == workspaceKey).ToArrayAsync(cancellationToken);
        foreach (var document in documents)
        {
            var current = Deserialize<WorkplaceDashboard>(document.Payload);
            if (current.Id == dashboard.Id || !current.IsDefault) continue;
            var demoted = current with
            {
                IsDefault = false,
                Version = checked(current.Version + 1),
                PublishedAt = dashboard.PublishedAt
            };
            document.Payload = JsonSerializer.Serialize(demoted, JsonOptions);
        }
        await UpsertDashboardAsync(context, dashboard, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkplaceDashboard>> ListDashboardsAsync(WorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workspaceKey = workspaceId.ToString();
        var payloads = await context.Dashboards.AsNoTracking()
            .Where(value => value.WorkspaceId == workspaceKey)
            .OrderBy(value => value.Name)
            .Select(value => value.Payload)
            .ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<WorkplaceDashboard>).ToArray();
    }

    public async Task<WorkplaceDashboard?> GetDashboardAsync(WorkspaceId workspaceId, DashboardId dashboardId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var key = DashboardStorageKey(workspaceId, dashboardId);
        var payload = await context.Dashboards.AsNoTracking().Where(value => value.Id == key).Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : Deserialize<WorkplaceDashboard>(payload);
    }

    public async Task DeleteDashboardAsync(WorkspaceId workspaceId, DashboardId dashboardId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Dashboards.SingleOrDefaultAsync(value => value.Id == DashboardStorageKey(workspaceId, dashboardId), cancellationToken);
        if (document is null) return;
        context.Dashboards.Remove(document);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertDashboardDraftAsync(WorkplaceDashboardDraft draft, CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(draft);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var key = DashboardStorageKey(draft.WorkspaceId, draft.Id);
        var workspaceKey = draft.WorkspaceId.ToString();
        var document = await context.DashboardDrafts.SingleOrDefaultAsync(value => value.Id == key, cancellationToken);
        if (document is null)
            context.DashboardDrafts.Add(new WorkplaceDashboardDraftDocument { Id = key, WorkspaceId = workspaceKey, Name = draft.Name, Payload = JsonSerializer.Serialize(draft, JsonOptions) });
        else
        {
            document.Name = draft.Name;
            document.Payload = JsonSerializer.Serialize(draft, JsonOptions);
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkplaceDashboardDraft>> ListDashboardDraftsAsync(WorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workspaceKey = workspaceId.ToString();
        var payloads = await context.DashboardDrafts.AsNoTracking()
            .Where(value => value.WorkspaceId == workspaceKey)
            .OrderBy(value => value.Name)
            .Select(value => value.Payload)
            .ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<WorkplaceDashboardDraft>).ToArray();
    }

    public async Task<WorkplaceDashboardDraft?> GetDashboardDraftAsync(WorkspaceId workspaceId, DashboardId dashboardId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payload = await context.DashboardDrafts.AsNoTracking()
            .Where(value => value.Id == DashboardStorageKey(workspaceId, dashboardId))
            .Select(value => value.Payload)
            .SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : Deserialize<WorkplaceDashboardDraft>(payload);
    }

    public async Task DeleteDashboardDraftAsync(WorkspaceId workspaceId, DashboardId dashboardId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.DashboardDrafts.SingleOrDefaultAsync(value => value.Id == DashboardStorageKey(workspaceId, dashboardId), cancellationToken);
        if (document is null) return;
        context.DashboardDrafts.Remove(document);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertEntryAsync(EntryResource entry, CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(entry);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var key = EntryStorageKey(entry.WorkspaceId, entry.Id);
        var document = await context.Entries.SingleOrDefaultAsync(value => value.Id == key, cancellationToken);
        if (document is null) context.Entries.Add(new EntryDocument { Id = key, WorkspaceId = entry.WorkspaceId.ToString(), Name = entry.Name, Payload = JsonSerializer.Serialize(entry, JsonOptions) });
        else { document.Name = entry.Name; document.Payload = JsonSerializer.Serialize(entry, JsonOptions); }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EntryResource>> ListEntriesAsync(WorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.Entries.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.ToString()).OrderBy(value => value.Name).Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<EntryResource>).ToArray();
    }

    public async Task<EntryResource?> GetEntryAsync(WorkspaceId workspaceId, EntryId entryId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var key = EntryStorageKey(workspaceId, entryId);
        var payload = await context.Entries.AsNoTracking().Where(value => value.Id == key).Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : Deserialize<EntryResource>(payload);
    }

    public async Task DeleteEntryAsync(WorkspaceId workspaceId, EntryId entryId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var key = EntryStorageKey(workspaceId, entryId);
        var document = await context.Entries.SingleOrDefaultAsync(value => value.Id == key, cancellationToken);
        if (document is not null)
        {
            context.Entries.Remove(document);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task UpsertEntryDraftAsync(EntryDraft draft, CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(draft);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var key = EntryStorageKey(draft.WorkspaceId, draft.Id);
        var document = await context.EntryDrafts.SingleOrDefaultAsync(value => value.Id == key, cancellationToken);
        if (document is null) context.EntryDrafts.Add(new EntryDraftDocument { Id = key, WorkspaceId = draft.WorkspaceId.ToString(), Name = draft.Name, Payload = JsonSerializer.Serialize(draft, JsonOptions) });
        else { document.Name = draft.Name; document.Payload = JsonSerializer.Serialize(draft, JsonOptions); }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EntryDraft>> ListEntryDraftsAsync(WorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.EntryDrafts.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.ToString()).OrderBy(value => value.Name).Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<EntryDraft>).ToArray();
    }

    public async Task<EntryDraft?> GetEntryDraftAsync(WorkspaceId workspaceId, EntryId entryId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var key = EntryStorageKey(workspaceId, entryId);
        var payload = await context.EntryDrafts.AsNoTracking().Where(value => value.Id == key).Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : Deserialize<EntryDraft>(payload);
    }

    public async Task DeleteEntryDraftAsync(WorkspaceId workspaceId, EntryId entryId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var key = EntryStorageKey(workspaceId, entryId);
        var document = await context.EntryDrafts.SingleOrDefaultAsync(value => value.Id == key, cancellationToken);
        if (document is not null)
        {
            context.EntryDrafts.Remove(document);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<WorkplaceInteraction>> ListEntryInteractionsAsync(WorkspaceId workspaceId, EntryId entryId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.Interactions.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.ToString()).Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<WorkplaceInteraction>).Where(value => value.EntryId == entryId).ToArray();
    }

    public async Task CreateInteractionAsync(WorkplaceInteraction interaction, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Interactions.Add(ToDocument(interaction));
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new WorkplaceConcurrencyException(exception.InnerException?.Message ?? exception.Message); }
    }

    public async Task<WorkplaceInteraction?> GetInteractionAsync(WorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payload = await context.Interactions.AsNoTracking()
            .Where(value => value.Id == interactionId.ToString() && value.WorkspaceId == workspaceId.ToString())
            .Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : Deserialize<WorkplaceInteraction>(payload);
    }

    public async Task<IReadOnlyList<WorkplaceInteraction>> ListInteractionsAsync(WorkspaceId workspaceId, int take, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.Interactions.AsNoTracking()
            .Where(value => value.WorkspaceId == workspaceId.ToString())
            .OrderByDescending(value => value.LastActivityAt)
            .Take(Math.Clamp(take, 1, 100))
            .Select(value => value.Payload)
            .ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<WorkplaceInteraction>).ToArray();
    }

    public async Task SaveInteractionAsync(WorkplaceInteraction interaction, long expectedVersion, CancellationToken cancellationToken)
    {
        if (interaction.Version <= expectedVersion) throw new WorkplaceConcurrencyException("The Interaction version must increase before it is saved.");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Interactions.SingleOrDefaultAsync(value => value.Id == interaction.Id.ToString() && value.WorkspaceId == interaction.WorkspaceId.ToString(), cancellationToken)
            ?? throw new KeyNotFoundException($"Interaction '{interaction.Id}' was not found in Workspace '{interaction.WorkspaceId}'.");
        if (document.Version != expectedVersion) throw new WorkplaceConcurrencyException("The supplied Interaction version is stale.");
        document.Status = interaction.Status;
        document.LastActivityAt = interaction.LastActivityAt;
        document.Version = interaction.Version;
        document.Payload = JsonSerializer.Serialize(interaction, JsonOptions);
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception) { throw new WorkplaceConcurrencyException(exception.Message); }
    }

    public async Task AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.ConversationMessages.Add(new ConversationMessageDocument { Id = message.Id.ToString(), WorkspaceId = message.WorkspaceId.ToString(), InteractionId = message.InteractionId.ToString(), WorkTaskId = message.WorkTaskId?.ToString(), CreatedAt = message.CreatedAt, Payload = JsonSerializer.Serialize(message, JsonOptions) });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(WorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.ConversationMessages.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.ToString() && value.InteractionId == interactionId.ToString()).Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<ConversationMessage>).OrderBy(value => value.CreatedAt).ToArray();
    }

    public async Task CreatePendingActionAsync(PendingAction action, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.PendingActions.Add(new PendingActionDocument { Id = action.Id.ToString(), WorkspaceId = action.WorkspaceId.ToString(), InteractionId = action.InteractionId?.ToString() ?? string.Empty, WorkTaskId = action.WorkTaskId?.ToString(), Status = action.Status, ResumeTokenHash = action.ResumeTokenHash, CreatedAt = action.CreatedAt, Version = action.Version, Payload = JsonSerializer.Serialize(action, JsonOptions) });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PendingAction?> GetPendingActionAsync(WorkspaceId workspaceId, PendingActionId actionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payload = await context.PendingActions.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.ToString() && value.Id == actionId.ToString()).Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : Deserialize<PendingAction>(payload);
    }

    public async Task<IReadOnlyList<PendingAction>> ListPendingActionsAsync(WorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.PendingActions.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.ToString() && value.InteractionId == interactionId.ToString()).Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<PendingAction>).OrderBy(value => value.CreatedAt).ToArray();
    }

    public async Task<IReadOnlyList<PendingAction>> ListPendingActionsForTaskAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.PendingActions.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.ToString() && value.WorkTaskId == taskId.ToString()).Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<PendingAction>).OrderBy(value => value.CreatedAt).ToArray();
    }

    public async Task SavePendingActionAsync(PendingAction action, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.PendingActions.SingleOrDefaultAsync(value => value.WorkspaceId == action.WorkspaceId.ToString() && value.Id == action.Id.ToString(), cancellationToken) ?? throw new KeyNotFoundException($"Pending action '{action.Id}' was not found.");
        if (document.Version != expectedVersion) throw new WorkplaceConcurrencyException("The PendingAction version is stale.");
        document.Status = action.Status; document.Version = action.Version; document.InteractionId = action.InteractionId?.ToString() ?? string.Empty; document.WorkTaskId = action.WorkTaskId?.ToString(); document.Payload = JsonSerializer.Serialize(action, JsonOptions);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddActivityAsync(WorkTaskActivity activity, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.WorkTaskActivities.Add(new WorkTaskActivityDocument { Id = activity.Id.ToString(), WorkspaceId = activity.WorkspaceId.ToString(), WorkTaskId = activity.WorkTaskId.ToString(), CreatedAt = activity.CreatedAt, Payload = JsonSerializer.Serialize(activity, JsonOptions) }); await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkTaskActivity>> ListActivitiesAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var payloads = await context.WorkTaskActivities.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.ToString() && value.WorkTaskId == taskId.ToString()).Select(value => value.Payload).ToArrayAsync(cancellationToken); return payloads.Select(Deserialize<WorkTaskActivity>).OrderBy(value => value.CreatedAt).ToArray();
    }

    public async Task AddResultAsync(WorkTaskResult result, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); context.WorkTaskResults.Add(new WorkTaskResultDocument { Id = result.Id.ToString(), WorkspaceId = result.WorkspaceId.ToString(), WorkTaskId = result.WorkTaskId.ToString(), CreatedAt = result.CreatedAt, Payload = JsonSerializer.Serialize(result, JsonOptions) }); await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkTaskResult>> ListResultsAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var payloads = await context.WorkTaskResults.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.ToString() && value.WorkTaskId == taskId.ToString()).Select(value => value.Payload).ToArrayAsync(cancellationToken); return payloads.Select(Deserialize<WorkTaskResult>).OrderBy(value => value.CreatedAt).ToArray();
    }

    public async Task AddArtifactAsync(WorkTaskArtifact artifact, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); context.WorkTaskArtifacts.Add(new WorkTaskArtifactDocument { Id = artifact.Id.ToString(), WorkspaceId = artifact.WorkspaceId.ToString(), WorkTaskId = artifact.WorkTaskId.ToString(), CreatedAt = artifact.CreatedAt, Payload = JsonSerializer.Serialize(artifact, JsonOptions) }); await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkTaskArtifact>> ListArtifactsAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var payloads = await context.WorkTaskArtifacts.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.ToString() && value.WorkTaskId == taskId.ToString()).Select(value => value.Payload).ToArrayAsync(cancellationToken); return payloads.Select(Deserialize<WorkTaskArtifact>).OrderBy(value => value.CreatedAt).ToArray();
    }

    public async Task<WorkTaskArtifact?> GetArtifactAsync(WorkspaceId workspaceId, WorkTaskId taskId, WorkTaskArtifactId artifactId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var payload = await context.WorkTaskArtifacts.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.ToString() && value.WorkTaskId == taskId.ToString() && value.Id == artifactId.ToString()).Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken); return payload is null ? null : Deserialize<WorkTaskArtifact>(payload);
    }

    public async Task CreateNotificationAsync(WorkNotification notification, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); context.WorkNotifications.Add(new WorkNotificationDocument { Id = notification.Id.ToString(), WorkspaceId = notification.WorkspaceId.ToString(), Kind = notification.Kind, CreatedAt = notification.CreatedAt, ReadAt = notification.ReadAt, Version = notification.Version, Payload = JsonSerializer.Serialize(notification, JsonOptions) }); await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkNotification>> ListNotificationsAsync(WorkspaceId workspaceId, bool? unreadOnly, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var query = context.WorkNotifications.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.ToString()); if (unreadOnly == true) query = query.Where(value => value.ReadAt == null); var payloads = await query.Select(value => value.Payload).ToArrayAsync(cancellationToken); return payloads.Select(Deserialize<WorkNotification>).OrderByDescending(value => value.CreatedAt).ToArray();
    }

    public async Task<WorkNotification?> GetNotificationAsync(WorkspaceId workspaceId, WorkNotificationId notificationId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var payload = await context.WorkNotifications.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.ToString() && value.Id == notificationId.ToString()).Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken); return payload is null ? null : Deserialize<WorkNotification>(payload);
    }

    public async Task SaveNotificationAsync(WorkNotification notification, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var document = await context.WorkNotifications.SingleOrDefaultAsync(value => value.WorkspaceId == notification.WorkspaceId.ToString() && value.Id == notification.Id.ToString(), cancellationToken) ?? throw new KeyNotFoundException($"Notification '{notification.Id}' was not found."); if (document.Version != expectedVersion) throw new WorkplaceConcurrencyException("The notification version is stale."); document.ReadAt = notification.ReadAt; document.Version = notification.Version; document.Payload = JsonSerializer.Serialize(notification, JsonOptions); await context.SaveChangesAsync(cancellationToken);
    }

    private static InteractionDocument ToDocument(WorkplaceInteraction interaction) => new()
    {
        Id = interaction.Id.ToString(),
        WorkspaceId = interaction.WorkspaceId.ToString(),
        EntryId = interaction.EntryId.Value,
        Status = interaction.Status,
        LastActivityAt = interaction.LastActivityAt,
        Version = interaction.Version,
        Payload = JsonSerializer.Serialize(interaction, JsonOptions)
    };

    private static T Deserialize<T>(string payload) => JsonSerializer.Deserialize<T>(payload, JsonOptions)
        ?? throw new InvalidOperationException($"Stored {typeof(T).Name} document is invalid.");
    private static async Task UpsertDashboardAsync(WorkDbContext context, WorkplaceDashboard dashboard, CancellationToken cancellationToken)
    {
        var key = DashboardStorageKey(dashboard.WorkspaceId, dashboard.Id);
        var workspaceKey = dashboard.WorkspaceId.ToString();
        var document = await context.Dashboards.SingleOrDefaultAsync(value => value.Id == key, cancellationToken);
        if (document is null)
            context.Dashboards.Add(new WorkplaceDashboardDocument { Id = key, WorkspaceId = workspaceKey, Name = dashboard.Name, Payload = JsonSerializer.Serialize(dashboard, JsonOptions) });
        else
        {
            document.Name = dashboard.Name;
            document.Payload = JsonSerializer.Serialize(dashboard, JsonOptions);
        }
    }
    private static string DashboardStorageKey(WorkspaceId workspaceId, DashboardId dashboardId) =>
        $"{workspaceId}:{dashboardId.Value}";
    private static string EntryStorageKey(WorkspaceId workspaceId, EntryId entryId) =>
        $"{workspaceId}:{StorageKey(entryId.Namespace, entryId.Value)}";
    private static string StorageKey(ResourceNamespace @namespace, string name) => $"{@namespace.Value}:{name}";
}

