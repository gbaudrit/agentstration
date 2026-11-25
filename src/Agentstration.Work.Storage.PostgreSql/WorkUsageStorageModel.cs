using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.PostgreSql;

public sealed partial class WorkDbContext
{
    internal DbSet<WorkUsageDocument> WorkUsageRecords => Set<WorkUsageDocument>();

    private static void ConfigureWorkUsage(ModelBuilder builder)
    {
        var record = builder.Entity<WorkUsageDocument>();
        record.ToTable("WorkUsageRecords");
        record.HasKey(value => new { value.WorkspaceId, value.Id });
        record.Property(value => value.WorkspaceId).HasMaxLength(36);
        record.Property(value => value.Id).HasMaxLength(36);
        record.Property(value => value.OwnerPrincipalId).HasMaxLength(36).IsRequired();
        record.Property(value => value.ProjectId).HasMaxLength(36).IsRequired();
        record.Property(value => value.RootId).HasMaxLength(36).IsRequired();
        record.Property(value => value.PlanId).HasMaxLength(36).IsRequired();
        record.Property(value => value.TaskId).HasMaxLength(36).IsRequired();
        record.Property(value => value.AttemptId).HasMaxLength(36).IsRequired();
        record.Property(value => value.Digest).HasMaxLength(64).IsRequired();
        record.Property(value => value.Payload).IsRequired();
        record.HasIndex(value => new { value.WorkspaceId, value.OwnerPrincipalId, value.ProjectId, value.RootId, value.Revision }).HasDatabaseName("IX_WorkUsageRecords_RootRevision").IsUnique();
        record.HasIndex(value => new { value.WorkspaceId, value.OwnerPrincipalId, value.ProjectId, value.CreatedAtTicks, value.Id }).HasDatabaseName("IX_WorkUsageRecords_History");
        record.HasIndex(value => new { value.WorkspaceId, value.OwnerPrincipalId, value.ProjectId, value.PlanId }).HasDatabaseName("IX_WorkUsageRecords_Plan");
        record.HasIndex(value => new { value.WorkspaceId, value.OwnerPrincipalId, value.ProjectId, value.TaskId }).HasDatabaseName("IX_WorkUsageRecords_Task");
        record.HasIndex(value => new { value.WorkspaceId, value.OwnerPrincipalId, value.ProjectId, value.AttemptId }).HasDatabaseName("IX_WorkUsageRecords_Attempt");
    }
}

internal sealed class WorkUsageDocument
{
    public required string WorkspaceId { get; set; }
    public required string Id { get; set; }
    public required string OwnerPrincipalId { get; set; }
    public required string ProjectId { get; set; }
    public required string RootId { get; set; }
    public required string PlanId { get; set; }
    public required string TaskId { get; set; }
    public required string AttemptId { get; set; }
    public long Revision { get; set; }
    public long CreatedAtTicks { get; set; }
    public required string Digest { get; set; }
    public required string Payload { get; set; }
}
