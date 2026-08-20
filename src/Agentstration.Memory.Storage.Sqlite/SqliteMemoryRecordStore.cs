using System.Text.Json;
using Agentstration.Memory.Storage.Abstractions;
using Agentstration.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Memory.Storage.Sqlite;

public sealed class MemoryDbContext(DbContextOptions<MemoryDbContext> options) : DbContext(options)
{
    internal DbSet<MemoryRecordDocument> Records => Set<MemoryRecordDocument>();
    internal DbSet<MemoryMutationAuditDocument> MutationAudit => Set<MemoryMutationAuditDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var record = modelBuilder.Entity<MemoryRecordDocument>();
        record.ToTable("MemoryRecords");
        record.HasKey(value => new { value.WorkspaceId, value.Id });
        record.Property(value => value.ScopeKind).HasMaxLength(32);
        record.Property(value => value.ScopeKey).HasMaxLength(256);
        record.Property(value => value.SourceKind).HasMaxLength(32);
        record.Property(value => value.SourceId).HasMaxLength(256);
        record.Property(value => value.Reason).HasMaxLength(MemoryLimits.MaximumReasonLength);
        record.HasIndex(value => new { value.WorkspaceId, value.ScopeKind, value.ScopeKey, value.CreatedAt });
        record.HasIndex(value => new { value.WorkspaceId, value.ExpiresAt });
        var audit = modelBuilder.Entity<MemoryMutationAuditDocument>();
        audit.ToTable("MemoryMutationAudit");
        audit.HasKey(value => new { value.WorkspaceId, value.Id });
        audit.HasIndex(value => new { value.WorkspaceId, value.ProviderNamespace, value.ProviderName, value.Timestamp });
    }
}

internal sealed class MemoryMutationAuditDocument
{
    public Guid WorkspaceId { get; set; }
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public required string ProviderName { get; set; }
    public required string ProviderNamespace { get; set; }
    public required string Operation { get; set; }
    public required string Outcome { get; set; }
    public long Timestamp { get; set; }
    public Guid? PrincipalId { get; set; }
    public string? ScopeKind { get; set; }
    public string? ScopeKey { get; set; }
    public Guid? RecordId { get; set; }
    public int? Affected { get; set; }
    public string? SourceKind { get; set; }
    public string? SourceId { get; set; }
    public string? ErrorCode { get; set; }
}

internal sealed class MemoryRecordDocument
{
    public Guid WorkspaceId { get; set; }
    public Guid Id { get; set; }
    public required string ScopeKind { get; set; }
    public required string ScopeKey { get; set; }
    public required string Content { get; set; }
    public required string TagsJson { get; set; }
    public required string SourceKind { get; set; }
    public string? SourceId { get; set; }
    public required string Reason { get; set; }
    public Guid CreatedByPrincipalId { get; set; }
    public long CreatedAt { get; set; }
    public long? ExpiresAt { get; set; }
}

public sealed class SqliteMemoryRecordStore(IDbContextFactory<MemoryDbContext> contexts) : IMemoryRecordStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task AddAsync(MemoryRecord record, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        context.Records.Add(ToDocument(record));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<MemoryRecord?> GetAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var value = await context.Records.AsNoTracking().SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId.Value && item.Id == id.Value, cancellationToken);
        return value is null ? null : FromDocument(value);
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListAsync(WorkspaceId workspaceId, MemoryScope? scope, DateTimeOffset now, int skip, int take, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var ticks = now.UtcTicks;
        var query = context.Records.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value && (value.ExpiresAt == null || value.ExpiresAt > ticks));
        if (scope is not null)
        {
            var kind = scope.Kind.ToString();
            query = query.Where(value => value.ScopeKind == kind && value.ScopeKey == scope.Key);
        }
        var values = await query.OrderByDescending(value => value.CreatedAt).ThenBy(value => value.Id).Skip(skip).Take(take).ToArrayAsync(cancellationToken);
        return values.Select(FromDocument).ToArray();
    }

    public async Task<bool> DeleteAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await context.Records.Where(value => value.WorkspaceId == workspaceId.Value && value.Id == id.Value).ExecuteDeleteAsync(cancellationToken) == 1;
    }

    public async Task<int> ClearScopeAsync(WorkspaceId workspaceId, MemoryScope scope, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var kind = scope.Kind.ToString();
        return await context.Records.Where(value => value.WorkspaceId == workspaceId.Value && value.ScopeKind == kind && value.ScopeKey == scope.Key).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> PurgeExpiredAsync(WorkspaceId workspaceId, DateTimeOffset now, int take, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var ids = await context.Records.Where(value => value.WorkspaceId == workspaceId.Value && value.ExpiresAt != null && value.ExpiresAt <= now.UtcTicks).OrderBy(value => value.ExpiresAt).Take(take).Select(value => value.Id).ToArrayAsync(cancellationToken);
        var deleted = 0;
        foreach (var id in ids) deleted += await context.Records.Where(value => value.WorkspaceId == workspaceId.Value && value.Id == id).ExecuteDeleteAsync(cancellationToken);
        return deleted;
    }

    private static MemoryRecordDocument ToDocument(MemoryRecord value) => new()
    {
        WorkspaceId = value.WorkspaceId.Value, Id = value.Id.Value, ScopeKind = value.Scope.Kind.ToString(), ScopeKey = value.Scope.Key,
        Content = value.Content, TagsJson = JsonSerializer.Serialize(value.Tags, JsonOptions), SourceKind = value.Provenance.SourceKind.ToString(),
        SourceId = value.Provenance.SourceId, Reason = value.Provenance.Reason, CreatedByPrincipalId = value.Provenance.CreatedByPrincipalId,
        CreatedAt = value.CreatedAt.UtcTicks, ExpiresAt = value.ExpiresAt?.UtcTicks
    };

    private static MemoryRecord FromDocument(MemoryRecordDocument value) => new(
        new(value.Id), new(value.WorkspaceId), new(Enum.Parse<MemoryScopeKind>(value.ScopeKind), value.ScopeKey), value.Content,
        JsonSerializer.Deserialize<string[]>(value.TagsJson, JsonOptions) ?? [],
        new(Enum.Parse<MemorySourceKind>(value.SourceKind), value.SourceId, value.Reason, value.CreatedByPrincipalId),
        new DateTimeOffset(value.CreatedAt, TimeSpan.Zero), value.ExpiresAt is null ? null : new DateTimeOffset(value.ExpiresAt.Value, TimeSpan.Zero));
}

public sealed class SqliteMemoryMutationAuditStore(IDbContextFactory<MemoryDbContext> contexts) : IMemoryMutationAuditStore
{
    public async Task AppendAsync(MemoryMutationAuditRecord value, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        context.MutationAudit.Add(new MemoryMutationAuditDocument
        {
            WorkspaceId = value.WorkspaceId.Value, Id = value.Id, OperationId = value.OperationId,
            ProviderName = value.Provider.Name, ProviderNamespace = value.Provider.Namespace,
            Operation = value.Operation.ToString(), Outcome = value.Outcome.ToString(), Timestamp = value.Timestamp.UtcTicks,
            PrincipalId = value.PrincipalId, ScopeKind = value.Scope?.Kind.ToString(), ScopeKey = value.Scope?.Key,
            RecordId = value.RecordId?.Value, Affected = value.Affected, SourceKind = value.SourceKind?.ToString(),
            SourceId = value.SourceId, ErrorCode = value.ErrorCode
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryMutationAuditRecord>> ListAsync(WorkspaceId workspaceId, MemoryProviderReference provider, int skip, int take, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var values = await context.MutationAudit.AsNoTracking()
            .Where(value => value.WorkspaceId == workspaceId.Value && value.ProviderName == provider.Name && value.ProviderNamespace == provider.Namespace)
            .OrderByDescending(value => value.Timestamp).ThenBy(value => value.Id).Skip(skip).Take(take).ToArrayAsync(cancellationToken);
        return values.Select(value => new MemoryMutationAuditRecord(
            value.Id, value.OperationId, new(value.WorkspaceId), new(value.ProviderName, value.ProviderNamespace),
            Enum.Parse<MemoryMutationOperation>(value.Operation), Enum.Parse<MemoryMutationOutcome>(value.Outcome), new(value.Timestamp, TimeSpan.Zero),
            value.PrincipalId, value.ScopeKind is null ? null : new(Enum.Parse<MemoryScopeKind>(value.ScopeKind), value.ScopeKey!),
            value.RecordId is null ? null : new(value.RecordId.Value), value.Affected,
            value.SourceKind is null ? null : Enum.Parse<MemorySourceKind>(value.SourceKind), value.SourceId, value.ErrorCode)).ToArray();
    }
}

public static class MemoryStorageServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteMemoryStorage(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<MemoryDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IMemoryRecordStore, SqliteMemoryRecordStore>();
        services.AddSingleton<IMemoryRecordStoreResolver, SingleMemoryRecordStoreResolver>();
        services.AddSingleton<IMemoryMutationAuditStore, SqliteMemoryMutationAuditStore>();
        return services;
    }
}
