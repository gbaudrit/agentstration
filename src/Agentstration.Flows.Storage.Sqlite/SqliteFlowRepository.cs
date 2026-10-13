using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Flows.Storage.Sqlite;

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
        document.Property(value => value.Status);
        document.Property(value => value.ETag).HasMaxLength(64).IsConcurrencyToken();
        document.Property(value => value.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        document.HasIndex(value => new { value.WorkspaceId, value.Namespace, value.Kind, value.FlowId, value.Version });
        document.HasIndex(value => new { value.WorkspaceId, value.Kind, value.TenantId, value.PrincipalId, value.UpdatedAt, value.Key });
        document.HasIndex(value => new { value.WorkspaceId, value.Kind, value.TenantId, value.PrincipalId, value.Status, value.UpdatedAt, value.Key });
        document.HasIndex(value => new { value.WorkspaceId, value.Kind, value.TenantId, value.PrincipalId, value.Namespace, value.FlowId, value.UpdatedAt, value.Key });
        document.HasIndex(value => new { value.WorkspaceId, value.Kind, value.TenantId, value.PrincipalId, value.Namespace, value.FlowId, value.Status, value.UpdatedAt, value.Key });
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
    public Guid TenantId { get; set; }
    public Guid PrincipalId { get; set; }
    public FlowRunStatus? Status { get; set; }
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
    private const string InputRequestKind = "inputRequest";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await MigrateLegacyDefinitionScopesAsync(context, cancellationToken);
    }

    public async Task<StoredFlow> CreateAsync(FlowResource definition, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var document = Document(ScopeStorageId(definition.ScopeRef), DefinitionKey(definition.ScopeRef, definition.Id), DefinitionKind, definition.Id, null, definition, now);
        context.Documents.Add(document);
        await SaveCreateAsync(context, cancellationToken);
        return new StoredFlow(definition, document.ETag, now);
    }

    public async Task<StoredFlow?> GetExactAsync(ResourceScopeRef scopeRef, FlowId id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Key == DefinitionKey(scopeRef, id), cancellationToken);
        return document is null ? null : ToFlow(document);
    }

    public async Task<FlowPage> ListExactAsync(ResourceScopeRef scopeRef, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 200);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var prefix = ScopePrefix(scopeRef);
        var documents = await context.Documents.AsNoTracking().Where(value => value.Key.StartsWith(prefix) && value.Kind == DefinitionKind)
            .OrderBy(value => value.Namespace).ThenBy(value => value.FlowId).Skip(skip).Take(take + 1).ToArrayAsync(cancellationToken);
        return new FlowPage(documents.Take(take).Select(ToFlow).ToArray(), documents.Length > take);
    }

    public async Task<FlowPage> ListExactAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 200);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var prefix = ScopePrefix(scopeRef);
        var documents = await context.Documents.AsNoTracking()
            .Where(value => value.Key.StartsWith(prefix) && value.Kind == DefinitionKind && value.Namespace == @namespace.Value)
            .OrderBy(value => value.FlowId).Skip(skip).Take(take + 1).ToArrayAsync(cancellationToken);
        return new FlowPage(documents.Take(take).Select(ToFlow).ToArray(), documents.Length > take);
    }

    public async Task<StoredFlow> UpdateAsync(FlowResource definition, string expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.SingleOrDefaultAsync(value => value.Key == DefinitionKey(definition.ScopeRef, definition.Id), cancellationToken)
            ?? throw new FlowNotFoundException(definition.Id);
        if (!string.Equals(document.ETag, expectedETag, StringComparison.Ordinal)) throw new FlowConcurrencyException("The supplied ETag does not match the current Flow version.");
        var now = timeProvider.GetUtcNow();
        document.Payload = JsonSerializer.Serialize(definition, JsonOptions);
        document.ETag = NewETag();
        document.UpdatedAt = now;
        await SaveUpdateAsync(context, cancellationToken);
        return new StoredFlow(definition, document.ETag, now);
    }

    public async Task DeleteExactAsync(ResourceScopeRef scopeRef, FlowId id, string? expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var definition = await context.Documents.SingleOrDefaultAsync(value => value.Key == DefinitionKey(scopeRef, id), cancellationToken)
            ?? throw new FlowNotFoundException(id);
        if (expectedETag is not null && !string.Equals(definition.ETag, expectedETag, StringComparison.Ordinal))
            throw new FlowConcurrencyException("The supplied ETag does not match the current Flow version.");
        var flowPrefix = FlowPrefix(scopeRef, id);
        var removable = await context.Documents.Where(value =>
            value.Key.StartsWith(flowPrefix)
            && (value.Kind == DefinitionKind || value.Kind == VersionKind)).ToArrayAsync(cancellationToken);
        context.Documents.RemoveRange(removable);
        await SaveUpdateAsync(context, cancellationToken);
    }

    public async Task<StoredFlowVersion> CreateVersionAsync(FlowVersion version, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var document = Document(ScopeStorageId(version.ScopeRef), VersionKey(version.ScopeRef, version.FlowId, version.Version), VersionKind, version.FlowId, version.Version, version, now);
        context.Documents.Add(document);
        await SaveCreateAsync(context, cancellationToken);
        return new StoredFlowVersion(version, document.ETag, now);
    }

    public async Task<StoredFlowVersion?> GetVersionExactAsync(ResourceScopeRef scopeRef, FlowId id, string version, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Key == VersionKey(scopeRef, id, version), cancellationToken);
        return document is null ? null : ToVersion(document);
    }

    public async Task<IReadOnlyList<StoredFlowVersion>> ListVersionsExactAsync(ResourceScopeRef scopeRef, FlowId id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var flowPrefix = FlowPrefix(scopeRef, id);
        var documents = await context.Documents.AsNoTracking().Where(value => value.Key.StartsWith(flowPrefix) && value.Kind == VersionKind)
            .OrderBy(value => value.Version).ToArrayAsync(cancellationToken);
        return documents.Select(ToVersion).ToArray();
    }

    public async Task<StoredFlowRun> CreateRunAsync(FlowRun run, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var document = Document(run.WorkspaceId, RunKey(run.WorkspaceId, run.Id), RunKind, run.FlowId, run.FlowVersion, run, now);
        document.TenantId = run.Scope.TenantId;
        document.PrincipalId = run.Scope.PrincipalId;
        document.Status = run.Status;
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

    public async Task<StoredFlowRun?> GetRunAsync(FlowRunScope scope, string runId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Key == RunKey(scope.WorkspaceId, runId)
            && value.TenantId == scope.TenantId
            && value.PrincipalId == scope.PrincipalId,
            cancellationToken);
        return document is null ? null : ToRun(document);
    }

    public async Task<FlowRunPage> ListRunsAsync(FlowRunScope scope, FlowId? flowId, FlowRunStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 200);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Documents.AsNoTracking().Where(value =>
            value.WorkspaceId == scope.WorkspaceId.Value
            && value.Kind == RunKind
            && value.TenantId == scope.TenantId
            && value.PrincipalId == scope.PrincipalId);
        if (flowId is not null)
        {
            var namespaceValue = flowId.Value.Namespace.Value;
            query = query.Where(value => value.Namespace == namespaceValue && value.FlowId == flowId.Value.Value);
        }
        if (status is not null) query = query.Where(value => value.Status == status);
        var documents = await query.OrderByDescending(value => value.UpdatedAt).ThenByDescending(value => value.Key)
            .Skip(skip).Take(take + 1).ToArrayAsync(cancellationToken);
        return new FlowRunPage(documents.Take(take).Select(ToRun).ToArray(), documents.Length > take);
    }

    public async Task<IReadOnlyList<FlowRunKey>> ListRecoverableRunsAsync(int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await context.Documents.AsNoTracking().Where(value => value.Kind == RunKind)
            .OrderBy(value => value.UpdatedAt).Skip(skip).Take(Math.Min(take, 1000)).ToArrayAsync(cancellationToken);
        return documents.Select(ToRun).Where(value => value.Value.Status is FlowRunStatus.Pending or FlowRunStatus.Running or FlowRunStatus.WaitingForInput or FlowRunStatus.WaitingForChild)
            .Select(value => new FlowRunKey(value.Value.WorkspaceId, value.Value.Id)).ToArray();
    }

    public async Task<IReadOnlyList<FlowRunKey>> ListRunKeysAsync(int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await context.Documents.AsNoTracking().Where(value => value.Kind == RunKind)
            .OrderBy(value => value.UpdatedAt).Skip(skip).Take(Math.Min(take, 1000)).ToArrayAsync(cancellationToken);
        return documents.Select(ToRun).Select(value => new FlowRunKey(value.Value.WorkspaceId, value.Value.Id)).ToArray();
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
        document.TenantId = run.Scope.TenantId;
        document.PrincipalId = run.Scope.PrincipalId;
        document.Status = run.Status;
        document.ETag = NewETag();
        document.UpdatedAt = now;
        await SaveUpdateAsync(context, cancellationToken);
        return new StoredFlowRun(run, document.ETag, now);
    }

    public async Task DeleteRunAsync(WorkspaceId workspaceId, string runId, string expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await context.Documents.Where(value =>
            value.WorkspaceId == workspaceId.Value
            && (value.Key == RunKey(workspaceId, runId)
                || (value.FlowId == runId && (value.Kind == RunEventKind || value.Kind == InputRequestKind))))
            .ToArrayAsync(cancellationToken);
        var run = documents.SingleOrDefault(value => value.Kind == RunKind)
            ?? throw new FlowRunNotFoundException(runId);
        if (!string.Equals(run.ETag, expectedETag, StringComparison.Ordinal))
            throw new FlowConcurrencyException("The supplied ETag does not match the current Flow Run version.");
        context.Documents.RemoveRange(documents);
        await SaveUpdateAsync(context, cancellationToken);
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

    public async Task<StoredInputRequest> CreateInputRequestAsync(InputRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var document = Document(request.WorkspaceId, InputRequestKey(request.WorkspaceId, request.RunId, request.Id), InputRequestKind, new FlowId(request.RunId), request.Id, request, now);
        context.Documents.Add(document);
        await SaveCreateAsync(context, cancellationToken);
        return new StoredInputRequest(request, document.ETag, now);
    }

    public async Task<StoredInputRequest?> GetInputRequestAsync(WorkspaceId workspaceId, string runId, string requestId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Key == InputRequestKey(workspaceId, runId, requestId), cancellationToken);
        return document is null ? null : ToInputRequest(document);
    }

    public async Task<IReadOnlyList<StoredInputRequest>> ListInputRequestsAsync(WorkspaceId workspaceId, string runId, InputRequestStatus? status, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await context.Documents.AsNoTracking()
            .Where(value => value.WorkspaceId == workspaceId.Value && value.Kind == InputRequestKind && value.FlowId == runId)
            .OrderBy(value => value.UpdatedAt)
            .ToArrayAsync(cancellationToken);
        var requests = documents.Select(ToInputRequest);
        if (status is not null) requests = requests.Where(value => value.Value.Status == status);
        return requests.ToArray();
    }

    public async Task<StoredInputRequest> UpdateInputRequestAsync(InputRequest request, string expectedETag, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.SingleOrDefaultAsync(value => value.Key == InputRequestKey(request.WorkspaceId, request.RunId, request.Id), cancellationToken)
            ?? throw new FlowRunNotFoundException(request.RunId);
        if (!string.Equals(document.ETag, expectedETag, StringComparison.Ordinal))
            throw new FlowConcurrencyException("The Input Request was modified concurrently.");
        var now = timeProvider.GetUtcNow();
        document.Payload = JsonSerializer.Serialize(request, JsonOptions);
        document.ETag = NewETag();
        document.UpdatedAt = now;
        await SaveUpdateAsync(context, cancellationToken);
        return new StoredInputRequest(request, document.ETag, now);
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
    private static StoredInputRequest ToInputRequest(FlowDocument document) => new(Deserialize<InputRequest>(document), document.ETag, document.UpdatedAt);
    private static T Deserialize<T>(FlowDocument document) => JsonSerializer.Deserialize<T>(document.Payload, JsonOptions) ?? throw new InvalidOperationException($"Stored Flow resource '{document.Key}' is invalid.");
    private static async Task MigrateLegacyDefinitionScopesAsync(FlowDbContext context, CancellationToken cancellationToken)
    {
        var legacy = await context.Documents.Where(value =>
                (value.Kind == DefinitionKind || value.Kind == VersionKind)
                && value.Key.StartsWith("workspace:"))
            .ToArrayAsync(cancellationToken);
        if (legacy.Length == 0) return;

        var replacements = legacy.Select(document =>
        {
            if (document.WorkspaceId == Guid.Empty)
                throw new InvalidOperationException($"Legacy Flow resource '{document.Key}' has no Workspace owner.");
            var workspaceId = new WorkspaceId(document.WorkspaceId);
            var oldPrefix = $"workspace:{workspaceId}:";
            if (!document.Key.StartsWith(oldPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Legacy Flow resource key '{document.Key}' does not match its Workspace owner.");
            var scopeRef = ResourceScopeRef.Workspace(document.WorkspaceId);
            return new FlowDocument
            {
                WorkspaceId = document.WorkspaceId,
                Key = $"scope:{scopeRef.Value}:{document.Key[oldPrefix.Length..]}",
                Kind = document.Kind,
                FlowId = document.FlowId,
                Namespace = document.Namespace,
                Version = document.Version,
                TenantId = document.TenantId,
                PrincipalId = document.PrincipalId,
                Status = document.Status,
                Payload = MigrateLegacyDefinitionPayload(document.Payload, scopeRef, document.Key),
                ETag = document.ETag,
                UpdatedAt = document.UpdatedAt
            };
        }).ToArray();
        var replacementKeys = replacements.Select(value => value.Key).ToArray();
        if (await context.Documents.AnyAsync(value => replacementKeys.Contains(value.Key), cancellationToken))
            throw new InvalidOperationException("A legacy Flow definition cannot be migrated because its scoped identity already exists.");

        context.Documents.RemoveRange(legacy);
        context.Documents.AddRange(replacements);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static string MigrateLegacyDefinitionPayload(string payload, ResourceScopeRef scopeRef, string key)
    {
        var root = JsonNode.Parse(payload) as JsonObject
            ?? throw new InvalidOperationException($"Legacy Flow resource '{key}' has an invalid payload.");
        root.Remove("workspaceId");
        root["scopeRef"] = scopeRef.Value;
        return root.ToJsonString(JsonOptions);
    }
    private static WorkspaceId ScopeStorageId(ResourceScopeRef scopeRef) => new(scopeRef.TargetId ?? Guid.Empty);
    private static string ScopePrefix(ResourceScopeRef scopeRef) => $"scope:{scopeRef.Value}:";
    private static string FlowPrefix(ResourceScopeRef scopeRef, FlowId id) => $"{ScopePrefix(scopeRef)}flow:{id.Namespace.Value}:{id.Value}";
    private static string DefinitionKey(ResourceScopeRef scopeRef, FlowId id) => FlowPrefix(scopeRef, id);
    private static string VersionKey(ResourceScopeRef scopeRef, FlowId id, string version) => $"{FlowPrefix(scopeRef, id)}:version:{version}";
    private static string RunKey(WorkspaceId workspaceId, string id) => $"workspace:{workspaceId}:run:{id}";
    private static string DraftKey(WorkspaceId workspaceId, FlowId id) => $"workspace:{workspaceId}:flow:{id.Namespace.Value}:{id.Value}:draft";
    private static string RunEventKey(WorkspaceId workspaceId, string runId, long sequence) => $"workspace:{workspaceId}:run:{runId}:event:{sequence:D12}";
    private static string InputRequestKey(WorkspaceId workspaceId, string runId, string requestId) => $"workspace:{workspaceId}:run:{runId}:input:{requestId}";
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
