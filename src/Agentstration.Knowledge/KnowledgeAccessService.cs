using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.Knowledge;

public sealed class KnowledgeAccessException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IKnowledgeAccessTokenProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

public interface IKnowledgeAccessLifetime
{
    CancellationToken Stopping { get; }
}

public interface IKnowledgeSnapshotContentReader
{
    Task<ReadOnlyMemory<byte>> ReadAsync(KnowledgeSnapshotResource snapshot, KnowledgeSnapshotArtifact artifact, CancellationToken cancellationToken);
}

public sealed partial class KnowledgeAccessService(
    KnowledgeSourceManagementService sources, KnowledgeSnapshotService snapshots, KnowledgeProjectionService projections,
    IResourceStore resources, ICurrentRequestContext current, IRequestContextScopeFactory scopes,
    IAuthorizationService authorization, IKnowledgeSnapshotContentReader content,
    IKnowledgeAccessTokenProtector protector, TimeProvider clock, IKnowledgeAccessLifetime? lifetime = null, ISecurityAuditWriter? audit = null)
{
    public const string SessionKind = "KnowledgeReadSession";
    public const int MaximumCharacters = 32_768;
    public const int MaximumDocumentBytes = 1_048_576;
    public const int MaximumSessions = 256;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Lazy<Task<StoredResource<KnowledgeProjectionResource>>>> preparations = new();
    private readonly SemaphoreSlim preparationAdmission = new(32, 32);

    private sealed record AccessToken
    {
        public int Version { get; init; } = 1;
        public required string Kind { get; init; }
        public required Guid Principal { get; init; }
        public required Guid Tenant { get; init; }
        public required Guid Workspace { get; init; }
        public required Guid Source { get; init; }
        public required string Consumer { get; init; }
        public required DateTimeOffset Expires { get; init; }
        public string? Snapshot { get; init; }
        public Guid SnapshotUid { get; init; }
        public string? Session { get; init; }
        public long SessionGeneration { get; init; }
        public int Document { get; init; }
        public long Position { get; init; }
        public long? End { get; init; }
        public bool SingleDocument { get; init; }
        public string? Pattern { get; init; }
        public bool IgnoreCase { get; init; }
        public int ContextCharacters { get; init; }
        public string? RequestKey { get; init; }
        public long SourceGeneration { get; init; }
    }

    private RequestContext Context() => KnowledgeOperationalRead.AdministrativeContext(current)
        ?? (current.IsInitialized ? current.Current : throw Error("knowledge_access_context_required"));
    private ResourceScopeRef Consumer() => KnowledgeOperationalRead.AdministrativeScope(current)
        ?? ResourceScopeRef.Workspace(Context().WorkspaceId);
    private async Task<T> InWorkspaceAsync<T>(Func<Task<T>> operation)
    {
        using var scope = scopes.Push(Context());
        return await operation();
    }
    private async Task<KnowledgeSourceResource> SourceAsync(KnowledgeSourceId id, bool execute, CancellationToken ct)
    {
        await authorization.EnsurePermissionAsync(Context(), execute ? AuthorizationPermissions.RunsExecute : AuthorizationPermissions.RunsRead, ct);
        try
        {
            var source = (await sources.ResolveForUseAsync(id, Consumer(), ct)).Value;
            if (execute && !source.Definition.Enabled) throw Error("knowledge_access_source_disabled");
            if (audit is not null) await audit.WriteAsync(new(execute ? SecurityAuditActions.KnowledgeContentAccessed
                : SecurityAuditActions.KnowledgeContentStatusInspected, WorkspaceId: Context().WorkspaceId), ct);
            return source;
        }
        catch (KnowledgeSourceNotFoundException) { throw Error("knowledge_access_source_not_found"); }
        catch (KnowledgeSourceValidationException exception) { throw Error(exception.Code); }
    }
    private static KnowledgeSourceId Id(KnowledgeSourceResource source) => new(source.Name, source.Namespace, source.ScopeRef);
    private string Seal(AccessToken value) => protector.Protect(JsonSerializer.Serialize(value, Json));
    private AccessToken Open(string value, string kind, KnowledgeSourceResource source)
    {
        if (value.Length is < 1 or > 16_384) throw Error("knowledge_access_cursor_invalid");
        AccessToken token;
        try { token = JsonSerializer.Deserialize<AccessToken>(protector.Unprotect(value), Json) ?? throw Error("knowledge_access_cursor_invalid"); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { throw Error("knowledge_access_cursor_invalid"); }
        var context = Context();
        if (token.Version != 1 || token.Kind != kind || token.Principal != context.PrincipalId
            || token.Tenant != context.TenantId || token.Workspace != context.WorkspaceId || token.Source != source.Uid || token.Consumer != Consumer().Value)
            throw Error("knowledge_access_cursor_invalid");
        if (token.Expires <= clock.GetUtcNow()) throw Error("knowledge_access_cursor_expired");
        return token;
    }
    private AccessToken Token(string kind, KnowledgeSourceResource source) => new()
    {
        Kind = kind,
        Principal = Context().PrincipalId,
        Tenant = Context().TenantId,
        Workspace = Context().WorkspaceId,
        Source = source.Uid,
        Consumer = Consumer().Value,
        SourceGeneration = source.Generation,
        Expires = clock.GetUtcNow() + SessionLifetime
    };
    private async Task<KnowledgeSnapshotResource> ExactAsync(KnowledgeSourceResource source, string name, CancellationToken ct)
    {
        try { return await snapshots.ResolveForRetrievalAsync(Id(source), name, ct); }
        catch (KnowledgeSnapshotException exception) { throw Error(exception.Code); }
    }
    private static KnowledgeAccessException Error(string code) => new(code, "The Knowledge content operation is unavailable or invalid.");
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private bool NeedsPreparation(KnowledgeSourceResource source, KnowledgeSnapshotResource? snapshot) => snapshot is null
        || snapshot.KnowledgeSourceGeneration != source.Generation
        || (source.Definition.Lifecycle is { } policy
            ? policy.TimeToLive is { } ttl && clock.GetUtcNow() >= snapshot.PublishedAt + ttl
            : source.Definition.DataSources.Any(binding =>
        {
            var evidence = snapshot.ProjectionInputs.SingleOrDefault(value => value.BindingName == binding.Name);
            if (evidence is null && !binding.Required && !binding.RefreshIfNecessary) return false;
            return !KnowledgeFreshness.IsFresh(binding, evidence?.AcquiredAt, clock)
                || KnowledgeFreshness.RequiresRefresh(binding, evidence?.AcquiredAt, clock);
        }));

    private async Task<(KnowledgeSnapshotResource? Snapshot, KnowledgeAccessResult? Pending)> SelectAsync(
        KnowledgeSourceResource source, string? snapshotId, string? preparationId, CancellationToken ct)
    {
        if (snapshotId is not null && preparationId is not null) throw Error("knowledge_access_selection_invalid");
        if (snapshotId is not null) return (await ExactAsync(source, snapshotId, ct), null);
        AccessToken? pending = preparationId is null ? null : Open(preparationId, "preparation", source);
        if (pending is null)
        {
            var active = await snapshots.GetActiveAsync(Id(source), ct);
            if (active?.LifecycleState is not (KnowledgeSnapshotLifecycleState.Unavailable or KnowledgeSnapshotLifecycleState.Retired) && !NeedsPreparation(source, active?.Snapshot))
                return (active!.Snapshot, null);
            var previous = (await projections.ListAsync(Id(source), ct))
                .Where(value => value.KnowledgeSourceGeneration == source.Generation && value.CreatedBy == Context().PrincipalId)
                .OrderByDescending(value => value.CreatedAt).FirstOrDefault();
            var key = previous is { State: KnowledgeProjectionState.Running, IdempotencyKey: { } running }
                && running.StartsWith("access-", StringComparison.Ordinal) ? running
                : "access-" + Hash($"{source.Uid:N}:{source.Generation}:{active?.Snapshot.Uid}:{Context().PrincipalId:N}:{(previous?.State == KnowledgeProjectionState.Failed ? (Guid?)previous.Uid : null)}");
            pending = Token("preparation", source) with { RequestKey = key };
        }
        if (pending.SourceGeneration != source.Generation || pending.RequestKey is null) throw Error("knowledge_access_configuration_changed");
        var caller = Context();
        var consumer = Consumer();
        var taskKey = $"{caller.WorkspaceId:N}:{source.Uid:N}:{caller.PrincipalId:N}:{pending.RequestKey}";
        if (preparations.Count >= 32 && !preparations.ContainsKey(taskKey)) throw Error("knowledge_access_capacity");
        var work = preparations.GetOrAdd(taskKey, _ => new(() => PrepareAsync(source, pending.RequestKey, caller, consumer, taskKey)));
        var task = work.Value;
        _ = task.ContinueWith(fault => _ = fault.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(150), ct));
        ct.ThrowIfCancellationRequested();
        if (finished != task) return (null, new()
        {
            State = "preparing",
            SourceUid = source.Uid,
            PreparationId = preparationId ?? Seal(pending),
            RetryAfterMilliseconds = 500
        });
        try
        {
            var result = (await task).Value;
            if (result.State != KnowledgeProjectionState.Succeeded || result.SnapshotName is null)
            {
                if (await ExpiredFallbackAsync(source, ct) is { } fallback) return (fallback, null);
                return (null, new()
                {
                    State = "failed",
                    SourceUid = source.Uid,
                    ProjectionId = result.Name,
                    ErrorCode = result.ErrorCode ?? "knowledge_access_preparation_failed"
                });
            }
            return (await ExactAsync(source, result.SnapshotName, ct), null);
        }
        catch (KnowledgeProjectionException exception)
        {
            if (await ExpiredFallbackAsync(source, ct) is { } fallback) return (fallback, null);
            return (null, new() { State = "failed", SourceUid = source.Uid, ErrorCode = exception.Code });
        }
    }

    private async Task<KnowledgeSnapshotResource?> ExpiredFallbackAsync(KnowledgeSourceResource source, CancellationToken ct)
    {
        if (source.Definition.Lifecycle?.AllowExpiredOnRefreshFailure != true) return null;
        var active = await snapshots.GetActiveAsync(Id(source), ct);
        return active is not null && active.LifecycleState is not (KnowledgeSnapshotLifecycleState.Unavailable or KnowledgeSnapshotLifecycleState.Retired)
            ? await ExactAsync(source, active.Snapshot.Name, ct) : null;
    }

    private async Task<StoredResource<KnowledgeProjectionResource>> PrepareAsync(KnowledgeSourceResource source, string key,
        RequestContext caller, ResourceScopeRef consumer, string taskKey)
    {
        var admitted = await preparationAdmission.WaitAsync(0);
        try
        {
            if (!admitted) throw Error("knowledge_access_capacity");
            using var scope = consumer.Kind switch
            {
                ResourceScopeKind.Instance => scopes.PushInstance(caller),
                ResourceScopeKind.Tenant => scopes.PushTenantAdministration(caller),
                _ => scopes.Push(caller)
            };
            return await projections.StartAsync(Id(source), new()
            {
                IdempotencyKey = key,
                Parameters = source.Definition.ProjectionParameters.Clone(),
                ExpectedSourceUid = source.Uid,
                ExpectedSourceGeneration = source.Generation
            }, lifetime?.Stopping ?? CancellationToken.None);
        }
        finally { preparations.TryRemove(taskKey, out _); if (admitted) preparationAdmission.Release(); }
    }

    private async Task EnsureNewReadAllowedAsync(KnowledgeSourceResource source, KnowledgeSnapshotResource snapshot, CancellationToken ct)
    {
        try { await snapshots.EnsureNewReadAllowedAsync(source, snapshot, ct); }
        catch (KnowledgeSnapshotException exception) { throw Error(exception.Code); }
    }

    private async Task<AccessToken> SessionAsync(KnowledgeSourceResource source, KnowledgeSnapshotResource snapshot,
        string kind, CancellationToken ct)
    {
        var scope = ResourceScopeRef.Workspace(Context().WorkspaceId);
        var sessions = await InWorkspaceAsync(() => resources.ListExactAsync<KnowledgeReadSessionResource>(scope, SessionKind, 0, MaximumSessions, ct));
        var reusable = sessions.FirstOrDefault(value => value.Value.Ready && value.Value.SourceUid == source.Uid && value.Value.SnapshotUid == snapshot.Uid
            && value.Value.PrincipalId == Context().PrincipalId && value.Value.TenantId == Context().TenantId
            && value.Value.ExpiresAt > clock.GetUtcNow());
        if (reusable is not null) return Token(kind, source) with
        {
            Snapshot = snapshot.Name,
            SnapshotUid = snapshot.Uid,
            Session = reusable.Value.Name,
            SessionGeneration = reusable.Value.Generation,
            Expires = reusable.Value.ExpiresAt
        };
        await EnsureNewReadAllowedAsync(source, snapshot, ct);
        var occupied = sessions.ToDictionary(value => value.Value.Name, StringComparer.Ordinal);
        var start = Random.Shared.Next(MaximumSessions);
        // Fixed slots and conditional writes bound durable leases even across concurrent server processes.
        for (var offset = 0; offset < MaximumSessions; offset++)
        {
            var name = "read-" + ((start + offset) % MaximumSessions).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            occupied.TryGetValue(name, out var previous);
            if (previous is not null && previous.Value.ExpiresAt > clock.GetUtcNow()) continue;
            var session = new KnowledgeReadSessionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Ready = false,
                Kind = SessionKind,
                Metadata = new() { Name = name, Namespace = ResourceNamespace.Default },
                ScopeRef = scope,
                Uid = previous?.Value.Uid ?? Guid.Empty,
                Generation = previous is null ? 1 : checked(previous.Value.Generation + 1),
                SourceUid = source.Uid,
                SnapshotName = snapshot.Name,
                SnapshotUid = snapshot.Uid,
                PrincipalId = Context().PrincipalId,
                TenantId = Context().TenantId,
                ExpiresAt = clock.GetUtcNow() + SessionLifetime
            };
            try
            {
                var stored = await InWorkspaceAsync(() => resources.PutExactAsync(scope, session, previous?.ETag, previous is null, ct));
                try { await EnsureNewReadAllowedAsync(source, snapshot, ct); }
                catch (KnowledgeAccessException)
                {
                    try
                    {
                        _ = await InWorkspaceAsync(() => resources.PutExactAsync(scope, stored.Value with
                        { Generation = checked(stored.Value.Generation + 1), ExpiresAt = clock.GetUtcNow() }, stored.ETag, false, ct));
                    }
                    catch (ResourceConcurrencyException) { }
                    throw;
                }
                stored = await InWorkspaceAsync(() => resources.PutExactAsync(scope, stored.Value with
                { Generation = checked(stored.Value.Generation + 1), Ready = true }, stored.ETag, false, ct));
                return Token(kind, source) with
                {
                    Snapshot = snapshot.Name,
                    SnapshotUid = snapshot.Uid,
                    Session = session.Name,
                    SessionGeneration = stored.Value.Generation,
                    Expires = session.ExpiresAt
                };
            }
            catch (ResourceConcurrencyException) { }
        }
        throw Error("knowledge_access_session_capacity");
    }

    private async Task<KnowledgeSnapshotResource> ContinueAsync(KnowledgeSourceResource source, AccessToken token, CancellationToken ct)
    {
        var scope = ResourceScopeRef.Workspace(Context().WorkspaceId);
        var session = await InWorkspaceAsync(() => resources.GetExactAsync<KnowledgeReadSessionResource>(
            new(scope, ResourceNamespace.Default, SessionKind, token.Session ?? string.Empty), ct));
        if (session?.Value is not { } lease || lease.SourceUid != source.Uid || lease.Generation != token.SessionGeneration || lease.PrincipalId != Context().PrincipalId
            || lease.TenantId != Context().TenantId || lease.ExpiresAt <= clock.GetUtcNow()
            || lease.SnapshotUid != token.SnapshotUid || lease.SnapshotName != token.Snapshot)
            throw Error("knowledge_access_cursor_invalid");
        var snapshot = await ExactAsync(source, lease.SnapshotName, ct);
        if (snapshot.Uid != lease.SnapshotUid) throw Error("knowledge_access_snapshot_changed");
        return snapshot;
    }
}
