using System.Text.Json;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.ResourcePlanning.Storage.Sqlite;

public sealed class ResourcePlanningDbContext(DbContextOptions<ResourcePlanningDbContext> options) : DbContext(options)
{
    internal DbSet<ResourcePlanDocument> Plans => Set<ResourcePlanDocument>();
    internal DbSet<ResourcePlanActivityDocument> Activities => Set<ResourcePlanActivityDocument>();
    internal DbSet<ResourceChangeSetDocument> ChangeSets => Set<ResourceChangeSetDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var plan = modelBuilder.Entity<ResourcePlanDocument>();
        plan.ToTable("ResourcePlans");
        plan.HasKey(value => new { value.WorkspaceId, value.Id });
        plan.Property(value => value.ETag).HasMaxLength(64).IsConcurrencyToken();
        plan.Property(value => value.Payload).IsRequired();
        plan.Property(value => value.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        plan.HasIndex(value => new { value.TenantId, value.WorkspaceId, value.Status, value.UpdatedAt, value.Id });

        var activity = modelBuilder.Entity<ResourcePlanActivityDocument>();
        activity.ToTable("ResourcePlanActivities");
        activity.HasKey(value => value.Id);
        activity.Property(value => value.Payload).IsRequired();
        activity.Property(value => value.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        activity.HasIndex(value => new { value.TenantId, value.WorkspaceId, value.PlanId, value.CreatedAt, value.Id });

        var changeSet = modelBuilder.Entity<ResourceChangeSetDocument>();
        changeSet.ToTable("ResourceChangeSets");
        changeSet.HasKey(value => new { value.WorkspaceId, value.Id });
        changeSet.Property(value => value.MaterializationDigest).HasMaxLength(80);
        changeSet.Property(value => value.ETag).HasMaxLength(64).IsConcurrencyToken();
        changeSet.Property(value => value.Payload).IsRequired();
        changeSet.Property(value => value.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        changeSet.HasIndex(value => new { value.TenantId, value.WorkspaceId, value.PlanId, value.PlanRevision, value.MaterializationDigest }).IsUnique();
        changeSet.HasIndex(value => new { value.TenantId, value.WorkspaceId, value.CreatedAt, value.Id });
    }
}

internal sealed class ResourcePlanDocument
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public ResourcePlanStatus Status { get; set; }
    public long Revision { get; set; }
    public required string Payload { get; set; }
    public required string ETag { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ResourcePlanActivityDocument
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PlanId { get; set; }
    public long PlanRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Payload { get; set; }
}

internal sealed class ResourceChangeSetDocument
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PlanId { get; set; }
    public long PlanRevision { get; set; }
    public required string MaterializationDigest { get; set; }
    public ResourceChangeSetStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Payload { get; set; }
    public required string ETag { get; set; }
}

public sealed class SqliteResourcePlanRepository(
    IDbContextFactory<ResourcePlanningDbContext> contextFactory) : IResourcePlanRepository, IResourceChangeSetRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task<ResourcePlanSnapshot> CreateAsync(ResourcePlan plan, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = ToDocument(plan);
        context.Plans.Add(document);
        await SaveCreateAsync(context, cancellationToken);
        return Snapshot(document);
    }

    public async Task<ResourcePlanSnapshot?> GetAsync(ResourcePlanScope scope, ResourcePlanId id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Plans.AsNoTracking().SingleOrDefaultAsync(value =>
            value.TenantId == scope.TenantId && value.WorkspaceId == scope.WorkspaceId.Value && value.Id == id.Value,
            cancellationToken);
        return document is null ? null : Snapshot(document);
    }

    public async Task<ResourcePlanPage> ListAsync(ResourcePlanScope scope, ResourcePlanStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Plans.AsNoTracking().Where(value => value.TenantId == scope.TenantId && value.WorkspaceId == scope.WorkspaceId.Value);
        if (status is not null) query = query.Where(value => value.Status == status);
        var documents = await query.OrderByDescending(value => value.UpdatedAt).ThenByDescending(value => value.Id)
            .Skip(skip).Take(take + 1).ToArrayAsync(cancellationToken);
        return new(documents.Take(take).Select(Snapshot).ToArray(), documents.Length > take);
    }

    public async Task<ResourcePlanSnapshot> UpdateAsync(ResourcePlan plan, string expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Plans.SingleOrDefaultAsync(value =>
            value.TenantId == plan.Scope.TenantId && value.WorkspaceId == plan.Scope.WorkspaceId.Value && value.Id == plan.Id.Value,
            cancellationToken) ?? throw new ResourcePlanNotFoundException(plan.Id);
        if (!string.Equals(document.ETag, expectedETag, StringComparison.Ordinal))
            throw new ResourcePlanConcurrencyException("The Resource Plan was modified concurrently.");
        document.Status = plan.Status;
        document.Revision = plan.Revision;
        document.Payload = JsonSerializer.Serialize(plan, JsonOptions);
        document.ETag = NewETag();
        document.UpdatedAt = plan.UpdatedAt;
        await SaveUpdateAsync(context, cancellationToken);
        return Snapshot(document);
    }

    public async Task AddActivityAsync(ResourcePlanActivity activity, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Activities.Add(new()
        {
            Id = activity.Id,
            TenantId = activity.Scope.TenantId,
            WorkspaceId = activity.Scope.WorkspaceId.Value,
            PlanId = activity.PlanId.Value,
            PlanRevision = activity.PlanRevision,
            CreatedAt = activity.CreatedAt,
            Payload = JsonSerializer.Serialize(activity, JsonOptions)
        });
        await SaveCreateAsync(context, cancellationToken);
    }

    public async Task<IReadOnlyList<ResourcePlanActivity>> ListActivitiesAsync(ResourcePlanScope scope, ResourcePlanId id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.Activities.AsNoTracking().Where(value =>
                value.TenantId == scope.TenantId && value.WorkspaceId == scope.WorkspaceId.Value && value.PlanId == id.Value)
            .OrderBy(value => value.CreatedAt).ThenBy(value => value.Id).Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<ResourcePlanActivity>).ToArray();
    }

    public async Task<ResourceChangeSetSnapshot> CreateAsync(ResourceChangeSet changeSet, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = ToDocument(changeSet);
        context.ChangeSets.Add(document);
        await SaveCreateAsync(context, cancellationToken);
        return Snapshot(document);
    }

    public async Task<ResourceChangeSetSnapshot?> GetAsync(ResourcePlanScope scope, ResourceChangeSetId id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.ChangeSets.AsNoTracking().SingleOrDefaultAsync(value => value.TenantId == scope.TenantId && value.WorkspaceId == scope.WorkspaceId.Value && value.Id == id.Value, cancellationToken);
        return document is null ? null : Snapshot(document);
    }

    public async Task<ResourceChangeSetSnapshot?> FindAsync(ResourcePlanScope scope, ResourcePlanId planId, long planRevision, string materializationDigest, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.ChangeSets.AsNoTracking().SingleOrDefaultAsync(value => value.TenantId == scope.TenantId && value.WorkspaceId == scope.WorkspaceId.Value && value.PlanId == planId.Value && value.PlanRevision == planRevision && value.MaterializationDigest == materializationDigest, cancellationToken);
        return document is null ? null : Snapshot(document);
    }

    public async Task<ResourceChangeSetPage> ListAsync(ResourcePlanScope scope, ResourcePlanId? planId, int skip, int take, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.ChangeSets.AsNoTracking().Where(value => value.TenantId == scope.TenantId && value.WorkspaceId == scope.WorkspaceId.Value);
        if (planId is not null) query = query.Where(value => value.PlanId == planId.Value.Value);
        var documents = await query.OrderByDescending(value => value.CreatedAt).ThenByDescending(value => value.Id).Skip(skip).Take(take + 1).ToArrayAsync(cancellationToken);
        return new(documents.Take(take).Select(Snapshot).ToArray(), documents.Length > take);
    }

    public async Task<ResourceChangeSetSnapshot> UpdateAsync(ResourceChangeSet changeSet, string expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.ChangeSets.SingleOrDefaultAsync(value => value.TenantId == changeSet.Scope.TenantId && value.WorkspaceId == changeSet.Scope.WorkspaceId.Value && value.Id == changeSet.Id.Value, cancellationToken)
            ?? throw new ResourceChangeSetNotFoundException(changeSet.Id);
        if (!string.Equals(document.ETag, expectedETag, StringComparison.Ordinal)) throw new ResourcePlanConcurrencyException("The Resource ChangeSet was modified concurrently.");
        document.Status = changeSet.Status;
        document.Payload = JsonSerializer.Serialize(changeSet, JsonOptions);
        document.ETag = NewETag();
        await SaveUpdateAsync(context, cancellationToken);
        return Snapshot(document);
    }

    private static ResourcePlanDocument ToDocument(ResourcePlan plan) => new()
    {
        Id = plan.Id.Value,
        TenantId = plan.Scope.TenantId,
        WorkspaceId = plan.Scope.WorkspaceId.Value,
        Status = plan.Status,
        Revision = plan.Revision,
        Payload = JsonSerializer.Serialize(plan, JsonOptions),
        ETag = NewETag(),
        UpdatedAt = plan.UpdatedAt
    };

    private static ResourcePlanSnapshot Snapshot(ResourcePlanDocument value) => new(Deserialize<ResourcePlan>(value.Payload), value.ETag);
    private static ResourceChangeSetDocument ToDocument(ResourceChangeSet value) => new()
    {
        Id = value.Id.Value, TenantId = value.Scope.TenantId, WorkspaceId = value.Scope.WorkspaceId.Value, PlanId = value.PlanId.Value,
        PlanRevision = value.PlanRevision, MaterializationDigest = value.MaterializationDigest, Status = value.Status, CreatedAt = value.CreatedAt,
        Payload = JsonSerializer.Serialize(value, JsonOptions), ETag = NewETag()
    };
    private static ResourceChangeSetSnapshot Snapshot(ResourceChangeSetDocument value) => new(Deserialize<ResourceChangeSet>(value.Payload), value.ETag);
    private static T Deserialize<T>(string payload) => JsonSerializer.Deserialize<T>(payload, JsonOptions) ?? throw new InvalidOperationException("Stored Resource Planning data is invalid.");
    private static string NewETag() => $"\"{Guid.NewGuid():N}\"";

    private static async Task SaveCreateAsync(ResourcePlanningDbContext context, CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new ResourcePlanConcurrencyException(exception.InnerException?.Message ?? exception.Message); }
    }

    private static async Task SaveUpdateAsync(ResourcePlanningDbContext context, CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception) { throw new ResourcePlanConcurrencyException(exception.Message); }
    }
}

public static class SqliteResourcePlanningServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteResourcePlanning(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContextFactory<ResourcePlanningDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IResourcePlanRepository, SqliteResourcePlanRepository>();
        services.AddSingleton<IResourceChangeSetRepository>(provider => provider.GetRequiredService<IResourcePlanRepository>() as SqliteResourcePlanRepository
            ?? throw new InvalidOperationException("The SQLite Resource Planning repository registration is invalid."));
        return services;
    }
}
