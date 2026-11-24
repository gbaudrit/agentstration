using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.PostgreSql;

public sealed partial class WorkDbContext
{
    internal DbSet<WorkTaskOperationDocument> WorkTaskOperations => Set<WorkTaskOperationDocument>();

    private static void ConfigureWorkTaskOperations(ModelBuilder builder)
    {
        var operation = builder.Entity<WorkTaskOperationDocument>();
        operation.ToTable("WorkTaskOperations");
        operation.HasKey(value => new { value.WorkspaceId, value.Id });
        operation.Property(value => value.WorkspaceId).HasMaxLength(36);
        operation.Property(value => value.Id).HasMaxLength(36);
        operation.Property(value => value.OwnerPrincipalId).HasMaxLength(36).IsRequired();
        operation.Property(value => value.ProjectId).HasMaxLength(36).IsRequired();
        operation.Property(value => value.Version).IsConcurrencyToken();
        operation.Property(value => value.Payload).IsRequired();
        operation.HasIndex(value => new { value.WorkspaceId, value.OwnerPrincipalId, value.ProjectId, value.CreatedAtTicks, value.Id }).HasDatabaseName("IX_WorkTaskOperations_ProjectHistory");
    }
}

internal sealed class WorkTaskOperationDocument
{
    public required string WorkspaceId { get; set; }
    public required string Id { get; set; }
    public required string OwnerPrincipalId { get; set; }
    public required string ProjectId { get; set; }
    public long Version { get; set; }
    public long CreatedAtTicks { get; set; }
    public required string Payload { get; set; }
}
