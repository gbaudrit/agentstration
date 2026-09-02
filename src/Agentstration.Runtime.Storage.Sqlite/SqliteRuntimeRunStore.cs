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
    internal DbSet<RuntimeExecutionStateDocument> ExecutionStates => Set<RuntimeExecutionStateDocument>();

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

        var executionState = modelBuilder.Entity<RuntimeExecutionStateDocument>();
        executionState.ToTable("RuntimeExecutionStates");
        executionState.HasKey(value => new { value.WorkspaceId, value.RunId, value.RuntimeType, value.StateId });
        executionState.Property(value => value.WorkspaceId);
        executionState.Property(value => value.RunId).HasMaxLength(160);
        executionState.Property(value => value.RuntimeType).HasMaxLength(64);
        executionState.Property(value => value.StateId).HasMaxLength(256);
        executionState.Property(value => value.ParentStateId).HasMaxLength(256);
        executionState.HasIndex(value => new { value.WorkspaceId, value.RunId, value.RuntimeType, value.CreatedAt });
    }
}

internal sealed class RuntimeExecutionStateDocument
{
    public required Guid WorkspaceId { get; set; }
    public required string RunId { get; set; }
    public required string RuntimeType { get; set; }
    public required string StateId { get; set; }
    public string? ParentStateId { get; set; }
    public required string Payload { get; set; }
    public long CreatedAt { get; set; }
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
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS RuntimeExecutionStates (
                WorkspaceId TEXT NOT NULL,
                RunId TEXT NOT NULL,
                RuntimeType TEXT NOT NULL,
                StateId TEXT NOT NULL,
                ParentStateId TEXT NULL,
                Payload TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                CONSTRAINT PK_RuntimeExecutionStates PRIMARY KEY (WorkspaceId, RunId, RuntimeType, StateId)
            );
            CREATE INDEX IF NOT EXISTS IX_RuntimeExecutionStates_WorkspaceId_RunId_RuntimeType_CreatedAt
                ON RuntimeExecutionStates (WorkspaceId, RunId, RuntimeType, CreatedAt);
            """, cancellationToken);
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

    public async Task DeleteAsync(WorkspaceId workspaceId, string runId, string expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.Runs.SingleOrDefaultAsync(value => value.WorkspaceId == workspaceId.Value && value.RunId == runId, cancellationToken)
            ?? throw new RuntimeRunNotFoundException(runId);
        if (!string.Equals(run.ETag, expectedETag, StringComparison.Ordinal))
            throw new RuntimeRunConcurrencyException("The supplied ETag does not match the current Runtime Run version.");
        context.Events.RemoveRange(await context.Events.Where(value => value.WorkspaceId == workspaceId.Value && value.RunId == runId).ToArrayAsync(cancellationToken));
        context.ExecutionStates.RemoveRange(await context.ExecutionStates.Where(value => value.WorkspaceId == workspaceId.Value && value.RunId == runId).ToArrayAsync(cancellationToken));
        context.Runs.Remove(run);
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception) { throw new RuntimeRunConcurrencyException(exception.Message); }
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

public sealed class SqliteRuntimeExecutionStateStore(
    IDbContextFactory<RuntimeRunDbContext> contextFactory) : IRuntimeExecutionStateStore
{
    public async Task StoreAsync(RuntimeExecutionState state, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.ExecutionStates.SingleOrDefaultAsync(value =>
            value.WorkspaceId == state.WorkspaceId.Value && value.RunId == state.RunId && value.RuntimeType == state.RuntimeType && value.StateId == state.StateId,
            cancellationToken);
        if (existing is null)
        {
            context.ExecutionStates.Add(new RuntimeExecutionStateDocument
            {
                WorkspaceId = state.WorkspaceId.Value,
                RunId = state.RunId,
                RuntimeType = state.RuntimeType,
                StateId = state.StateId,
                ParentStateId = state.ParentStateId,
                Payload = state.Payload.GetRawText(),
                CreatedAt = state.CreatedAt.UtcTicks
            });
        }
        else
        {
            existing.ParentStateId = state.ParentStateId;
            existing.Payload = state.Payload.GetRawText();
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<RuntimeExecutionState?> GetAsync(WorkspaceId workspaceId, string runId, string runtimeType, string stateId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var value = await context.ExecutionStates.AsNoTracking().SingleOrDefaultAsync(state =>
            state.WorkspaceId == workspaceId.Value && state.RunId == runId && state.RuntimeType == runtimeType && state.StateId == stateId,
            cancellationToken);
        return value is null ? null : ToState(value);
    }

    public async Task<IReadOnlyList<RuntimeExecutionState>> ListAsync(WorkspaceId workspaceId, string runId, string runtimeType, string? parentStateId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.ExecutionStates.AsNoTracking().Where(state => state.WorkspaceId == workspaceId.Value && state.RunId == runId && state.RuntimeType == runtimeType);
        if (parentStateId is not null) query = query.Where(state => state.ParentStateId == parentStateId);
        var values = await query.OrderBy(state => state.CreatedAt).ToArrayAsync(cancellationToken);
        return values.Select(ToState).ToArray();
    }

    public async Task DeleteAsync(WorkspaceId workspaceId, string runId, string? runtimeType, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.ExecutionStates.Where(state => state.WorkspaceId == workspaceId.Value && state.RunId == runId);
        if (runtimeType is not null) query = query.Where(state => state.RuntimeType == runtimeType);
        context.ExecutionStates.RemoveRange(await query.ToArrayAsync(cancellationToken));
        await context.SaveChangesAsync(cancellationToken);
    }

    private static RuntimeExecutionState ToState(RuntimeExecutionStateDocument value) => new(
        new WorkspaceId(value.WorkspaceId),
        value.RunId,
        value.RuntimeType,
        value.StateId,
        JsonSerializer.Deserialize<JsonElement>(value.Payload),
        new DateTimeOffset(value.CreatedAt, TimeSpan.Zero),
        value.ParentStateId);
}

public static class SqliteRuntimeRunServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteRuntimeRuns(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContextFactory<RuntimeRunDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IRuntimeRunStore, SqliteRuntimeRunStore>();
        services.AddSingleton<IRuntimeExecutionStateStore, SqliteRuntimeExecutionStateStore>();
        return services;
    }
}
