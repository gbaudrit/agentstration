using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.Sqlite;

public sealed partial class WorkDbContext
{
    internal DbSet<WorkTaskAttemptDocument> WorkTaskAttempts => Set<WorkTaskAttemptDocument>();
    private static void ConfigureTaskAttempts(ModelBuilder builder)
    {
        var attempt = builder.Entity<WorkTaskAttemptDocument>();
        attempt.ToTable("WorkTaskAttempts");
        attempt.HasKey(value => new { value.WorkspaceId, value.Id });
        attempt.Property(value => value.WorkspaceId).HasMaxLength(36);
        attempt.Property(value => value.Id).HasMaxLength(36);
        attempt.Property(value => value.OwnerPrincipalId).HasMaxLength(36).IsRequired();
        attempt.Property(value => value.TaskId).HasMaxLength(36).IsRequired();
        attempt.Property(value => value.PlanId).HasMaxLength(36).IsRequired();
        attempt.Property(value => value.Payload).IsRequired();
        attempt.HasIndex(value => new { value.WorkspaceId, value.OwnerPrincipalId, value.TaskId, value.AdmittedAtTicks, value.Id });
        attempt.HasOne<WorkPlanDocument>().WithMany().HasForeignKey(value => new { value.WorkspaceId, value.PlanId }).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class WorkTaskAttemptDocument
{
    public required string WorkspaceId { get; set; }
    public required string Id { get; set; }
    public required string OwnerPrincipalId { get; set; }
    public required string TaskId { get; set; }
    public required string PlanId { get; set; }
    public long AdmittedAtTicks { get; set; }
    public required string Payload { get; set; }
}
