using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.Sqlite;

public sealed partial class WorkDbContext
{
    internal DbSet<WorkPlanDocument> WorkPlans => Set<WorkPlanDocument>();
    internal DbSet<WorkPlanRevisionDocument> WorkPlanRevisions => Set<WorkPlanRevisionDocument>();
    internal DbSet<WorkPlanCommandDocument> WorkPlanCommands => Set<WorkPlanCommandDocument>();

    private static void ConfigureWorkPlans(ModelBuilder builder)
    {
        var plan = builder.Entity<WorkPlanDocument>();
        plan.ToTable("WorkPlans");
        plan.HasKey(value => new { value.WorkspaceId, value.Id });
        plan.Property(value => value.WorkspaceId).HasMaxLength(36);
        plan.Property(value => value.Id).HasMaxLength(36);
        plan.Property(value => value.OwnerPrincipalId).HasMaxLength(36).IsRequired();
        plan.Property(value => value.Version).IsConcurrencyToken();
        plan.Property(value => value.Payload).IsRequired();
        plan.HasIndex(value => new { value.WorkspaceId, value.OwnerPrincipalId });

        var revision = builder.Entity<WorkPlanRevisionDocument>();
        revision.ToTable("WorkPlanRevisions");
        revision.HasKey(value => new { value.WorkspaceId, value.PlanId, value.Number });
        revision.Property(value => value.WorkspaceId).HasMaxLength(36);
        revision.Property(value => value.PlanId).HasMaxLength(36);
        revision.Property(value => value.OwnerPrincipalId).HasMaxLength(36).IsRequired();
        revision.Property(value => value.Payload).IsRequired();
        revision.HasOne<WorkPlanDocument>().WithMany()
            .HasForeignKey(value => new { value.WorkspaceId, value.PlanId }).OnDelete(DeleteBehavior.Restrict);

        var command = builder.Entity<WorkPlanCommandDocument>();
        command.ToTable("WorkPlanCommands");
        command.HasKey(value => new { value.WorkspaceId, value.PlanId, value.CommandId });
        command.Property(value => value.WorkspaceId).HasMaxLength(36);
        command.Property(value => value.PlanId).HasMaxLength(36);
        command.Property(value => value.CommandId).HasMaxLength(36);
        command.Property(value => value.OwnerPrincipalId).HasMaxLength(36).IsRequired();
        command.Property(value => value.Payload).IsRequired();
        command.HasOne<WorkPlanDocument>().WithMany()
            .HasForeignKey(value => new { value.WorkspaceId, value.PlanId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkPlanDocument
{
    public required string WorkspaceId { get; set; }
    public required string Id { get; set; }
    public required string OwnerPrincipalId { get; set; }
    public long Version { get; set; }
    public required string Payload { get; set; }
}
internal sealed class WorkPlanRevisionDocument
{
    public required string WorkspaceId { get; set; }
    public required string PlanId { get; set; }
    public required string OwnerPrincipalId { get; set; }
    public long Number { get; set; }
    public required string Payload { get; set; }
}
internal sealed class WorkPlanCommandDocument
{
    public required string WorkspaceId { get; set; }
    public required string PlanId { get; set; }
    public required string OwnerPrincipalId { get; set; }
    public required string CommandId { get; set; }
    public required string Payload { get; set; }
}
