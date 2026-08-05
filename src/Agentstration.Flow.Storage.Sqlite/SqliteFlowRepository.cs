using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Flow.Storage.Sqlite;

public sealed class FlowDbContext(DbContextOptions<FlowDbContext> options) : DbContext(options)
{
    internal DbSet<FlowDocument> Documents => Set<FlowDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var document = modelBuilder.Entity<FlowDocument>();
        document.ToTable("FlowResources");
        document.HasKey(value => value.Key);
        document.Property(value => value.Key).HasMaxLength(512);
        document.Property(value => value.FlowId).HasMaxLength(128);
        document.Property(value => value.Version).HasMaxLength(128);
        document.Property(value => value.ETag).HasMaxLength(64).IsConcurrencyToken();
        document.Property(value => value.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        document.HasIndex(value => new { value.Kind, value.FlowId, value.Version });
    }
}

internal sealed class FlowDocument
{
    public required string Key { get; set; }
    public required string Kind { get; set; }
    public required string FlowId { get; set; }
    public string? Version { get; set; }
    public required string Payload { get; set; }
    public required string ETag { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SqliteFlowRepository(IDbContextFactory<FlowDbContext> contextFactory, TimeProvider timeProvider) : IFlowRepository
{
    private const string DefinitionKind = "definition";
    private const string VersionKind = "version";
    private const string RunKind = "run";
    private const string DraftKind = "draft";
    private const string RunEventKind = "runEvent";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task<StoredFlow> CreateAsync(FlowDefinition definition, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var document = Document(DefinitionKey(definition.Id), DefinitionKind, definition.Id, null, definition, now);
        context.Documents.Add(document);
        await SaveCreateAsync(context, cancellationToken);
        return new StoredFlow(definition, document.ETag, now);
    }

    public async Task<StoredFlow?> GetAsync(FlowId id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Key == DefinitionKey(id), cancellationToken);
        return document is null ? null : ToFlow(document);
    }

    public async Task<FlowPage> ListAsync(int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 200);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await context.Documents.AsNoTracking().Where(value => value.Kind == DefinitionKind)
            .OrderBy(value => value.FlowId).Skip(skip).Take(take + 1).ToArrayAsync(cancellationToken);
        return new FlowPage(documents.Take(take).Select(ToFlow).ToArray(), documents.Length > take);
    }

    public async Task<StoredFlow> UpdateAsync(FlowDefinition definition, string expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.SingleOrDefaultAsync(value => value.Key == DefinitionKey(definition.Id), cancellationToken)
            ?? throw new FlowNotFoundException(definition.Id);
        if (!string.Equals(document.ETag, expectedETag, StringComparison.Ordinal)) throw new FlowConcurrencyException("The supplied ETag does not match the current Flow version.");
        var now = timeProvider.GetUtcNow();
        document.Payload = JsonSerializer.Serialize(definition, JsonOptions);
        document.ETag = NewETag();
        document.UpdatedAt = now;
        await SaveUpdateAsync(context, cancellationToken);
        return new StoredFlow(definition, document.ETag, now);
    }

    public async Task DeleteAsync(FlowId id, string? expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var definition = await context.Documents.SingleOrDefaultAsync(value => value.Key == DefinitionKey(id), cancellationToken)
            ?? throw new FlowNotFoundException(id);
        if (expectedETag is not null && !string.Equals(definition.ETag, expectedETag, StringComparison.Ordinal))
            throw new FlowConcurrencyException("The supplied ETag does not match the current Flow version.");
        var documents = await context.Documents.Where(value => value.FlowId == id.Value).ToArrayAsync(cancellationToken);
        context.Documents.RemoveRange(documents);
        await SaveUpdateAsync(context, cancellationToken);
    }

    public async Task<StoredFlowVersion> CreateVersionAsync(FlowVersion version, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var document = Document(VersionKey(version.FlowId, version.Version), VersionKind, version.FlowId, version.Version, version, now);
        context.Documents.Add(document);
        await SaveCreateAsync(context, cancellationToken);
        return new StoredFlowVersion(version, document.ETag, now);
    }

    public async Task<StoredFlowVersion?> GetVersionAsync(FlowId id, string version, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Key == VersionKey(id, version), cancellationToken);
        return document is null ? null : ToVersion(document);
    }

    public async Task<IReadOnlyList<StoredFlowVersion>> ListVersionsAsync(FlowId id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await context.Documents.AsNoTracking().Where(value => value.Kind == VersionKind && value.FlowId == id.Value)
            .OrderBy(value => value.Version).ToArrayAsync(cancellationToken);
        return documents.Select(ToVersion).ToArray();
    }

    public async Task<StoredFlowRun> CreateRunAsync(FlowRun run, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var document = Document(RunKey(run.Id), RunKind, run.FlowId, run.FlowVersion, run, now);
        context.Documents.Add(document);
        await SaveCreateAsync(context, cancellationToken);
        return new StoredFlowRun(run, document.ETag, now);
    }

    public async Task<StoredFlowRun?> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Key == RunKey(runId), cancellationToken);
        return document is null ? null : ToRun(document);
    }

    public async Task<FlowRunPage> ListRunsAsync(FlowId? flowId, FlowRunStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 200);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Documents.AsNoTracking().Where(value => value.Kind == RunKind);
        if (flowId is not null) query = query.Where(value => value.FlowId == flowId.Value.Value);
        var documents = await query.OrderByDescending(value => value.UpdatedAt).Skip(skip).Take(take + 1).ToArrayAsync(cancellationToken);
        var runs = documents.Select(ToRun);
        if (status is not null) runs = runs.Where(value => value.Value.Status == status);
        var items = runs.Take(take).ToArray();
        return new FlowRunPage(items, documents.Length > take);
    }

    public async Task<StoredFlowRun> UpdateRunAsync(FlowRun run, string expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.SingleOrDefaultAsync(value => value.Key == RunKey(run.Id), cancellationToken)
            ?? throw new FlowRunNotFoundException(run.Id);
        if (!string.Equals(document.ETag, expectedETag, StringComparison.Ordinal)) throw new FlowConcurrencyException("The Flow Run was modified concurrently.");
        var now = timeProvider.GetUtcNow();
        document.Payload = JsonSerializer.Serialize(run, JsonOptions);
        document.ETag = NewETag();
        document.UpdatedAt = now;
        await SaveUpdateAsync(context, cancellationToken);
        return new StoredFlowRun(run, document.ETag, now);
    }

    public async Task<StoredFlowDraft> CreateDraftAsync(FlowDraft draft, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var document = Document(DraftKey(draft.FlowId), DraftKind, draft.FlowId, null, draft, now);
        context.Documents.Add(document);
        await SaveCreateAsync(context, cancellationToken);
        return new StoredFlowDraft(draft, document.ETag, now);
    }

    public async Task<StoredFlowDraft?> GetDraftAsync(FlowId flowId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Key == DraftKey(flowId), cancellationToken);
        return document is null ? null : ToDraft(document);
    }

    public async Task<StoredFlowDraft> UpdateDraftAsync(FlowDraft draft, string expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.SingleOrDefaultAsync(value => value.Key == DraftKey(draft.FlowId), cancellationToken)
            ?? throw new FlowNotFoundException(draft.FlowId);
        if (!string.Equals(document.ETag, expectedETag, StringComparison.Ordinal)) throw new FlowConcurrencyException("The supplied ETag does not match the current Flow Draft revision.");
        var now = timeProvider.GetUtcNow();
        document.Payload = JsonSerializer.Serialize(draft, JsonOptions);
        document.ETag = NewETag();
        document.UpdatedAt = now;
        await SaveUpdateAsync(context, cancellationToken);
        return new StoredFlowDraft(draft, document.ETag, now);
    }

    public async Task<FlowRunEvent> AppendRunEventAsync(FlowRunEvent runEvent, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.Documents.AsNoTracking().Where(value => value.Kind == RunEventKind && value.FlowId == runEvent.RunId).CountAsync(cancellationToken);
        var sequenced = runEvent with { Sequence = existing + 1 };
        context.Documents.Add(Document(RunEventKey(runEvent.RunId, sequenced.Sequence), RunEventKind, new FlowId(runEvent.RunId), sequenced.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), sequenced, runEvent.Timestamp));
        await SaveCreateAsync(context, cancellationToken);
        return sequenced;
    }

    public async Task<IReadOnlyList<FlowRunEvent>> ListRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await context.Documents.AsNoTracking().Where(value => value.Kind == RunEventKind && value.FlowId == runId).OrderBy(value => value.Key).ToArrayAsync(cancellationToken);
        return documents.Select(document => Deserialize<FlowRunEvent>(document)).Where(runEvent => runEvent.Sequence > afterSequence).ToArray();
    }

    private static FlowDocument Document<T>(string key, string kind, FlowId id, string? version, T value, DateTimeOffset now) => new()
    {
        Key = key, Kind = kind, FlowId = id.Value, Version = version,
        Payload = JsonSerializer.Serialize(value, JsonOptions), ETag = NewETag(), UpdatedAt = now
    };
    private static StoredFlow ToFlow(FlowDocument document) => new(Deserialize<FlowDefinition>(document), document.ETag, document.UpdatedAt);
    private static StoredFlowVersion ToVersion(FlowDocument document) => new(Deserialize<FlowVersion>(document), document.ETag, document.UpdatedAt);
    private static StoredFlowRun ToRun(FlowDocument document) => new(Deserialize<FlowRun>(document), document.ETag, document.UpdatedAt);
    private static StoredFlowDraft ToDraft(FlowDocument document) => new(Deserialize<FlowDraft>(document), document.ETag, document.UpdatedAt);
    private static T Deserialize<T>(FlowDocument document) => JsonSerializer.Deserialize<T>(document.Payload, JsonOptions) ?? throw new InvalidOperationException($"Stored Flow resource '{document.Key}' is invalid.");
    private static string DefinitionKey(FlowId id) => $"flow:{id.Value}";
    private static string VersionKey(FlowId id, string version) => $"flow:{id.Value}:version:{version}";
    private static string RunKey(string id) => $"run:{id}";
    private static string DraftKey(FlowId id) => $"flow:{id.Value}:draft";
    private static string RunEventKey(string runId, long sequence) => $"run:{runId}:event:{sequence:D12}";
    private static string NewETag() => $"\"{Guid.NewGuid():N}\"";

    private static async Task SaveCreateAsync(FlowDbContext context, CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new FlowConcurrencyException(exception.InnerException?.Message ?? exception.Message); }
    }
    private static async Task SaveUpdateAsync(FlowDbContext context, CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception) { throw new FlowConcurrencyException(exception.Message); }
    }
}

public static class SqliteFlowServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteFlowStorage(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContextFactory<FlowDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IFlowRepository, SqliteFlowRepository>();
        return services;
    }
}
