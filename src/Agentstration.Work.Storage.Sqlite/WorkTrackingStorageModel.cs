using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.Sqlite;

public sealed partial class WorkDbContext
{
    internal DbSet<WorkTrackingDocument> WorkTrackingDocuments => Set<WorkTrackingDocument>();

    private static void ConfigureWorkTracking(ModelBuilder builder)
    {
        var document = builder.Entity<WorkTrackingDocument>();
        document.ToTable("WorkTrackingDocuments");
        document.HasKey(value => new { value.WorkspaceId, value.OwnerPrincipalId, value.ProjectId, value.Key });
        document.Property(value => value.WorkspaceId).HasMaxLength(36);
        document.Property(value => value.OwnerPrincipalId).HasMaxLength(36);
        document.Property(value => value.ProjectId).HasMaxLength(36);
        document.Property(value => value.Key).HasMaxLength(80);
        document.Property(value => value.Version).IsConcurrencyToken();
        document.Property(value => value.Payload).IsRequired();
    }
}

internal sealed class WorkTrackingDocument
{
    public required string WorkspaceId { get; set; }
    public required string OwnerPrincipalId { get; set; }
    public required string ProjectId { get; set; }
    public required string Key { get; set; }
    public long Version { get; set; }
    public required string Payload { get; set; }
}
