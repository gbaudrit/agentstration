using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Extensions.Memory.Sqlite;

public sealed class SqliteAepMemoryDbContext(DbContextOptions<SqliteAepMemoryDbContext> options) : DbContext(options)
{
    internal DbSet<SqliteAepMemoryRecordDocument> Records => Set<SqliteAepMemoryRecordDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var record = modelBuilder.Entity<SqliteAepMemoryRecordDocument>();
        record.ToTable("MemoryRecords");
        record.HasKey(value => new { value.WorkspaceId, value.Id });
        record.Property(value => value.ScopeKind).HasMaxLength(32);
        record.Property(value => value.ScopeKey).HasMaxLength(256);
        record.Property(value => value.SourceKind).HasMaxLength(32);
        record.Property(value => value.SourceId).HasMaxLength(256);
        record.Property(value => value.Reason).HasMaxLength(512);
        record.HasIndex(value => new { value.WorkspaceId, value.ScopeKind, value.ScopeKey, value.CreatedAt });
        record.HasIndex(value => new { value.WorkspaceId, value.ExpiresAt });
    }
}

internal sealed class SqliteAepMemoryRecordDocument
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

public sealed class SqliteAepMemoryProvider(IDbContextFactory<SqliteAepMemoryDbContext> contexts) : IAepMemoryProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumPageSize = 100;

    public AepMemoryProviderDescriptor Descriptor { get; } = new(
        "sqlite",
        "SQLite durable Memory",
        new(ExactScope: true, Expiry: true, Delete: true, ClearScope: true, PurgeExpired: true),
        new Dictionary<string, JsonElement>
        {
            ["storage"] = JsonSerializer.SerializeToElement("sqlite"),
            ["durability"] = JsonSerializer.SerializeToElement("local")
        });

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task<AepProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            return await context.Database.CanConnectAsync(cancellationToken)
                ? new("available")
                : new("unavailable", "SQLite database is not reachable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new("unavailable", "SQLite database health check failed.");
        }
    }

    public async Task WriteAsync(AepMemoryRecord record, CancellationToken cancellationToken)
    {
        ValidateRecord(record);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        context.Records.Add(ToDocument(record));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AepMemoryRecord?> GetAsync(AepMemoryRecordRequest request, CancellationToken cancellationToken)
    {
        ValidateWorkspace(request.WorkspaceId);
        if (request.RecordId == Guid.Empty) throw new ArgumentException("A record id is required.", nameof(request));
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var value = await context.Records.AsNoTracking().SingleOrDefaultAsync(
            item => item.WorkspaceId == request.WorkspaceId && item.Id == request.RecordId,
            cancellationToken);
        return value is null ? null : FromDocument(value);
    }

    public async Task<IReadOnlyList<AepMemoryRecord>> ListAsync(AepMemoryListRequest request, CancellationToken cancellationToken)
    {
        ValidateWorkspace(request.WorkspaceId);
        if (request.Skip < 0) throw new ArgumentOutOfRangeException(nameof(request), "Skip cannot be negative.");
        if (request.Take is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(request), $"Take must be between 1 and {MaximumPageSize}.");
        if (request.Scope is not null) ValidateScope(request.Scope);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var now = request.Now.UtcTicks;
        var query = context.Records.AsNoTracking().Where(value => value.WorkspaceId == request.WorkspaceId && (value.ExpiresAt == null || value.ExpiresAt > now));
        if (request.Scope is not null)
        {
            var kind = request.Scope.Kind;
            var key = request.Scope.Key;
            query = query.Where(value => value.ScopeKind == kind && value.ScopeKey == key);
        }
        var values = await query.OrderByDescending(value => value.CreatedAt).ThenBy(value => value.Id)
            .Skip(request.Skip).Take(request.Take).ToArrayAsync(cancellationToken);
        return values.Select(FromDocument).ToArray();
    }

    public async Task<bool> DeleteAsync(AepMemoryRecordRequest request, CancellationToken cancellationToken)
    {
        ValidateWorkspace(request.WorkspaceId);
        if (request.RecordId == Guid.Empty) throw new ArgumentException("A record id is required.", nameof(request));
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await context.Records.Where(value => value.WorkspaceId == request.WorkspaceId && value.Id == request.RecordId)
            .ExecuteDeleteAsync(cancellationToken) == 1;
    }

    public async Task<int> ClearScopeAsync(AepMemoryScopeRequest request, CancellationToken cancellationToken)
    {
        ValidateWorkspace(request.WorkspaceId);
        ValidateScope(request.Scope);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await context.Records.Where(value => value.WorkspaceId == request.WorkspaceId && value.ScopeKind == request.Scope.Kind && value.ScopeKey == request.Scope.Key)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> PurgeExpiredAsync(AepMemoryPurgeRequest request, CancellationToken cancellationToken)
    {
        ValidateWorkspace(request.WorkspaceId);
        if (request.Take is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(request), $"Take must be between 1 and {MaximumPageSize}.");
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var now = request.Now.UtcTicks;
        var ids = await context.Records.Where(value => value.WorkspaceId == request.WorkspaceId && value.ExpiresAt != null && value.ExpiresAt <= now)
            .OrderBy(value => value.ExpiresAt).ThenBy(value => value.Id).Take(request.Take).Select(value => value.Id).ToArrayAsync(cancellationToken);
        var deleted = 0;
        foreach (var id in ids)
            deleted += await context.Records.Where(value => value.WorkspaceId == request.WorkspaceId && value.Id == id).ExecuteDeleteAsync(cancellationToken);
        return deleted;
    }

    private static void ValidateRecord(AepMemoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateWorkspace(record.WorkspaceId);
        if (record.Id == Guid.Empty) throw new ArgumentException("A record id is required.", nameof(record));
        ValidateScope(record.Scope);
        if (string.IsNullOrWhiteSpace(record.Content) || record.Content.Length > 4_096) throw new ArgumentException("Content must contain at most 4096 characters.", nameof(record));
        if (record.Tags.Count > 16 || record.Tags.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 64)) throw new ArgumentException("Tags are invalid.", nameof(record));
        if (string.IsNullOrWhiteSpace(record.Provenance.Reason) || record.Provenance.Reason.Length > 512) throw new ArgumentException("Provenance reason is invalid.", nameof(record));
        if (record.Provenance.CreatedByPrincipalId == Guid.Empty) throw new ArgumentException("A creating principal is required.", nameof(record));
        if (record.Provenance.SourceId?.Length > 256) throw new ArgumentException("Source id is too long.", nameof(record));
    }

    private static void ValidateWorkspace(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("A Workspace id is required.", nameof(workspaceId));
    }

    private static void ValidateScope(AepMemoryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Kind is not ("Agent" or "Shared") || string.IsNullOrWhiteSpace(scope.Key) || scope.Key.Length > 256)
            throw new ArgumentException("The Memory scope is invalid.", nameof(scope));
    }

    private static SqliteAepMemoryRecordDocument ToDocument(AepMemoryRecord value) => new()
    {
        WorkspaceId = value.WorkspaceId,
        Id = value.Id,
        ScopeKind = value.Scope.Kind,
        ScopeKey = value.Scope.Key,
        Content = value.Content,
        TagsJson = JsonSerializer.Serialize(value.Tags, JsonOptions),
        SourceKind = value.Provenance.SourceKind,
        SourceId = value.Provenance.SourceId,
        Reason = value.Provenance.Reason,
        CreatedByPrincipalId = value.Provenance.CreatedByPrincipalId,
        CreatedAt = value.CreatedAt.UtcTicks,
        ExpiresAt = value.ExpiresAt?.UtcTicks
    };

    private static AepMemoryRecord FromDocument(SqliteAepMemoryRecordDocument value) => new(
        value.Id,
        value.WorkspaceId,
        new(value.ScopeKind, value.ScopeKey),
        value.Content,
        JsonSerializer.Deserialize<string[]>(value.TagsJson, JsonOptions) ?? [],
        new(value.SourceKind, value.SourceId, value.Reason, value.CreatedByPrincipalId),
        new DateTimeOffset(value.CreatedAt, TimeSpan.Zero),
        value.ExpiresAt is null ? null : new DateTimeOffset(value.ExpiresAt.Value, TimeSpan.Zero));
}
