using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Work.Storage.Sqlite;

public sealed class SqliteWorkItemRepository(IDbContextFactory<WorkDbContext> contextFactory) : IWorkItemRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task<StoredWorkItem> CreateAsync(WorkItem workItem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.WorkItems.Add(ToDocument(workItem));
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new WorkItemConcurrencyException(exception.InnerException?.Message ?? exception.Message); }
        return Stored(workItem);
    }

    public async Task<StoredWorkItem?> GetAsync(WorkspaceId workspaceId, WorkItemId id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workspaceKey = workspaceId.ToString();
        var document = await context.WorkItems.AsNoTracking().SingleOrDefaultAsync(value => value.WorkspaceId == workspaceKey && value.Id == id.ToString(), cancellationToken);
        return document is null ? null : FromDocument(document);
    }

    public async Task<StoredWorkItem> SaveAsync(WorkItem workItem, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        if (workItem.Version <= expectedVersion) throw new WorkItemConcurrencyException("The work item version must increase before it is saved.");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workspaceKey = workItem.WorkspaceId.ToString();
        var document = await context.WorkItems.SingleOrDefaultAsync(value => value.WorkspaceId == workspaceKey && value.Id == workItem.Id.ToString(), cancellationToken)
            ?? throw new KeyNotFoundException($"Work item '{workItem.Id}' was not found.");
        if (document.Version != expectedVersion) throw new WorkItemConcurrencyException("The supplied version does not match the current work item version.");
        Apply(document, workItem);
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception) { throw new WorkItemConcurrencyException(exception.Message); }
        return Stored(workItem);
    }

    public async Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Take, 1);
        var take = Math.Min(query.Take, 200);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workspaceKey = query.WorkspaceId.ToString();
        IQueryable<WorkItemDocument> documents = context.WorkItems.AsNoTracking().Where(value => value.WorkspaceId == workspaceKey);
        if (query.Status is not null && !query.OperationalTasks) documents = documents.Where(value => value.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Type)) documents = documents.Where(value => value.Type == query.Type);
        if (!string.IsNullOrWhiteSpace(query.RequesterIdentity)) documents = documents.Where(value => value.RequesterIdentity == query.RequesterIdentity);
        if (!string.IsNullOrWhiteSpace(query.AgentId)) documents = documents.Where(value => value.RequestedAgentId == query.AgentId || value.SelectedAgentId == query.AgentId);
        if (query.CreatedFrom is not null) documents = documents.Where(value => value.CreatedAt >= query.CreatedFrom);
        if (query.CreatedTo is not null) documents = documents.Where(value => value.CreatedAt <= query.CreatedTo);
        if (query.UpdatedFrom is not null) documents = documents.Where(value => value.UpdatedAt >= query.UpdatedFrom);
        if (query.UpdatedTo is not null) documents = documents.Where(value => value.UpdatedAt <= query.UpdatedTo);
        if (!string.IsNullOrWhiteSpace(query.InteractionId)) documents = documents.Where(value => value.InteractionId == query.InteractionId);
        if (!string.IsNullOrWhiteSpace(query.EntryId)) documents = documents.Where(value => value.EntryId == query.EntryId);
        if (!string.IsNullOrWhiteSpace(query.AnchorTaskId)) documents = documents.Where(value => value.AnchorTaskId == query.AnchorTaskId);
        if (query.IsContinuation == true) documents = documents.Where(value => value.AnchorTaskId != null);
        if (query.IsContinuation == false) documents = documents.Where(value => value.AnchorTaskId == null && value.WorkspaceId != null);
        if (query.OperationalTasks)
        {
            documents = documents.Where(value => value.AnchorTaskId == null && value.WorkspaceId != null);
            if (query.Status is not null)
            {
                documents = documents.Where(anchor =>
                    context.WorkItems.Where(child => child.WorkspaceId == workspaceKey && child.AnchorTaskId == anchor.Id)
                        .OrderByDescending(child => child.UpdatedAt)
                        .Select(child => (WorkItemStatus?)child.Status)
                        .FirstOrDefault() == query.Status
                    || !context.WorkItems.Any(child => child.WorkspaceId == workspaceKey && child.AnchorTaskId == anchor.Id) && anchor.Status == query.Status);
            }
            if (query.HasPendingAction is not null)
            {
                documents = query.HasPendingAction.Value
                    ? documents.Where(value => context.PendingActions.Any(action => action.WorkspaceId == workspaceKey && action.WorkTaskId == value.Id && action.Status == PendingActionStatus.Pending))
                    : documents.Where(value => !context.PendingActions.Any(action => action.WorkspaceId == workspaceKey && action.WorkTaskId == value.Id && action.Status == PendingActionStatus.Pending));
            }
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            documents = documents.Where(value => EF.Functions.Like(value.Id, pattern)
                || value.Title != null && EF.Functions.Like(value.Title, pattern)
                || value.Description != null && EF.Functions.Like(value.Description, pattern)
                || value.InteractionId != null && EF.Functions.Like(value.InteractionId, pattern));
        }
        var totalCount = await documents.CountAsync(cancellationToken);
        documents = Order(documents, query.SortBy, query.SortDirection);
        var page = await documents.Skip(query.Skip).Take(take + 1).ToArrayAsync(cancellationToken);
        return new WorkItemPage(page.Take(take).Select(FromDocument).ToArray(), page.Length > take, totalCount);
    }

    private static IQueryable<WorkItemDocument> Order(IQueryable<WorkItemDocument> query, WorkItemSortField field, WorkItemSortDirection direction) => (field, direction) switch
    {
        (WorkItemSortField.UpdatedAt, WorkItemSortDirection.Ascending) => query.OrderBy(value => value.UpdatedAt).ThenBy(value => value.Id),
        (WorkItemSortField.UpdatedAt, WorkItemSortDirection.Descending) => query.OrderByDescending(value => value.UpdatedAt).ThenBy(value => value.Id),
        (WorkItemSortField.CreatedAt, WorkItemSortDirection.Ascending) => query.OrderBy(value => value.CreatedAt).ThenBy(value => value.Id),
        _ => query.OrderByDescending(value => value.CreatedAt).ThenBy(value => value.Id)
    };

    private static WorkItemDocument ToDocument(WorkItem item) => new()
    {
        Id = item.Id.ToString(),
        Type = item.Type,
        Status = item.Status,
        RequesterIdentity = item.RequesterIdentity,
        RequestedAgentId = item.RequestedAgentId,
        SelectedAgentId = item.SelectedAgentId,
        Title = item.Title,
        Description = item.Description,
        WorkspaceId = item.WorkspaceId.ToString(),
        InteractionId = Metadata(item, "workplace.interactionId"),
        EntryId = Metadata(item, "workplace.entryId"),
        AnchorTaskId = Metadata(item, "workplace.taskId"),
        FlowRunId = Metadata(item, "flowRunId"),
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        Version = item.Version,
        Payload = JsonSerializer.Serialize(item.ToSnapshot(), JsonOptions)
    };

    private static void Apply(WorkItemDocument document, WorkItem item)
    {
        var updated = ToDocument(item);
        document.Type = updated.Type;
        document.Status = updated.Status;
        document.RequesterIdentity = updated.RequesterIdentity;
        document.RequestedAgentId = updated.RequestedAgentId;
        document.SelectedAgentId = updated.SelectedAgentId;
        document.Title = updated.Title;
        document.Description = updated.Description;
        document.WorkspaceId = updated.WorkspaceId;
        document.InteractionId = updated.InteractionId;
        document.EntryId = updated.EntryId;
        document.AnchorTaskId = updated.AnchorTaskId;
        document.FlowRunId = updated.FlowRunId;
        document.UpdatedAt = updated.UpdatedAt;
        document.Version = updated.Version;
        document.Payload = updated.Payload;
    }

    private static StoredWorkItem FromDocument(WorkItemDocument document)
    {
        var snapshot = JsonSerializer.Deserialize<WorkItemSnapshot>(document.Payload, JsonOptions)
            ?? throw new InvalidOperationException($"Stored work item '{document.Id}' is invalid.");
        return Stored(WorkItem.Restore(snapshot));
    }

    private static StoredWorkItem Stored(WorkItem item) => new(item, ETag(item.Version), item.UpdatedAt);
    private static string? Metadata(WorkItem item, string name) => item.Metadata.TryGetValue(name, out var value) ? value : null;

    private static string ETag(long version) => $"\"{version}\"";
}
