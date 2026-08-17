using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Runtime.Storage.Sqlite;

public sealed class RuntimeRunDbContext(DbContextOptions<RuntimeRunDbContext> options) : DbContext(options)
{
    internal DbSet<RuntimeRunDocument> Runs => Set<RuntimeRunDocument>();
    internal DbSet<RuntimeRunEventDocument> Events => Set<RuntimeRunEventDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var run = modelBuilder.Entity<RuntimeRunDocument>();
        run.ToTable("RuntimeRuns");
        run.HasKey(value => new { value.WorkspaceId, value.RunId });
        run.Property(value => value.WorkspaceId);
        run.Property(value => value.RunId).HasMaxLength(64);
        run.Property(value => value.AgentResourceId).HasMaxLength(1024);
        run.Property(value => value.State).HasMaxLength(32);
        run.Property(value => value.ETag).HasMaxLength(64).IsConcurrencyToken();
        run.HasIndex(value => new { value.WorkspaceId, value.AgentResourceId, value.CreatedAt });

        var runEvent = modelBuilder.Entity<RuntimeRunEventDocument>();
        runEvent.ToTable("RuntimeRunEvents");
        runEvent.HasKey(value => new { value.WorkspaceId, value.RunId, value.Sequence });
        runEvent.Property(value => value.WorkspaceId);
        runEvent.Property(value => value.RunId).HasMaxLength(64);
        runEvent.HasIndex(value => new { value.WorkspaceId, value.RunId, value.Sequence });
    }
}

internal sealed class RuntimeRunDocument
{
    public Guid WorkspaceId { get; set; }
    public required string RunId { get; set; }
    public required string AgentResourceId { get; set; }
    public required string State { get; set; }
    public required string Payload { get; set; }
    public required string ETag { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

internal sealed class RuntimeRunEventDocument
{
    public Guid WorkspaceId { get; set; }
    public required string RunId { get; set; }
    public long Sequence { get; set; }
    public required string Payload { get; set; }
    public required DateTimeOffset Timestamp { get; set; }
}

public sealed class SqliteRuntimeRunStore(IDbContextFactory<RuntimeRunDbContext> contextFactory, TimeProvider timeProvider) : IRuntimeRunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task<StoredRuntimeRun> CreateAsync(RuntimeRun run, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var etag = NewETag();
        var now = timeProvider.GetUtcNow();
        var versioned = run with { ETag = etag };
        context.Runs.Add(ToDocument(versioned, etag, now));
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new RuntimeRunConcurrencyException(exception.InnerException?.Message ?? exception.Message); }
        return new StoredRuntimeRun(versioned, etag, now);
    }

    public async Task<StoredRuntimeRun?> GetAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Runs.AsNoTracking().SingleOrDefaultAsync(value => value.WorkspaceId == workspaceId.Value && value.RunId == runId, cancellationToken);
        return document is null ? null : Deserialize(document);
    }

    public async Task<IReadOnlyList<StoredRuntimeRun>> ListAsync(WorkspaceId workspaceId, string? agentResourceId, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Runs.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value);
        if (!string.IsNullOrWhiteSpace(agentResourceId)) query = query.Where(value => value.AgentResourceId == agentResourceId);
        var documents = await query.OrderByDescending(value => value.CreatedAt).Skip(skip).Take(Math.Min(take, 1000)).ToArrayAsync(cancellationToken);
        return documents.Select(Deserialize).ToArray();
    }

    public async Task<IReadOnlyList<RuntimeRunKey>> ListRecoverableAsync(int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Runs.AsNoTracking()
            .Where(value => value.State == nameof(RuntimeRunState.Pending) || value.State == nameof(RuntimeRunState.Running))
            .OrderBy(value => value.CreatedAt)
            .Skip(skip)
            .Take(Math.Min(take, 1000))
            .Select(value => new RuntimeRunKey(new WorkspaceId(value.WorkspaceId), value.RunId))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<StoredRuntimeRun> UpdateAsync(RuntimeRun run, string expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Runs.SingleOrDefaultAsync(value => value.WorkspaceId == run.WorkspaceId.Value && value.RunId == run.Id, cancellationToken)
            ?? throw new RuntimeRunNotFoundException(run.Id);
        if (!string.Equals(document.ETag, expectedETag, StringComparison.Ordinal))
            throw new RuntimeRunConcurrencyException("The supplied ETag does not match the current runtime run version.");
        var current = Deserialize(document).Value;
        if (current.Properties.Scope != run.Properties.Scope)
            throw new RuntimeRunConcurrencyException("The Runtime Run execution scope is immutable.");
        var etag = NewETag();
        var now = timeProvider.GetUtcNow();
        var versioned = run with { ETag = etag };
        document.State = versioned.Status.State.ToString();
        document.Payload = JsonSerializer.Serialize(versioned, JsonOptions);
        document.ETag = etag;
        document.UpdatedAt = now.UtcTicks;
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception) { throw new RuntimeRunConcurrencyException(exception.Message); }
        return new StoredRuntimeRun(versioned, etag, now);
    }

    public async Task<RuntimeRunEvent> AppendEventAsync(RuntimeRunEvent runEvent, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.Runs.AnyAsync(value => value.WorkspaceId == runEvent.WorkspaceId.Value && value.RunId == runEvent.RunId, cancellationToken)) throw new RuntimeRunNotFoundException(runEvent.RunId);
        var sequence = (await context.Events.Where(value => value.WorkspaceId == runEvent.WorkspaceId.Value && value.RunId == runEvent.RunId).MaxAsync(value => (long?)value.Sequence, cancellationToken) ?? 0) + 1;
        var sequenced = runEvent with { Sequence = sequence };
        context.Events.Add(new RuntimeRunEventDocument
        {
            WorkspaceId = runEvent.WorkspaceId.Value,
            RunId = runEvent.RunId,
            Sequence = sequence,
            Payload = JsonSerializer.Serialize(sequenced, JsonOptions),
            Timestamp = runEvent.Timestamp
        });
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new RuntimeRunConcurrencyException(exception.InnerException?.Message ?? exception.Message); }
        return sequenced;
    }

    public async Task<IReadOnlyList<RuntimeRunEvent>> ListEventsAsync(WorkspaceId workspaceId, string runId, long afterSequence, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await context.Events.AsNoTracking()
            .Where(value => value.WorkspaceId == workspaceId.Value && value.RunId == runId && value.Sequence > afterSequence)
            .OrderBy(value => value.Sequence)
            .Take(1000)
            .ToArrayAsync(cancellationToken);
        return documents.Select(value => JsonSerializer.Deserialize<RuntimeRunEvent>(value.Payload, JsonOptions)
            ?? throw new InvalidOperationException($"Stored runtime event '{value.RunId}/{value.Sequence}' is invalid.")).ToArray();
    }

    private static RuntimeRunDocument ToDocument(RuntimeRun run, string etag, DateTimeOffset now) => new()
    {
        WorkspaceId = run.WorkspaceId.Value,
        RunId = run.Id,
        AgentResourceId = run.Properties.Agent.ResourceId,
        State = run.Status.State.ToString(),
        Payload = JsonSerializer.Serialize(run, JsonOptions),
        ETag = etag,
        CreatedAt = run.Status.CreatedAt.UtcTicks,
        UpdatedAt = now.UtcTicks
    };

    private static StoredRuntimeRun Deserialize(RuntimeRunDocument document)
    {
        var run = JsonSerializer.Deserialize<RuntimeRun>(document.Payload, JsonOptions)
            ?? throw new InvalidOperationException($"Stored runtime run '{document.RunId}' is invalid.");
        return new StoredRuntimeRun(run with { ETag = document.ETag }, document.ETag, new DateTimeOffset(document.UpdatedAt, TimeSpan.Zero));
    }

    private static string NewETag() => $"\"{Guid.NewGuid():N}\"";

}

public static class SqliteRuntimeRunServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteRuntimeRuns(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContextFactory<RuntimeRunDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IRuntimeRunStore, SqliteRuntimeRunStore>();
        return services;
    }
}
