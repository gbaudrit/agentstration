using System.Text.Json;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Work.Storage.Sqlite;

public sealed class WorkDbContext(DbContextOptions<WorkDbContext> options) : DbContext(options)
{
    internal DbSet<WorkItemDocument> WorkItems => Set<WorkItemDocument>();

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
        item.Property(value => value.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        item.Property(value => value.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        item.Property(value => value.Version).IsConcurrencyToken();
        item.HasIndex(value => new { value.Status, value.CreatedAt });
        item.HasIndex(value => new { value.Type, value.CreatedAt });
        item.HasIndex(value => new { value.RequesterIdentity, value.CreatedAt });
        item.HasIndex(value => new { value.SelectedAgentId, value.CreatedAt });
    }
}

internal sealed class WorkItemDocument
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public WorkItemStatus Status { get; set; }
    public string? RequesterIdentity { get; set; }
    public string? RequestedAgentId { get; set; }
    public string? SelectedAgentId { get; set; }
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
        if (query.Status is not null) documents = documents.Where(value => value.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Type)) documents = documents.Where(value => value.Type == query.Type);
        if (!string.IsNullOrWhiteSpace(query.RequesterIdentity)) documents = documents.Where(value => value.RequesterIdentity == query.RequesterIdentity);
        if (!string.IsNullOrWhiteSpace(query.AgentId)) documents = documents.Where(value => value.RequestedAgentId == query.AgentId || value.SelectedAgentId == query.AgentId);
        if (query.CreatedFrom is not null) documents = documents.Where(value => value.CreatedAt >= query.CreatedFrom);
        if (query.CreatedTo is not null) documents = documents.Where(value => value.CreatedAt <= query.CreatedTo);
        documents = Order(documents, query.SortBy, query.SortDirection);
        var page = await documents.Skip(query.Skip).Take(take + 1).ToArrayAsync(cancellationToken);
        return new WorkItemPage(page.Take(take).Select(FromDocument).ToArray(), page.Length > take);
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
    private static string ETag(long version) => $"\"{version}\"";
}

public static class SqliteWorkServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteWorkPlane(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContextFactory<WorkDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IWorkItemRepository, SqliteWorkItemRepository>();
        return services;
    }
}
