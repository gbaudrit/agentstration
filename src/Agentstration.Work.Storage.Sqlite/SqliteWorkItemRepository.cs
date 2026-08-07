using System.Text.Json;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Work.Storage.Sqlite;

public sealed class WorkDbContext(DbContextOptions<WorkDbContext> options) : DbContext(options)
{
    internal DbSet<WorkItemDocument> WorkItems => Set<WorkItemDocument>();
    internal DbSet<WorkplaceWorkspaceDocument> Workspaces => Set<WorkplaceWorkspaceDocument>();
    internal DbSet<WorkplaceWorkspaceDraftDocument> WorkspaceDrafts => Set<WorkplaceWorkspaceDraftDocument>();
    internal DbSet<EntryDocument> Entries => Set<EntryDocument>();
    internal DbSet<EntryDraftDocument> EntryDrafts => Set<EntryDraftDocument>();
    internal DbSet<InteractionDocument> Interactions => Set<InteractionDocument>();
    internal DbSet<ConversationMessageDocument> ConversationMessages => Set<ConversationMessageDocument>();
    internal DbSet<PendingActionDocument> PendingActions => Set<PendingActionDocument>();
    internal DbSet<WorkNotificationDocument> WorkNotifications => Set<WorkNotificationDocument>();
    internal DbSet<WorkTaskActivityDocument> WorkTaskActivities => Set<WorkTaskActivityDocument>();
    internal DbSet<WorkTaskResultDocument> WorkTaskResults => Set<WorkTaskResultDocument>();
    internal DbSet<WorkTaskArtifactDocument> WorkTaskArtifacts => Set<WorkTaskArtifactDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<WorkItemDocument>();
        item.ToTable("WorkItems");
        item.HasKey(value => value.Id);
        item.Property(value => value.Id).HasMaxLength(36);
        item.Property(value => value.Type).HasMaxLength(128);
        item.Property(value => value.Status).HasConversion<string>().HasMaxLength(32);
        item.Property(value => value.RequesterIdentity).HasMaxLength(256);
        item.Property(value => value.RequestedAgentId).HasMaxLength(1024);
        item.Property(value => value.SelectedAgentId).HasMaxLength(1024);
        item.Property(value => value.Title).HasMaxLength(512);
        item.Property(value => value.WorkspaceId).HasMaxLength(512);
        item.Property(value => value.InteractionId).HasMaxLength(36);
        item.Property(value => value.EntryId).HasMaxLength(512);
        item.Property(value => value.AnchorTaskId).HasMaxLength(36);
        item.Property(value => value.FlowRunId).HasMaxLength(128);
        item.Property(value => value.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        item.Property(value => value.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        item.Property(value => value.Version).IsConcurrencyToken();
        item.HasIndex(value => new { value.Status, value.CreatedAt });
        item.HasIndex(value => new { value.Type, value.CreatedAt });
        item.HasIndex(value => new { value.RequesterIdentity, value.CreatedAt });
        item.HasIndex(value => new { value.SelectedAgentId, value.CreatedAt });
        item.HasIndex(value => new { value.WorkspaceId, value.Status, value.UpdatedAt });
        item.HasIndex(value => new { value.WorkspaceId, value.InteractionId, value.UpdatedAt });
        item.HasIndex(value => new { value.WorkspaceId, value.AnchorTaskId, value.UpdatedAt });
        item.HasIndex(value => value.FlowRunId);

        var workspace = modelBuilder.Entity<WorkplaceWorkspaceDocument>();
        workspace.ToTable("WorkplaceWorkspaces");
        workspace.HasKey(value => value.Id);
        workspace.Property(value => value.Id).HasMaxLength(512);
        workspace.Property(value => value.Name).HasMaxLength(128);

        var workspaceDraft = modelBuilder.Entity<WorkplaceWorkspaceDraftDocument>();
        workspaceDraft.ToTable("WorkplaceWorkspaceDrafts");
        workspaceDraft.HasKey(value => value.Id);
        workspaceDraft.Property(value => value.Id).HasMaxLength(512);
        workspaceDraft.Property(value => value.Name).HasMaxLength(128);

        var entry = modelBuilder.Entity<EntryDocument>();
        entry.ToTable("Entries");
        entry.HasKey(value => value.Id);
        entry.Property(value => value.Id).HasMaxLength(512);
        entry.Property(value => value.Name).HasMaxLength(128);

        var entryDraft = modelBuilder.Entity<EntryDraftDocument>();
        entryDraft.ToTable("EntryDrafts");
        entryDraft.HasKey(value => value.Id);
        entryDraft.Property(value => value.Id).HasMaxLength(512);
        entryDraft.Property(value => value.Name).HasMaxLength(128);

        var interaction = modelBuilder.Entity<InteractionDocument>();
        interaction.ToTable("Interactions");
        interaction.HasKey(value => value.Id);
        interaction.Property(value => value.Id).HasMaxLength(36);
        interaction.Property(value => value.WorkspaceId).HasMaxLength(512);
        interaction.Property(value => value.EntryId).HasMaxLength(512);
        interaction.Property(value => value.Status).HasConversion<string>().HasMaxLength(32);
        interaction.Property(value => value.LastActivityAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        interaction.Property(value => value.Version).IsConcurrencyToken();
        interaction.HasIndex(value => new { value.WorkspaceId, value.LastActivityAt });

        ConfigureConversationMessage(modelBuilder.Entity<ConversationMessageDocument>());
        ConfigurePendingAction(modelBuilder.Entity<PendingActionDocument>());
        ConfigureNotification(modelBuilder.Entity<WorkNotificationDocument>());
        ConfigureTaskEntity(modelBuilder.Entity<WorkTaskActivityDocument>(), "WorkTaskActivities");
        ConfigureTaskEntity(modelBuilder.Entity<WorkTaskResultDocument>(), "WorkTaskResults");
        ConfigureTaskEntity(modelBuilder.Entity<WorkTaskArtifactDocument>(), "WorkTaskArtifacts");
    }

    private static void ConfigureConversationMessage(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<ConversationMessageDocument> entity)
    {
        entity.ToTable("ConversationMessages"); entity.HasKey(value => value.Id);
        entity.Property(value => value.WorkspaceId).HasMaxLength(512); entity.Property(value => value.InteractionId).HasMaxLength(36);
        entity.Property(value => value.WorkTaskId).HasMaxLength(36); entity.HasIndex(value => new { value.WorkspaceId, value.InteractionId, value.CreatedAt });
    }

    private static void ConfigurePendingAction(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PendingActionDocument> entity)
    {
        entity.ToTable("PendingActions"); entity.HasKey(value => value.Id); entity.Property(value => value.WorkspaceId).HasMaxLength(512);
        entity.Property(value => value.InteractionId).HasMaxLength(36); entity.Property(value => value.WorkTaskId).HasMaxLength(36);
        entity.Property(value => value.Status).HasConversion<string>().HasMaxLength(32); entity.Property(value => value.ResumeTokenHash).HasMaxLength(128);
        entity.HasIndex(value => new { value.WorkspaceId, value.Status }); entity.HasIndex(value => new { value.InteractionId, value.CreatedAt });
        entity.HasIndex(value => value.ResumeTokenHash).IsUnique();
    }

    private static void ConfigureNotification(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<WorkNotificationDocument> entity)
    {
        entity.ToTable("WorkNotifications"); entity.HasKey(value => value.Id); entity.Property(value => value.WorkspaceId).HasMaxLength(512);
        entity.Property(value => value.Kind).HasConversion<string>().HasMaxLength(32); entity.HasIndex(value => new { value.WorkspaceId, value.CreatedAt });
        entity.HasIndex(value => new { value.WorkspaceId, value.ReadAt }); entity.Property(value => value.Version).IsConcurrencyToken();
    }

    private static void ConfigureTaskEntity<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity, string table) where T : class, IWorkTaskEntityDocument
    {
        entity.ToTable(table); entity.HasKey(value => value.Id); entity.Property(value => value.WorkspaceId).HasMaxLength(512);
        entity.Property(value => value.WorkTaskId).HasMaxLength(36); entity.HasIndex(value => new { value.WorkspaceId, value.WorkTaskId, value.CreatedAt });
    }
}

internal sealed class WorkplaceWorkspaceDocument
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Payload { get; set; }
}

internal sealed class WorkplaceWorkspaceDraftDocument
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Payload { get; set; }
}

internal sealed class EntryDocument
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Payload { get; set; }
}

internal sealed class EntryDraftDocument
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Payload { get; set; }
}

internal sealed class InteractionDocument
{
    public required string Id { get; set; }
    public required string WorkspaceId { get; set; }
    public required string EntryId { get; set; }
    public InteractionStatus Status { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public long Version { get; set; }
    public required string Payload { get; set; }
}

internal sealed class ConversationMessageDocument
{
    public required string Id { get; set; } public required string WorkspaceId { get; set; } public required string InteractionId { get; set; }
    public string? WorkTaskId { get; set; } public DateTimeOffset CreatedAt { get; set; } public required string Payload { get; set; }
}

internal sealed class PendingActionDocument
{
    public required string Id { get; set; } public required string WorkspaceId { get; set; } public required string InteractionId { get; set; }
    public string? WorkTaskId { get; set; } public PendingActionStatus Status { get; set; } public required string ResumeTokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } public long Version { get; set; } public required string Payload { get; set; }
}

internal sealed class WorkNotificationDocument
{
    public required string Id { get; set; } public required string WorkspaceId { get; set; } public WorkNotificationKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset? ReadAt { get; set; } public long Version { get; set; }
    public required string Payload { get; set; }
}

internal interface IWorkTaskEntityDocument
{
    string Id { get; set; } string WorkspaceId { get; set; } string WorkTaskId { get; set; } DateTimeOffset CreatedAt { get; set; } string Payload { get; set; }
}
internal sealed class WorkTaskActivityDocument : IWorkTaskEntityDocument { public required string Id { get; set; } public required string WorkspaceId { get; set; } public required string WorkTaskId { get; set; } public DateTimeOffset CreatedAt { get; set; } public required string Payload { get; set; } }
internal sealed class WorkTaskResultDocument : IWorkTaskEntityDocument { public required string Id { get; set; } public required string WorkspaceId { get; set; } public required string WorkTaskId { get; set; } public DateTimeOffset CreatedAt { get; set; } public required string Payload { get; set; } }
internal sealed class WorkTaskArtifactDocument : IWorkTaskEntityDocument { public required string Id { get; set; } public required string WorkspaceId { get; set; } public required string WorkTaskId { get; set; } public DateTimeOffset CreatedAt { get; set; } public required string Payload { get; set; } }

internal sealed class WorkItemDocument
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public WorkItemStatus Status { get; set; }
    public string? RequesterIdentity { get; set; }
    public string? RequestedAgentId { get; set; }
    public string? SelectedAgentId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? WorkspaceId { get; set; }
    public string? InteractionId { get; set; }
    public string? EntryId { get; set; }
    public string? AnchorTaskId { get; set; }
    public string? FlowRunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
    public required string Payload { get; set; }
}

public sealed class SqliteWorkItemRepository(IDbContextFactory<WorkDbContext> contextFactory) : IWorkItemRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS WorkplaceWorkspaceDrafts (Id TEXT NOT NULL CONSTRAINT PK_WorkplaceWorkspaceDrafts PRIMARY KEY, Name TEXT NOT NULL, Payload TEXT NOT NULL);",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS EntryDrafts (Id TEXT NOT NULL CONSTRAINT PK_EntryDrafts PRIMARY KEY, Name TEXT NOT NULL, Payload TEXT NOT NULL);",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS WorkplaceWorkspaces (
                Id TEXT NOT NULL CONSTRAINT PK_WorkplaceWorkspaces PRIMARY KEY,
                Name TEXT NOT NULL,
                Payload TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Entries (
                Id TEXT NOT NULL CONSTRAINT PK_Entries PRIMARY KEY,
                Name TEXT NOT NULL,
                Payload TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Interactions (
                Id TEXT NOT NULL CONSTRAINT PK_Interactions PRIMARY KEY,
                WorkspaceId TEXT NOT NULL,
                EntryId TEXT NOT NULL,
                Status TEXT NOT NULL,
                LastActivityAt INTEGER NOT NULL,
                Version INTEGER NOT NULL,
                Payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Interactions_WorkspaceId_LastActivityAt ON Interactions (WorkspaceId, LastActivityAt);
            CREATE TABLE IF NOT EXISTS ConversationMessages (Id TEXT NOT NULL PRIMARY KEY, WorkspaceId TEXT NOT NULL, InteractionId TEXT NOT NULL, WorkTaskId TEXT NULL, CreatedAt TEXT NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_ConversationMessages_WorkspaceId_InteractionId_CreatedAt ON ConversationMessages (WorkspaceId, InteractionId, CreatedAt);
            CREATE TABLE IF NOT EXISTS PendingActions (Id TEXT NOT NULL PRIMARY KEY, WorkspaceId TEXT NOT NULL, InteractionId TEXT NOT NULL, WorkTaskId TEXT NULL, Status TEXT NOT NULL, ResumeTokenHash TEXT NOT NULL, CreatedAt TEXT NOT NULL, Version INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PendingActions_ResumeTokenHash ON PendingActions (ResumeTokenHash);
            CREATE INDEX IF NOT EXISTS IX_PendingActions_WorkspaceId_Status ON PendingActions (WorkspaceId, Status);
            CREATE INDEX IF NOT EXISTS IX_PendingActions_InteractionId_CreatedAt ON PendingActions (InteractionId, CreatedAt);
            CREATE TABLE IF NOT EXISTS WorkNotifications (Id TEXT NOT NULL PRIMARY KEY, WorkspaceId TEXT NOT NULL, Kind TEXT NOT NULL, CreatedAt TEXT NOT NULL, ReadAt TEXT NULL, Version INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorkNotifications_WorkspaceId_CreatedAt ON WorkNotifications (WorkspaceId, CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_WorkNotifications_WorkspaceId_ReadAt ON WorkNotifications (WorkspaceId, ReadAt);
            CREATE TABLE IF NOT EXISTS WorkTaskActivities (Id TEXT NOT NULL PRIMARY KEY, WorkspaceId TEXT NOT NULL, WorkTaskId TEXT NOT NULL, CreatedAt TEXT NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorkTaskActivities_WorkspaceId_WorkTaskId_CreatedAt ON WorkTaskActivities (WorkspaceId, WorkTaskId, CreatedAt);
            CREATE TABLE IF NOT EXISTS WorkTaskResults (Id TEXT NOT NULL PRIMARY KEY, WorkspaceId TEXT NOT NULL, WorkTaskId TEXT NOT NULL, CreatedAt TEXT NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorkTaskResults_WorkspaceId_WorkTaskId_CreatedAt ON WorkTaskResults (WorkspaceId, WorkTaskId, CreatedAt);
            CREATE TABLE IF NOT EXISTS WorkTaskArtifacts (Id TEXT NOT NULL PRIMARY KEY, WorkspaceId TEXT NOT NULL, WorkTaskId TEXT NOT NULL, CreatedAt TEXT NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorkTaskArtifacts_WorkspaceId_WorkTaskId_CreatedAt ON WorkTaskArtifacts (WorkspaceId, WorkTaskId, CreatedAt);
            """,
            cancellationToken);
        await EnsureOperationalColumnsAsync(context, cancellationToken);
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

    public async Task<StoredWorkItem?> GetAsync(WorkItemId id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.WorkItems.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id.ToString(), cancellationToken);
        return document is null ? null : FromDocument(document);
    }

    public async Task<StoredWorkItem> SaveAsync(WorkItem workItem, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        if (workItem.Version <= expectedVersion) throw new WorkItemConcurrencyException("The work item version must increase before it is saved.");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.WorkItems.SingleOrDefaultAsync(value => value.Id == workItem.Id.ToString(), cancellationToken)
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
        IQueryable<WorkItemDocument> documents = context.WorkItems.AsNoTracking();
        if (query.Status is not null && !query.OperationalTasks) documents = documents.Where(value => value.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Type)) documents = documents.Where(value => value.Type == query.Type);
        if (!string.IsNullOrWhiteSpace(query.RequesterIdentity)) documents = documents.Where(value => value.RequesterIdentity == query.RequesterIdentity);
        if (!string.IsNullOrWhiteSpace(query.AgentId)) documents = documents.Where(value => value.RequestedAgentId == query.AgentId || value.SelectedAgentId == query.AgentId);
        if (query.CreatedFrom is not null) documents = documents.Where(value => value.CreatedAt >= query.CreatedFrom);
        if (query.CreatedTo is not null) documents = documents.Where(value => value.CreatedAt <= query.CreatedTo);
        if (query.UpdatedFrom is not null) documents = documents.Where(value => value.UpdatedAt >= query.UpdatedFrom);
        if (query.UpdatedTo is not null) documents = documents.Where(value => value.UpdatedAt <= query.UpdatedTo);
        if (!string.IsNullOrWhiteSpace(query.WorkspaceId)) documents = documents.Where(value => value.WorkspaceId == query.WorkspaceId);
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
                    context.WorkItems.Where(child => child.AnchorTaskId == anchor.Id)
                        .OrderByDescending(child => child.UpdatedAt)
                        .Select(child => (WorkItemStatus?)child.Status)
                        .FirstOrDefault() == query.Status
                    || !context.WorkItems.Any(child => child.AnchorTaskId == anchor.Id) && anchor.Status == query.Status);
            }
            if (query.HasPendingAction is not null)
            {
                documents = query.HasPendingAction.Value
                    ? documents.Where(value => context.PendingActions.Any(action => action.WorkTaskId == value.Id && action.Status == PendingActionStatus.Pending))
                    : documents.Where(value => !context.PendingActions.Any(action => action.WorkTaskId == value.Id && action.Status == PendingActionStatus.Pending));
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
        WorkspaceId = Metadata(item, "workplace.workspaceId"),
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

    private static async Task EnsureOperationalColumnsAsync(WorkDbContext context, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('WorkItems');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        }
        var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Title"] = "TEXT NULL", ["Description"] = "TEXT NULL", ["WorkspaceId"] = "TEXT NULL",
            ["InteractionId"] = "TEXT NULL", ["EntryId"] = "TEXT NULL", ["AnchorTaskId"] = "TEXT NULL", ["FlowRunId"] = "TEXT NULL"
        };
        foreach (var definition in definitions.Where(value => !columns.Contains(value.Key)))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE WorkItems ADD COLUMN {definition.Key} {definition.Value};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var legacyDocuments = await context.WorkItems.Where(value => value.WorkspaceId == null && EF.Functions.Like(value.Payload, "%workplace.workspaceId%")).ToArrayAsync(cancellationToken);
        foreach (var document in legacyDocuments) Apply(document, FromDocument(document).Value);
        if (legacyDocuments.Length > 0) await context.SaveChangesAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_WorkItems_WorkspaceId_Status_UpdatedAt ON WorkItems (WorkspaceId, Status, UpdatedAt);", cancellationToken);
        await context.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_WorkItems_WorkspaceId_InteractionId_UpdatedAt ON WorkItems (WorkspaceId, InteractionId, UpdatedAt);", cancellationToken);
        await context.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_WorkItems_WorkspaceId_AnchorTaskId_UpdatedAt ON WorkItems (WorkspaceId, AnchorTaskId, UpdatedAt);", cancellationToken);
        await context.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_WorkItems_FlowRunId ON WorkItems (FlowRunId);", cancellationToken);
    }
    private static string ETag(long version) => $"\"{version}\"";
}

public sealed class SqliteWorkplaceRepository(IDbContextFactory<WorkDbContext> contextFactory) : IWorkplaceRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task UpsertWorkspaceAsync(WorkplaceWorkspace workspace, CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(workspace);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Workspaces.SingleOrDefaultAsync(value => value.Id == workspace.Id.Value, cancellationToken);
        if (document is null) context.Workspaces.Add(new WorkplaceWorkspaceDocument { Id = workspace.Id.Value, Name = workspace.Name, Payload = JsonSerializer.Serialize(workspace, JsonOptions) });
        else { document.Name = workspace.Name; document.Payload = JsonSerializer.Serialize(workspace, JsonOptions); }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkplaceWorkspace>> ListWorkspacesAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.Workspaces.AsNoTracking().OrderBy(value => value.Name).Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<WorkplaceWorkspace>).ToArray();
    }

    public async Task<WorkplaceWorkspace?> GetWorkspaceAsync(WorkplaceWorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payload = await context.Workspaces.AsNoTracking().Where(value => value.Id == workspaceId.Value).Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : Deserialize<WorkplaceWorkspace>(payload);
    }

    public async Task UpsertWorkspaceDraftAsync(WorkplaceWorkspaceDraft draft, CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(draft);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.WorkspaceDrafts.SingleOrDefaultAsync(value => value.Id == draft.Id.Value, cancellationToken);
        if (document is null) context.WorkspaceDrafts.Add(new WorkplaceWorkspaceDraftDocument { Id = draft.Id.Value, Name = draft.Name, Payload = JsonSerializer.Serialize(draft, JsonOptions) });
        else { document.Name = draft.Name; document.Payload = JsonSerializer.Serialize(draft, JsonOptions); }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkplaceWorkspaceDraft>> ListWorkspaceDraftsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.WorkspaceDrafts.AsNoTracking().OrderBy(value => value.Name).Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<WorkplaceWorkspaceDraft>).ToArray();
    }

    public async Task<WorkplaceWorkspaceDraft?> GetWorkspaceDraftAsync(WorkplaceWorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payload = await context.WorkspaceDrafts.AsNoTracking().Where(value => value.Id == workspaceId.Value).Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : Deserialize<WorkplaceWorkspaceDraft>(payload);
    }

    public async Task UpsertEntryAsync(EntryResource entry, CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(entry);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Entries.SingleOrDefaultAsync(value => value.Id == entry.Id.Value, cancellationToken);
        if (document is null) context.Entries.Add(new EntryDocument { Id = entry.Id.Value, Name = entry.Name, Payload = JsonSerializer.Serialize(entry, JsonOptions) });
        else { document.Name = entry.Name; document.Payload = JsonSerializer.Serialize(entry, JsonOptions); }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EntryResource>> ListEntriesAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.Entries.AsNoTracking().OrderBy(value => value.Name).Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<EntryResource>).ToArray();
    }

    public async Task<EntryResource?> GetEntryAsync(EntryId entryId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payload = await context.Entries.AsNoTracking().Where(value => value.Id == entryId.Value).Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : Deserialize<EntryResource>(payload);
    }

    public async Task UpsertEntryDraftAsync(EntryDraft draft, CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(draft);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.EntryDrafts.SingleOrDefaultAsync(value => value.Id == draft.Id.Value, cancellationToken);
        if (document is null) context.EntryDrafts.Add(new EntryDraftDocument { Id = draft.Id.Value, Name = draft.Name, Payload = JsonSerializer.Serialize(draft, JsonOptions) });
        else { document.Name = draft.Name; document.Payload = JsonSerializer.Serialize(draft, JsonOptions); }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EntryDraft>> ListEntryDraftsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.EntryDrafts.AsNoTracking().OrderBy(value => value.Name).Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<EntryDraft>).ToArray();
    }

    public async Task<EntryDraft?> GetEntryDraftAsync(EntryId entryId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payload = await context.EntryDrafts.AsNoTracking().Where(value => value.Id == entryId.Value).Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : Deserialize<EntryDraft>(payload);
    }

    public async Task CreateInteractionAsync(WorkplaceInteraction interaction, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Interactions.Add(ToDocument(interaction));
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new WorkplaceConcurrencyException(exception.InnerException?.Message ?? exception.Message); }
    }

    public async Task<WorkplaceInteraction?> GetInteractionAsync(WorkplaceWorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payload = await context.Interactions.AsNoTracking()
            .Where(value => value.Id == interactionId.ToString() && value.WorkspaceId == workspaceId.Value)
            .Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : Deserialize<WorkplaceInteraction>(payload);
    }

    public async Task<IReadOnlyList<WorkplaceInteraction>> ListInteractionsAsync(WorkplaceWorkspaceId workspaceId, int take, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.Interactions.AsNoTracking()
            .Where(value => value.WorkspaceId == workspaceId.Value)
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
        var document = await context.Interactions.SingleOrDefaultAsync(value => value.Id == interaction.Id.ToString() && value.WorkspaceId == interaction.WorkspaceId.Value, cancellationToken)
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
        context.ConversationMessages.Add(new ConversationMessageDocument { Id = message.Id.ToString(), WorkspaceId = message.WorkspaceId.Value, InteractionId = message.InteractionId.ToString(), WorkTaskId = message.WorkTaskId?.ToString(), CreatedAt = message.CreatedAt, Payload = JsonSerializer.Serialize(message, JsonOptions) });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(WorkplaceWorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.ConversationMessages.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value && value.InteractionId == interactionId.ToString()).Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<ConversationMessage>).OrderBy(value => value.CreatedAt).ToArray();
    }

    public async Task CreatePendingActionAsync(PendingAction action, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.PendingActions.Add(new PendingActionDocument { Id = action.Id.ToString(), WorkspaceId = action.WorkspaceId.Value, InteractionId = action.InteractionId.ToString(), WorkTaskId = action.WorkTaskId?.ToString(), Status = action.Status, ResumeTokenHash = action.ResumeTokenHash, CreatedAt = action.CreatedAt, Version = action.Version, Payload = JsonSerializer.Serialize(action, JsonOptions) });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PendingAction?> GetPendingActionAsync(WorkplaceWorkspaceId workspaceId, PendingActionId actionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payload = await context.PendingActions.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value && value.Id == actionId.ToString()).Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : Deserialize<PendingAction>(payload);
    }

    public async Task<IReadOnlyList<PendingAction>> ListPendingActionsAsync(WorkplaceWorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.PendingActions.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value && value.InteractionId == interactionId.ToString()).Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<PendingAction>).OrderBy(value => value.CreatedAt).ToArray();
    }

    public async Task SavePendingActionAsync(PendingAction action, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.PendingActions.SingleOrDefaultAsync(value => value.WorkspaceId == action.WorkspaceId.Value && value.Id == action.Id.ToString(), cancellationToken) ?? throw new KeyNotFoundException($"Pending action '{action.Id}' was not found.");
        if (document.Version != expectedVersion) throw new WorkplaceConcurrencyException("The PendingAction version is stale.");
        document.Status = action.Status; document.Version = action.Version; document.Payload = JsonSerializer.Serialize(action, JsonOptions);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddActivityAsync(WorkTaskActivity activity, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.WorkTaskActivities.Add(new WorkTaskActivityDocument { Id = activity.Id.ToString(), WorkspaceId = activity.WorkspaceId.Value, WorkTaskId = activity.WorkTaskId.ToString(), CreatedAt = activity.CreatedAt, Payload = JsonSerializer.Serialize(activity, JsonOptions) }); await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkTaskActivity>> ListActivitiesAsync(WorkplaceWorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var payloads = await context.WorkTaskActivities.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value && value.WorkTaskId == taskId.ToString()).Select(value => value.Payload).ToArrayAsync(cancellationToken); return payloads.Select(Deserialize<WorkTaskActivity>).OrderBy(value => value.CreatedAt).ToArray();
    }

    public async Task AddResultAsync(WorkTaskResult result, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); context.WorkTaskResults.Add(new WorkTaskResultDocument { Id = result.Id.ToString(), WorkspaceId = result.WorkspaceId.Value, WorkTaskId = result.WorkTaskId.ToString(), CreatedAt = result.CreatedAt, Payload = JsonSerializer.Serialize(result, JsonOptions) }); await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkTaskResult>> ListResultsAsync(WorkplaceWorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var payloads = await context.WorkTaskResults.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value && value.WorkTaskId == taskId.ToString()).Select(value => value.Payload).ToArrayAsync(cancellationToken); return payloads.Select(Deserialize<WorkTaskResult>).OrderBy(value => value.CreatedAt).ToArray();
    }

    public async Task AddArtifactAsync(WorkTaskArtifact artifact, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); context.WorkTaskArtifacts.Add(new WorkTaskArtifactDocument { Id = artifact.Id.ToString(), WorkspaceId = artifact.WorkspaceId.Value, WorkTaskId = artifact.WorkTaskId.ToString(), CreatedAt = artifact.CreatedAt, Payload = JsonSerializer.Serialize(artifact, JsonOptions) }); await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkTaskArtifact>> ListArtifactsAsync(WorkplaceWorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var payloads = await context.WorkTaskArtifacts.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value && value.WorkTaskId == taskId.ToString()).Select(value => value.Payload).ToArrayAsync(cancellationToken); return payloads.Select(Deserialize<WorkTaskArtifact>).OrderBy(value => value.CreatedAt).ToArray();
    }

    public async Task<WorkTaskArtifact?> GetArtifactAsync(WorkplaceWorkspaceId workspaceId, WorkTaskId taskId, WorkTaskArtifactId artifactId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var payload = await context.WorkTaskArtifacts.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value && value.WorkTaskId == taskId.ToString() && value.Id == artifactId.ToString()).Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken); return payload is null ? null : Deserialize<WorkTaskArtifact>(payload);
    }

    public async Task CreateNotificationAsync(WorkNotification notification, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); context.WorkNotifications.Add(new WorkNotificationDocument { Id = notification.Id.ToString(), WorkspaceId = notification.WorkspaceId.Value, Kind = notification.Kind, CreatedAt = notification.CreatedAt, ReadAt = notification.ReadAt, Version = notification.Version, Payload = JsonSerializer.Serialize(notification, JsonOptions) }); await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkNotification>> ListNotificationsAsync(WorkplaceWorkspaceId workspaceId, bool? unreadOnly, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var query = context.WorkNotifications.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value); if (unreadOnly == true) query = query.Where(value => value.ReadAt == null); var payloads = await query.Select(value => value.Payload).ToArrayAsync(cancellationToken); return payloads.Select(Deserialize<WorkNotification>).OrderByDescending(value => value.CreatedAt).ToArray();
    }

    public async Task<WorkNotification?> GetNotificationAsync(WorkplaceWorkspaceId workspaceId, WorkNotificationId notificationId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var payload = await context.WorkNotifications.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value && value.Id == notificationId.ToString()).Select(value => value.Payload).SingleOrDefaultAsync(cancellationToken); return payload is null ? null : Deserialize<WorkNotification>(payload);
    }

    public async Task SaveNotificationAsync(WorkNotification notification, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken); var document = await context.WorkNotifications.SingleOrDefaultAsync(value => value.WorkspaceId == notification.WorkspaceId.Value && value.Id == notification.Id.ToString(), cancellationToken) ?? throw new KeyNotFoundException($"Notification '{notification.Id}' was not found."); if (document.Version != expectedVersion) throw new WorkplaceConcurrencyException("The notification version is stale."); document.ReadAt = notification.ReadAt; document.Version = notification.Version; document.Payload = JsonSerializer.Serialize(notification, JsonOptions); await context.SaveChangesAsync(cancellationToken);
    }

    private static InteractionDocument ToDocument(WorkplaceInteraction interaction) => new()
    {
        Id = interaction.Id.ToString(), WorkspaceId = interaction.WorkspaceId.Value, EntryId = interaction.EntryId.Value,
        Status = interaction.Status, LastActivityAt = interaction.LastActivityAt, Version = interaction.Version,
        Payload = JsonSerializer.Serialize(interaction, JsonOptions)
    };

    private static T Deserialize<T>(string payload) => JsonSerializer.Deserialize<T>(payload, JsonOptions)
        ?? throw new InvalidOperationException($"Stored {typeof(T).Name} document is invalid.");
}

public static class SqliteWorkServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteWorkPlane(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContextFactory<WorkDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IWorkItemRepository, SqliteWorkItemRepository>();
        services.AddSingleton<IWorkplaceRepository, SqliteWorkplaceRepository>();
        return services;
    }
}
