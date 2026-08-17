using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Resources;
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
        document.Property(value => value.WorkspaceId);
        document.Property(value => value.Key).HasMaxLength(512);
        document.Property(value => value.FlowId).HasMaxLength(128);
        document.Property(value => value.Namespace).HasMaxLength(128);
        document.Property(value => value.Version).HasMaxLength(128);
        document.Property(value => value.ETag).HasMaxLength(64).IsConcurrencyToken();
        document.Property(value => value.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        document.HasIndex(value => new { value.WorkspaceId, value.Namespace, value.Kind, value.FlowId, value.Version });
    }
}

internal sealed class FlowDocument
{
    public Guid WorkspaceId { get; set; }
    public required string Key { get; set; }
    public required string Kind { get; set; }
    public required string FlowId { get; set; }
    public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
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

    public async Task<StoredFlow> CreateAsync(FlowResource definition, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var document = Document(definition.WorkspaceId, DefinitionKey(definition.WorkspaceId, definition.Id), DefinitionKind, definition.Id, null, definition, now);
        context.Documents.Add(document);
        await SaveCreateAsync(context, cancellationToken);
        return new StoredFlow(definition, document.ETag, now);
    }

    public async Task<StoredFlow?> GetAsync(WorkspaceId workspaceId, FlowId id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Key == DefinitionKey(workspaceId, id), cancellationToken);
        return document is null ? null : ToFlow(document);
    }

    public async Task<FlowPage> ListAsync(WorkspaceId workspaceId, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 200);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await context.Documents.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value && value.Kind == DefinitionKind)
            .OrderBy(value => value.Namespace).ThenBy(value => value.FlowId).Skip(skip).Take(take + 1).ToArrayAsync(cancellationToken);
        return new FlowPage(documents.Take(take).Select(ToFlow).ToArray(), documents.Length > take);
    }

    public async Task<FlowPage> ListAsync(WorkspaceId workspaceId, ResourceNamespace @namespace, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 200);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await context.Documents.AsNoTracking()
            .Where(value => value.WorkspaceId == workspaceId.Value && value.Kind == DefinitionKind && value.Namespace == @namespace.Value)
            .OrderBy(value => value.FlowId).Skip(skip).Take(take + 1).ToArrayAsync(cancellationToken);
        return new FlowPage(documents.Take(take).Select(ToFlow).ToArray(), documents.Length > take);
    }

    public async Task<StoredFlow> UpdateAsync(FlowResource definition, string expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.SingleOrDefaultAsync(value => value.Key == DefinitionKey(definition.WorkspaceId, definition.Id), cancellationToken)
            ?? throw new FlowNotFoundException(definition.Id);
        if (!string.Equals(document.ETag, expectedETag, StringComparison.Ordinal)) throw new FlowConcurrencyException("The supplied ETag does not match the current Flow version.");
        var now = timeProvider.GetUtcNow();
        document.Payload = JsonSerializer.Serialize(definition, JsonOptions);
        document.ETag = NewETag();
        document.UpdatedAt = now;
        await SaveUpdateAsync(context, cancellationToken);
        return new StoredFlow(definition, document.ETag, now);
    }

    public async Task DeleteAsync(WorkspaceId workspaceId, FlowId id, string? expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var definition = await context.Documents.SingleOrDefaultAsync(value => value.Key == DefinitionKey(workspaceId, id), cancellationToken)
            ?? throw new FlowNotFoundException(id);
        if (expectedETag is not null && !string.Equals(definition.ETag, expectedETag, StringComparison.Ordinal))
            throw new FlowConcurrencyException("The supplied ETag does not match the current Flow version.");
        var namespaceValue = id.Namespace.Value;
        var documents = await context.Documents.Where(value => value.WorkspaceId == workspaceId.Value && value.Namespace == namespaceValue && value.FlowId == id.Value).ToArrayAsync(cancellationToken);
        context.Documents.RemoveRange(documents);
        await SaveUpdateAsync(context, cancellationToken);
    }

    public async Task<StoredFlowVersion> CreateVersionAsync(FlowVersion version, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var document = Document(version.WorkspaceId, VersionKey(version.WorkspaceId, version.FlowId, version.Version), VersionKind, version.FlowId, version.Version, version, now);
        context.Documents.Add(document);
        await SaveCreateAsync(context, cancellationToken);
        return new StoredFlowVersion(version, document.ETag, now);
    }

    public async Task<StoredFlowVersion?> GetVersionAsync(WorkspaceId workspaceId, FlowId id, string version, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Key == VersionKey(workspaceId, id, version), cancellationToken);
        return document is null ? null : ToVersion(document);
    }

    public async Task<IReadOnlyList<StoredFlowVersion>> ListVersionsAsync(WorkspaceId workspaceId, FlowId id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var namespaceValue = id.Namespace.Value;
        var documents = await context.Documents.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value && value.Namespace == namespaceValue && value.Kind == VersionKind && value.FlowId == id.Value)
            .OrderBy(value => value.Version).ToArrayAsync(cancellationToken);
        return documents.Select(ToVersion).ToArray();
    }

    public async Task<StoredFlowRun> CreateRunAsync(FlowRun run, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var document = Document(run.WorkspaceId, RunKey(run.WorkspaceId, run.Id), RunKind, run.FlowId, run.FlowVersion, run, now);
        context.Documents.Add(document);
        await SaveCreateAsync(context, cancellationToken);
        return new StoredFlowRun(run, document.ETag, now);
    }

    public async Task<StoredFlowRun?> GetRunAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Key == RunKey(workspaceId, runId), cancellationToken);
        return document is null ? null : ToRun(document);
    }

    public async Task<FlowRunPage> ListRunsAsync(WorkspaceId workspaceId, FlowId? flowId, FlowRunStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 200);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Documents.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value && value.Kind == RunKind);
        if (flowId is not null)
        {
            var namespaceValue = flowId.Value.Namespace.Value;
            query = query.Where(value => value.Namespace == namespaceValue && value.FlowId == flowId.Value.Value);
        }
        var documents = await query.OrderByDescending(value => value.UpdatedAt).Skip(skip).Take(take + 1).ToArrayAsync(cancellationToken);
        var runs = documents.Select(ToRun);
        if (status is not null) runs = runs.Where(value => value.Value.Status == status);
        var items = runs.Take(take).ToArray();
        return new FlowRunPage(items, documents.Length > take);
    }

    public async Task<IReadOnlyList<FlowRunKey>> ListRecoverableRunsAsync(int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await context.Documents.AsNoTracking().Where(value => value.Kind == RunKind)
            .OrderBy(value => value.UpdatedAt).Skip(skip).Take(Math.Min(take, 1000)).ToArrayAsync(cancellationToken);
        return documents.Select(ToRun).Where(value => value.Value.Status is FlowRunStatus.Pending or FlowRunStatus.Running)
            .Select(value => new FlowRunKey(value.Value.WorkspaceId, value.Value.Id)).ToArray();
    }

    public async Task<StoredFlowRun> UpdateRunAsync(FlowRun run, string expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.SingleOrDefaultAsync(value => value.Key == RunKey(run.WorkspaceId, run.Id), cancellationToken)
            ?? throw new FlowRunNotFoundException(run.Id);
        if (!string.Equals(document.ETag, expectedETag, StringComparison.Ordinal)) throw new FlowConcurrencyException("The Flow Run was modified concurrently.");
        var existing = ToRun(document).Value;
        if (existing.Scope != run.Scope || existing.WorkspaceId != run.WorkspaceId)
            throw new FlowConcurrencyException("The Flow Run execution scope is immutable.");
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
        var document = Document(draft.WorkspaceId, DraftKey(draft.WorkspaceId, draft.FlowId), DraftKind, draft.FlowId, null, draft, now);
        context.Documents.Add(document);
        await SaveCreateAsync(context, cancellationToken);
        return new StoredFlowDraft(draft, document.ETag, now);
    }

    public async Task<StoredFlowDraft?> GetDraftAsync(WorkspaceId workspaceId, FlowId flowId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Key == DraftKey(workspaceId, flowId), cancellationToken);
        return document is null ? null : ToDraft(document);
    }

    public async Task<StoredFlowDraft> UpdateDraftAsync(FlowDraft draft, string expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.SingleOrDefaultAsync(value => value.Key == DraftKey(draft.WorkspaceId, draft.FlowId), cancellationToken)
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
        var existing = await context.Documents.AsNoTracking().Where(value => value.WorkspaceId == runEvent.WorkspaceId.Value && value.Kind == RunEventKind && value.FlowId == runEvent.RunId).CountAsync(cancellationToken);
        var sequenced = runEvent with { Sequence = existing + 1 };
        context.Documents.Add(Document(runEvent.WorkspaceId, RunEventKey(runEvent.WorkspaceId, runEvent.RunId, sequenced.Sequence), RunEventKind, new FlowId(runEvent.RunId), sequenced.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), sequenced, runEvent.Timestamp));
        await SaveCreateAsync(context, cancellationToken);
        return sequenced;
    }

    public async Task<IReadOnlyList<FlowRunEvent>> ListRunEventsAsync(WorkspaceId workspaceId, string runId, long afterSequence, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await context.Documents.AsNoTracking().Where(value => value.WorkspaceId == workspaceId.Value && value.Kind == RunEventKind && value.FlowId == runId).OrderBy(value => value.Key).ToArrayAsync(cancellationToken);
        return documents.Select(document => Deserialize<FlowRunEvent>(document)).Where(runEvent => runEvent.Sequence > afterSequence).ToArray();
    }

    private static FlowDocument Document<T>(WorkspaceId workspaceId, string key, string kind, FlowId id, string? version, T value, DateTimeOffset now) => new()
    {
        WorkspaceId = workspaceId.Value,
        Key = key,
        Kind = kind,
        FlowId = id.Value,
        Namespace = id.Namespace.Value,
        Version = version,
        Payload = JsonSerializer.Serialize(value, JsonOptions),
        ETag = NewETag(),
        UpdatedAt = now
    };
    private static StoredFlow ToFlow(FlowDocument document) => new(Deserialize<FlowResource>(document), document.ETag, document.UpdatedAt);
    private static StoredFlowVersion ToVersion(FlowDocument document) => new(Deserialize<FlowVersion>(document), document.ETag, document.UpdatedAt);
    private static StoredFlowRun ToRun(FlowDocument document) => new(Deserialize<FlowRun>(document), document.ETag, document.UpdatedAt);
    private static StoredFlowDraft ToDraft(FlowDocument document) => new(Deserialize<FlowDraft>(document), document.ETag, document.UpdatedAt);
    private static T Deserialize<T>(FlowDocument document) => JsonSerializer.Deserialize<T>(document.Payload, JsonOptions) ?? throw new InvalidOperationException($"Stored Flow resource '{document.Key}' is invalid.");
    private static string DefinitionKey(WorkspaceId workspaceId, FlowId id) => $"workspace:{workspaceId}:flow:{id.Namespace.Value}:{id.Value}";
    private static string VersionKey(WorkspaceId workspaceId, FlowId id, string version) => $"workspace:{workspaceId}:flow:{id.Namespace.Value}:{id.Value}:version:{version}";
    private static string RunKey(WorkspaceId workspaceId, string id) => $"workspace:{workspaceId}:run:{id}";
    private static string DraftKey(WorkspaceId workspaceId, FlowId id) => $"workspace:{workspaceId}:flow:{id.Namespace.Value}:{id.Value}:draft";
    private static string RunEventKey(WorkspaceId workspaceId, string runId, long sequence) => $"workspace:{workspaceId}:run:{runId}:event:{sequence:D12}";
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
