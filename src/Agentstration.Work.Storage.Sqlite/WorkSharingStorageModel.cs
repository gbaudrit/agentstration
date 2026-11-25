using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.Sqlite;

public sealed partial class WorkDbContext
{
    internal DbSet<WorkSharingStateDocument> WorkSharingStates => Set<WorkSharingStateDocument>();
    internal DbSet<WorkSharingOperationDocument> WorkSharingOperations => Set<WorkSharingOperationDocument>();
    internal DbSet<WorkSharingPlanMappingDocument> WorkSharingPlanMappings => Set<WorkSharingPlanMappingDocument>();

    private static void ConfigureWorkSharing(ModelBuilder builder)
    {
        var state = builder.Entity<WorkSharingStateDocument>();
        state.ToTable("WorkSharingStates"); state.HasKey(value => new { value.WorkspaceId, value.ProjectId });
        state.Property(value => value.WorkspaceId).HasMaxLength(36); state.Property(value => value.ProjectId).HasMaxLength(36);
        state.Property(value => value.OwnerPrincipalId).HasMaxLength(36).IsRequired(); state.Property(value => value.Version).IsConcurrencyToken();
        state.Property(value => value.Payload).IsRequired();
        var operation = builder.Entity<WorkSharingOperationDocument>();
        operation.ToTable("WorkSharingOperations"); operation.HasKey(value => new { value.WorkspaceId, value.Id });
        operation.Property(value => value.WorkspaceId).HasMaxLength(36); operation.Property(value => value.Id).HasMaxLength(36);
        operation.Property(value => value.OwnerPrincipalId).HasMaxLength(36).IsRequired(); operation.Property(value => value.ProjectId).HasMaxLength(36).IsRequired();
        operation.Property(value => value.Version).IsConcurrencyToken(); operation.Property(value => value.Payload).IsRequired();
        operation.HasIndex(value => new { value.WorkspaceId, value.OwnerPrincipalId, value.ProjectId, value.CreatedAtTicks, value.Id })
            .HasDatabaseName("IX_WorkSharingOperations_ProjectHistory");
        var mapping = builder.Entity<WorkSharingPlanMappingDocument>();
        mapping.ToTable("WorkSharingPlanMappings"); mapping.HasKey(value => new { value.WorkspaceId, value.OwnerPrincipalId, value.PlanId });
        mapping.Property(value => value.WorkspaceId).HasMaxLength(36); mapping.Property(value => value.OwnerPrincipalId).HasMaxLength(36);
        mapping.Property(value => value.PlanId).HasMaxLength(36); mapping.Property(value => value.OperationId).HasMaxLength(36); mapping.Property(value => value.ProjectId).HasMaxLength(36).IsRequired();
    }
}
internal sealed class WorkSharingStateDocument
{
    public required string WorkspaceId { get; set; }
    public required string OwnerPrincipalId { get; set; }
    public required string ProjectId { get; set; }
    public long Version { get; set; }
    public required string Payload { get; set; }
}
internal sealed class WorkSharingOperationDocument
{
    public required string WorkspaceId { get; set; }
    public required string OwnerPrincipalId { get; set; }
    public required string ProjectId { get; set; }
    public required string Id { get; set; }
    public long Version { get; set; }
    public long CreatedAtTicks { get; set; }
    public required string Payload { get; set; }
}
internal sealed class WorkSharingPlanMappingDocument
{
    public required string WorkspaceId { get; set; }
    public required string OwnerPrincipalId { get; set; }
    public required string ProjectId { get; set; }
    public required string PlanId { get; set; }
    public string? OperationId { get; set; }
}
