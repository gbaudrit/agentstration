using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.PostgreSql;

public sealed partial class WorkDbContext
{
    internal DbSet<WorkProjectDocument> WorkProjects => Set<WorkProjectDocument>();

    private static void ConfigureWorkProjects(ModelBuilder builder)
    {
        var project = builder.Entity<WorkProjectDocument>();
        project.ToTable("WorkProjects");
        project.HasKey(value => new { value.WorkspaceId, value.Id });
        project.Property(value => value.WorkspaceId).HasMaxLength(36);
        project.Property(value => value.Id).HasMaxLength(36);
        project.Property(value => value.OwnerPrincipalId).HasMaxLength(36).IsRequired();
        project.Property(value => value.Name).HasMaxLength(128).IsRequired();
        project.Property(value => value.Namespace).HasMaxLength(128).IsRequired();
        project.Property(value => value.Version).IsConcurrencyToken();
        project.Property(value => value.Payload).IsRequired();
        project.HasIndex(value => new { value.WorkspaceId, value.OwnerPrincipalId, value.Namespace, value.Name }).IsUnique();
    }
}

internal sealed class WorkProjectDocument
{
    public required string WorkspaceId { get; set; }
    public required string Id { get; set; }
    public required string OwnerPrincipalId { get; set; }
    public required string Name { get; set; }
    public required string Namespace { get; set; }
    public long Version { get; set; }
    public required string Payload { get; set; }
}
