using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.Sqlite;

public sealed partial class WorkDbContext
{
    internal DbSet<WorkTaskDeliveryDocument> WorkTaskDeliveries => Set<WorkTaskDeliveryDocument>();
    private static void ConfigureWorkTaskDeliveries(ModelBuilder builder)
    {
        var row = builder.Entity<WorkTaskDeliveryDocument>();
        row.ToTable("WorkTaskDeliveries"); row.HasKey(value => new { value.WorkspaceId, value.Id });
        row.Property(value => value.WorkspaceId).HasMaxLength(36); row.Property(value => value.Id).HasMaxLength(36);
        row.Property(value => value.OwnerPrincipalId).HasMaxLength(36).IsRequired(); row.Property(value => value.ProjectId).HasMaxLength(36).IsRequired();
        row.Property(value => value.PlanId).HasMaxLength(36).IsRequired(); row.Property(value => value.TaskId).HasMaxLength(36).IsRequired();
        row.Property(value => value.AttemptId).HasMaxLength(36).IsRequired(); row.Property(value => value.IntentDigest).HasMaxLength(64).IsRequired();
        row.Property(value => value.Version).IsConcurrencyToken(); row.Property(value => value.Payload).IsRequired();
        row.HasIndex(value => new { value.WorkspaceId, value.OwnerPrincipalId, value.ProjectId, value.AttemptId }).IsUnique().HasDatabaseName("IX_WorkTaskDeliveries_Attempt");
        row.HasIndex(value => new { value.WorkspaceId, value.OwnerPrincipalId, value.ProjectId, value.CreatedAtTicks, value.Id }).HasDatabaseName("IX_WorkTaskDeliveries_History");
    }
}
internal sealed class WorkTaskDeliveryDocument
{
    public required string WorkspaceId { get; set; }
    public required string Id { get; set; }
    public required string OwnerPrincipalId { get; set; }
    public required string ProjectId { get; set; }
    public required string PlanId { get; set; }
    public required string TaskId { get; set; }
    public required string AttemptId { get; set; }
    public required string IntentDigest { get; set; }
    public long Version { get; set; }
    public long CreatedAtTicks { get; set; }
    public required string Payload { get; set; }
}
