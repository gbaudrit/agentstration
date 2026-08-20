using Microsoft.EntityFrameworkCore;

namespace Agentstration.Infrastructure.Persistence.Postgres;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<WorkspaceRow> Workspaces => Set<WorkspaceRow>();
    public DbSet<InboxRow> Inboxes => Set<InboxRow>();
    public DbSet<ItemRow> Items => Set<ItemRow>();
    public DbSet<ItemAnalysisRow> ItemAnalyses => Set<ItemAnalysisRow>();
    public DbSet<MissionRow> Missions => Set<MissionRow>();
    public DbSet<MissionRunRow> MissionRuns => Set<MissionRunRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("agent_platform");
        modelBuilder.Entity<WorkspaceRow>(entity => { entity.ToTable("workspaces"); entity.HasKey(x => x.Id); entity.HasIndex(x => x.CreatedAt); });
        modelBuilder.Entity<InboxRow>(entity => { entity.ToTable("inboxes"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.WorkspaceId, x.Slug }).IsUnique(); });
        modelBuilder.Entity<ItemRow>(entity =>
        {
            entity.ToTable("items"); entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WorkspaceId, x.InboxId, x.ContentHash }).IsUnique();
            entity.HasIndex(x => new { x.WorkspaceId, x.Status, x.CreatedAt });
            entity.HasIndex(x => new { x.WorkspaceId, x.ExternalId });
        });
        modelBuilder.Entity<ItemAnalysisRow>(entity => { entity.ToTable("item_analyses"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.WorkspaceId, x.ItemId, x.CreatedAt }); });
        modelBuilder.Entity<MissionRow>(entity => { entity.ToTable("missions"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.WorkspaceId, x.Status, x.NextRunAt }); });
        modelBuilder.Entity<MissionRunRow>(entity => { entity.ToTable("mission_runs"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.WorkspaceId, x.MissionId, x.StartedAt }); });
    }
}

public sealed class WorkspaceRow { public Guid Id { get; set; } public required string Name { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class InboxRow { public Guid Id { get; set; } public Guid WorkspaceId { get; set; } public required string Name { get; set; } public required string Slug { get; set; } public required string ApiKeyHash { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class ItemRow { public Guid Id { get; set; } public Guid WorkspaceId { get; set; } public Guid InboxId { get; set; } public required string Status { get; set; } public required string ContentHash { get; set; } public string? ExternalId { get; set; } public required string RawContent { get; set; } public string? NormalizedContent { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class ItemAnalysisRow { public Guid Id { get; set; } public Guid WorkspaceId { get; set; } public Guid ItemId { get; set; } public required string Summary { get; set; } public required string CategoriesJson { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class MissionRow { public Guid Id { get; set; } public Guid WorkspaceId { get; set; } public required string Name { get; set; } public required string Objective { get; set; } public required string SourceUrl { get; set; } public int FrequencySeconds { get; set; } public decimal? Threshold { get; set; } public required string Status { get; set; } public DateTimeOffset NextRunAt { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class MissionRunRow { public Guid Id { get; set; } public Guid WorkspaceId { get; set; } public Guid MissionId { get; set; } public required string Status { get; set; } public decimal? Observation { get; set; } public bool Changed { get; set; } public string? Error { get; set; } public DateTimeOffset StartedAt { get; set; } public DateTimeOffset? CompletedAt { get; set; } }
