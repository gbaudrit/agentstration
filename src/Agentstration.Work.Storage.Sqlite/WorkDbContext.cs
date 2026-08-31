using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Work.Storage.Sqlite;

public sealed class WorkDbContext(DbContextOptions<WorkDbContext> options) : DbContext(options)
{
    internal DbSet<WorkItemDocument> WorkItems => Set<WorkItemDocument>();
    internal DbSet<WorkplaceDashboardDocument> Dashboards => Set<WorkplaceDashboardDocument>();
    internal DbSet<WorkplaceDashboardDraftDocument> DashboardDrafts => Set<WorkplaceDashboardDraftDocument>();
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
        item.HasKey(value => new { value.WorkspaceId, value.Id });
        item.Property(value => value.Id).HasMaxLength(36);
        item.Property(value => value.Type).HasMaxLength(128);
        item.Property(value => value.Status).HasConversion<string>().HasMaxLength(32);
        item.Property(value => value.RequesterIdentity).HasMaxLength(256);
        item.Property(value => value.RequestedAgentId).HasMaxLength(1024);
        item.Property(value => value.SelectedAgentId).HasMaxLength(1024);
        item.Property(value => value.Title).HasMaxLength(512);
        item.Property(value => value.WorkspaceId).HasMaxLength(36).IsRequired();
        item.Property(value => value.InteractionId).HasMaxLength(36);
        item.Property(value => value.EntryId).HasMaxLength(512);
        item.Property(value => value.AnchorTaskId).HasMaxLength(36);
        item.Property(value => value.FlowRunId).HasMaxLength(128);
        item.Property(value => value.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        item.Property(value => value.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        item.HasIndex(value => new { value.WorkspaceId, value.Status, value.UpdatedAt });
        item.HasIndex(value => new { value.WorkspaceId, value.InteractionId, value.UpdatedAt });
        item.HasIndex(value => new { value.WorkspaceId, value.AnchorTaskId, value.UpdatedAt });
        item.HasIndex(value => new { value.WorkspaceId, value.FlowRunId });
        item.Property(value => value.Version).IsConcurrencyToken();
        item.HasIndex(value => new { value.Status, value.CreatedAt });
        item.HasIndex(value => new { value.Type, value.CreatedAt });
        item.HasIndex(value => new { value.RequesterIdentity, value.CreatedAt });
        item.HasIndex(value => new { value.SelectedAgentId, value.CreatedAt });
        item.HasIndex(value => new { value.WorkspaceId, value.Status, value.UpdatedAt });
        item.HasIndex(value => new { value.WorkspaceId, value.InteractionId, value.UpdatedAt });
        item.HasIndex(value => new { value.WorkspaceId, value.AnchorTaskId, value.UpdatedAt });
        item.HasIndex(value => value.FlowRunId);

        ConfigureDashboard(modelBuilder.Entity<WorkplaceDashboardDocument>(), "WorkplaceDashboards");
        ConfigureDashboard(modelBuilder.Entity<WorkplaceDashboardDraftDocument>(), "WorkplaceDashboardDrafts");

        var entry = modelBuilder.Entity<EntryDocument>();
        entry.ToTable("Entries");
        entry.HasKey(value => value.Id);
        entry.Property(value => value.Id).HasMaxLength(768);
        entry.Property(value => value.WorkspaceId).HasMaxLength(36);
        entry.Property(value => value.Name).HasMaxLength(128);
        entry.HasIndex(value => new { value.WorkspaceId, value.Name });

        var entryDraft = modelBuilder.Entity<EntryDraftDocument>();
        entryDraft.ToTable("EntryDrafts");
        entryDraft.HasKey(value => value.Id);
        entryDraft.Property(value => value.Id).HasMaxLength(768);
        entryDraft.Property(value => value.WorkspaceId).HasMaxLength(36);
        entryDraft.Property(value => value.Name).HasMaxLength(128);
        entryDraft.HasIndex(value => new { value.WorkspaceId, value.Name });

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

    private static void ConfigureDashboard<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity, string table)
        where T : class, IWorkplaceDashboardDocument
    {
        entity.ToTable(table);
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasMaxLength(768);
        entity.Property(value => value.WorkspaceId).HasMaxLength(512);
        entity.Property(value => value.Name).HasMaxLength(128);
        entity.HasIndex(value => new { value.WorkspaceId, value.Name }).IsUnique();
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

internal interface IWorkplaceDashboardDocument
{
    string Id { get; set; }
    string WorkspaceId { get; set; }
    string Name { get; set; }
    string Payload { get; set; }
}

internal sealed class WorkplaceDashboardDocument : IWorkplaceDashboardDocument
{
    public required string Id { get; set; }
    public required string WorkspaceId { get; set; }
    public required string Name { get; set; }
    public required string Payload { get; set; }
}

internal sealed class WorkplaceDashboardDraftDocument : IWorkplaceDashboardDocument
{
    public required string Id { get; set; }
    public required string WorkspaceId { get; set; }
    public required string Name { get; set; }
    public required string Payload { get; set; }
}

internal sealed class EntryDocument
{
    public required string Id { get; set; }
    public required string WorkspaceId { get; set; }
    public required string Name { get; set; }
    public required string Payload { get; set; }
}

internal sealed class EntryDraftDocument
{
    public required string Id { get; set; }
    public required string WorkspaceId { get; set; }
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
    public required string Id { get; set; }
    public required string WorkspaceId { get; set; }
    public required string InteractionId { get; set; }
    public string? WorkTaskId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Payload { get; set; }
}

internal sealed class PendingActionDocument
{
    public required string Id { get; set; }
    public required string WorkspaceId { get; set; }
    public required string InteractionId { get; set; }
    public string? WorkTaskId { get; set; }
    public PendingActionStatus Status { get; set; }
    public required string ResumeTokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; }
    public required string Payload { get; set; }
}

internal sealed class WorkNotificationDocument
{
    public required string Id { get; set; }
    public required string WorkspaceId { get; set; }
    public WorkNotificationKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public long Version { get; set; }
    public required string Payload { get; set; }
}

internal interface IWorkTaskEntityDocument
{
    string Id { get; set; }
    string WorkspaceId { get; set; }
    string WorkTaskId { get; set; }
    DateTimeOffset CreatedAt { get; set; }
    string Payload { get; set; }
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

