using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Storage.Sqlite;

public sealed class ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options) : DbContext(options)
{
    internal DbSet<ControlPlaneDocument> Documents => Set<ControlPlaneDocument>();
    internal DbSet<TenantRow> Tenants => Set<TenantRow>();
    internal DbSet<WorkspaceRow> Workspaces => Set<WorkspaceRow>();
    internal DbSet<PrincipalRow> Principals => Set<PrincipalRow>();
    internal DbSet<PrincipalPreferencesRow> PrincipalPreferences => Set<PrincipalPreferencesRow>();
    internal DbSet<ExternalIdentityRow> ExternalIdentities => Set<ExternalIdentityRow>();
    internal DbSet<LocalIdentityRow> LocalIdentities => Set<LocalIdentityRow>();
    internal DbSet<PlatformAdministratorRow> PlatformAdministrators => Set<PlatformAdministratorRow>();
    internal DbSet<TenantMembershipRow> TenantMemberships => Set<TenantMembershipRow>();
    internal DbSet<WorkspaceMembershipRow> WorkspaceMemberships => Set<WorkspaceMembershipRow>();
    internal DbSet<RoleDefinitionRow> RoleDefinitions => Set<RoleDefinitionRow>();
    internal DbSet<RoleAssignmentRow> RoleAssignments => Set<RoleAssignmentRow>();
    internal DbSet<SecurityAuditRow> SecurityAuditEvents => Set<SecurityAuditRow>();
    internal DbSet<PersonalAccessTokenRow> PersonalAccessTokens => Set<PersonalAccessTokenRow>();
    internal DbSet<TriggerOccurrenceRow> TriggerOccurrences => Set<TriggerOccurrenceRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var document = modelBuilder.Entity<ControlPlaneDocument>();
        document.ToTable("ControlPlaneResources");
        document.HasKey(value => value.StorageKey);
        document.Property(value => value.StorageKey).HasColumnName("ResourceId").HasMaxLength(1024);
        document.Property(value => value.LegacyResourceType).HasColumnName("ResourceType").HasMaxLength(256);
        document.Property(value => value.Kind).HasMaxLength(256);
        document.Property(value => value.Name).HasMaxLength(256);
        document.Property(value => value.Namespace).HasMaxLength(128);
        document.Property(value => value.TenantId);
        document.Property(value => value.WorkspaceId);
        document.Property(value => value.ScopeType).HasMaxLength(16);
        document.Property(value => value.ScopeKey).HasMaxLength(64);
        document.Property(value => value.ETag).HasMaxLength(64).IsConcurrencyToken();
        document.HasIndex(value => new { value.ScopeKey, value.Namespace, value.Kind, value.Name }).IsUnique();
        var occurrence = modelBuilder.Entity<TriggerOccurrenceRow>();
        occurrence.ToTable("TriggerOccurrences");
        occurrence.HasKey(value => value.Id);
        occurrence.Property(value => value.TriggerName).HasMaxLength(256);
        occurrence.Property(value => value.TriggerNamespace).HasMaxLength(128);
        occurrence.Property(value => value.Kind).HasMaxLength(32);
        occurrence.Property(value => value.Outcome).HasMaxLength(32);
        occurrence.Property(value => value.WorkItemId).HasMaxLength(128);
        occurrence.Property(value => value.ErrorCode).HasMaxLength(128);
        occurrence.HasIndex(value => new { value.WorkspaceId, value.TriggerUid, value.ScheduledAt });
        modelBuilder.ConfigureIdentityModel();
    }
}

internal sealed class ControlPlaneDocument
{
    public required string StorageKey { get; set; }
    public required string LegacyResourceType { get; set; }
    public Guid? Uid { get; set; }
    public string? Kind { get; set; }
    public string? Name { get; set; }
    public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
    public Guid? TenantId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string ScopeType { get; set; } = "workspace";
    public string ScopeKey { get; set; } = string.Empty;
    public required string Payload { get; set; }
    public required string ETag { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SqliteControlPlaneStore(
    IDbContextFactory<ControlPlaneDbContext> contextFactory,
    TimeProvider timeProvider,
    ICurrentRequestContext requestContext) : IControlPlaneStore, IAgentResourceQueries
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureResourceScopeSchemaAsync(context, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS PrincipalPreferences (
                PrincipalId TEXT NOT NULL CONSTRAINT PK_PrincipalPreferences PRIMARY KEY,
                PreferencesJson TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                CONSTRAINT FK_PrincipalPreferences_Users_PrincipalId FOREIGN KEY (PrincipalId) REFERENCES Users (Id) ON DELETE CASCADE
            )
            """,
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_ControlPlaneResources_AgentRevisionLookup ON ControlPlaneResources (TenantId, WorkspaceId, Kind, json_extract(Payload, '$.agentUid'), json_extract(Payload, '$.agentVersion'))",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS PersonalAccessTokens (
                Id TEXT NOT NULL CONSTRAINT PK_PersonalAccessTokens PRIMARY KEY,
                PrincipalId TEXT NOT NULL,
                WorkspaceId TEXT NOT NULL,
                Name TEXT NOT NULL,
                TokenPrefix TEXT NOT NULL,
                SecretHash BLOB NOT NULL,
                PermissionsJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                LastUsedAt TEXT NULL,
                RevokedAt TEXT NULL,
                CONSTRAINT FK_PersonalAccessTokens_Users_PrincipalId FOREIGN KEY (PrincipalId) REFERENCES Users (Id) ON DELETE CASCADE,
                CONSTRAINT FK_PersonalAccessTokens_Workspaces_WorkspaceId FOREIGN KEY (WorkspaceId) REFERENCES Workspaces (Id) ON DELETE CASCADE
            )
            """,
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_PersonalAccessTokens_TokenPrefix ON PersonalAccessTokens (TokenPrefix)",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_PersonalAccessTokens_PrincipalId ON PersonalAccessTokens (PrincipalId)",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_PersonalAccessTokens_WorkspaceId ON PersonalAccessTokens (WorkspaceId)",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_PersonalAccessTokens_ExpiresAt ON PersonalAccessTokens (ExpiresAt)",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_ControlPlaneResources_DeploymentRevision ON ControlPlaneResources (TenantId, WorkspaceId, Kind, json_extract(Payload, '$.revisionName'))",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_ControlPlaneResources_DeploymentAgent ON ControlPlaneResources (TenantId, WorkspaceId, Kind, json_extract(Payload, '$.agentName'))",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS TriggerOccurrences (
                Id TEXT NOT NULL CONSTRAINT PK_TriggerOccurrences PRIMARY KEY,
                TenantId TEXT NOT NULL,
                WorkspaceId TEXT NOT NULL,
                TriggerUid TEXT NOT NULL,
                TriggerName TEXT NOT NULL,
                TriggerNamespace TEXT NOT NULL,
                TriggerGeneration INTEGER NOT NULL,
                Kind TEXT NOT NULL,
                ScheduledAt TEXT NOT NULL,
                FiredAt TEXT NULL,
                Outcome TEXT NOT NULL,
                WorkItemId TEXT NULL,
                ErrorCode TEXT NULL,
                ErrorMessage TEXT NULL
            )
            """, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_TriggerOccurrences_WorkspaceId_TriggerUid_ScheduledAt ON TriggerOccurrences (WorkspaceId, TriggerUid, ScheduledAt)",
            cancellationToken);
    }

    public async Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource
    {
        return await GetExactAsync<T>(key.AtScope(LegacyExactScope()), cancellationToken);
    }

    public async Task<StoredResource<T>?> GetByUidAsync<T>(Guid uid, CancellationToken cancellationToken) where T : Resource
    {
        if (uid == Guid.Empty) throw new ArgumentException("A resource UID cannot be empty.", nameof(uid));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Uid == uid, cancellationToken);
        if (document is null || !CanRead(ScopeOf(document))) return null;
        return Deserialize<T>(document);
    }

    public async Task<StoredResource<T>?> GetExactAsync<T>(ScopedResourceAddress address, CancellationToken cancellationToken) where T : Resource
    {
        EnsureCanRead(address.Scope);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.ScopeKey == address.Scope.Key
            && value.Namespace == address.Namespace.Value && value.Kind == address.Kind && value.Name == address.Name, cancellationToken);
        return document is null ? null : Deserialize<T>(document);
    }

    public Task<IReadOnlyList<StoredResource<T>>> ListExactAsync<T>(ResourceScope scope, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
        ListScopedAsync<T>(scope, kind, skip, take, visible: false, cancellationToken);

    public Task<IReadOnlyList<StoredResource<T>>> ListVisibleAsync<T>(ResourceScope target, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
        ListScopedAsync<T>(target, kind, skip, take, visible: true, cancellationToken);

    public async Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 1000);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = Scoped(context.Documents.AsNoTracking()).Where(value => value.Kind == kind);
        var documents = await query.OrderBy(value => value.Namespace).ThenBy(value => value.Name).Skip(skip).Take(take).ToArrayAsync(cancellationToken);
        return documents.Select(Deserialize<T>).ToArray();
    }

    public async Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        => await PutExactAsync(ResolveWriteScope(resource), resource, ifMatch, ifNoneMatch, cancellationToken);

    public async Task<StoredResource<T>> PutExactAsync<T>(ResourceScope scope, T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
    {
        if (resource is AgentRevision) throw new InvalidOperationException("Published agent revisions are immutable and must be created through CreateImmutableAsync.");
        EnsureCanWrite(scope);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var namespaceValue = resource.Namespace.Value;
        ControlPlaneDocument? byUid = null;
        if (resource.Uid != Guid.Empty)
            byUid = await context.Documents.SingleOrDefaultAsync(value => value.Uid == resource.Uid, cancellationToken);
        if (byUid is not null && ScopeOf(byUid) != scope)
            throw new ControlPlaneConcurrencyException("The ownership scope of an existing resource is immutable.");
        var existing = byUid ?? await context.Documents.SingleOrDefaultAsync(
            value => value.ScopeKey == scope.Key && value.Namespace == namespaceValue && value.Kind == resource.Kind && value.Name == resource.Metadata.Name, cancellationToken);
        if (existing is null && ifMatch is not null) throw new ControlPlaneConcurrencyException("If-Match cannot update a resource that does not exist.");
        if (existing is not null && ifNoneMatch) throw new ControlPlaneConcurrencyException("If-None-Match prevented replacement of an existing resource.");
        if (existing is not null && ifMatch is not null && !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
            throw new ControlPlaneConcurrencyException("The supplied ETag does not match the current resource version.");

        var etag = NewETag();
        var now = timeProvider.GetUtcNow();
        var uid = existing?.Uid ?? Guid.NewGuid();
        if (existing is not null && resource.Uid != Guid.Empty && resource.Uid != uid)
            throw new ControlPlaneConcurrencyException("The UID of an existing resource is immutable.");
        var versioned = ApplySystemState(resource, uid, scope.TenantId, scope.WorkspaceId, etag);
        if (existing is null)
        {
            context.Documents.Add(new ControlPlaneDocument
            {
                StorageKey = uid.ToString("N"),
                LegacyResourceType = resource.Kind,
                Uid = uid,
                Kind = resource.Kind,
                Name = resource.Metadata.Name,
                Namespace = namespaceValue,
                TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId,
                ScopeType = ScopeName(scope.Type),
                ScopeKey = scope.Key,
                Payload = JsonSerializer.Serialize(versioned, JsonOptions),
                ETag = etag,
                UpdatedAt = now
            });
        }
        else
        {
            existing.LegacyResourceType = resource.Kind;
            existing.Kind = resource.Kind;
            existing.Name = resource.Metadata.Name;
            existing.Namespace = namespaceValue;
            existing.TenantId = scope.TenantId;
            existing.WorkspaceId = scope.WorkspaceId;
            existing.ScopeType = ScopeName(scope.Type);
            existing.ScopeKey = scope.Key;
            existing.Payload = JsonSerializer.Serialize(versioned, JsonOptions);
            existing.ETag = etag;
            existing.UpdatedAt = now;
        }
        await SaveAsync(context, cancellationToken);
        return new StoredResource<T>(versioned, etag, now);
    }

    public async Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var etag = NewETag();
        var now = timeProvider.GetUtcNow();
        var uid = Guid.NewGuid();
        var scope = ResolveScope(resource);
        EnsureCanWrite(scope);
        var versioned = ApplySystemState(resource, uid, scope.TenantId, scope.WorkspaceId, etag);
        context.Documents.Add(new ControlPlaneDocument
        {
            StorageKey = uid.ToString("N"),
            LegacyResourceType = resource.Kind,
            Uid = uid,
            Kind = resource.Kind,
            Name = resource.Metadata.Name,
            Namespace = resource.Namespace.Value,
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            ScopeType = ScopeName(scope.Type),
            ScopeKey = scope.Key,
            Payload = JsonSerializer.Serialize(versioned, JsonOptions),
            ETag = etag,
            UpdatedAt = now
        });
        try { await SaveAsync(context, cancellationToken); }
        catch (ControlPlaneConcurrencyException exception) { throw new ControlPlaneConcurrencyException($"Immutable resource '{resource.Kind}/{resource.Metadata.Name}' already exists: {exception.Message}"); }
        return new StoredResource<T>(versioned, etag, now);
    }

    public async Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken)
        => await DeleteExactAsync(key.AtScope(LegacyExactScope()), ifMatch, cancellationToken);

    public async Task DeleteExactAsync(ScopedResourceAddress address, string? ifMatch, CancellationToken cancellationToken)
    {
        EnsureCanWrite(address.Scope);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.Documents.SingleOrDefaultAsync(value => value.ScopeKey == address.Scope.Key && value.Namespace == address.Namespace.Value && value.Kind == address.Kind && value.Name == address.Name, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new ResourceKey(address.Kind, address.Name, address.Namespace));
        if (ifMatch is not null && !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
            throw new ControlPlaneConcurrencyException("The supplied ETag does not match the current resource version.");
        context.Documents.Remove(existing);
        await SaveAsync(context, cancellationToken);
    }

    private static StoredResource<T> Deserialize<T>(ControlPlaneDocument document) where T : Resource
    {
        var value = JsonSerializer.Deserialize<T>(document.Payload, JsonOptions)
            ?? throw new InvalidOperationException($"Stored resource '{document.Kind}/{document.Name}' is invalid.");
        value = ApplySystemState(value, document.Uid ?? value.Uid, document.TenantId ?? Guid.Empty, document.WorkspaceId ?? Guid.Empty, document.ETag);
        return new StoredResource<T>(value, document.ETag, document.UpdatedAt);
    }

    public Task<IReadOnlyList<StoredResource<T>>> ListAllAsync<T>(string kind, CancellationToken cancellationToken) where T : Resource =>
        LoadKindAsync<T>(kind, cancellationToken);

    public async Task<StoredResource<AgentRevision>?> FindRevisionAsync(Guid agentUid, long generation, CancellationToken cancellationToken) =>
        (await LoadFilteredAsync<AgentRevision>($"""
            SELECT * FROM ControlPlaneResources
            WHERE Kind = {ResourceKinds.AgentRevision}
              AND json_extract(Payload, '$.agentUid') = {agentUid.ToString()}
              AND json_extract(Payload, '$.agentVersion') = {generation}
            """, cancellationToken)).SingleOrDefault();

    public async Task<StoredResource<AgentRevision>?> FindLatestRevisionAsync(Guid agentUid, CancellationToken cancellationToken)
    {
        var results = requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.System => await LoadFilteredAsync<AgentRevision>($"""
                SELECT * FROM ControlPlaneResources
                WHERE Kind = {ResourceKinds.AgentRevision}
                  AND json_extract(Payload, '$.agentUid') = {agentUid.ToString()}
                ORDER BY json_extract(Payload, '$.agentVersion') DESC,
                         json_extract(Payload, '$.createdAt') DESC
                LIMIT 1
                """, cancellationToken),
            ControlPlaneAccessMode.Workspace => await LoadFilteredAsync<AgentRevision>($"""
                SELECT * FROM ControlPlaneResources
                WHERE TenantId = {requestContext.Current.TenantId}
                  AND WorkspaceId = {requestContext.Current.WorkspaceId}
                  AND Kind = {ResourceKinds.AgentRevision}
                  AND json_extract(Payload, '$.agentUid') = {agentUid.ToString()}
                ORDER BY json_extract(Payload, '$.agentVersion') DESC,
                         json_extract(Payload, '$.createdAt') DESC
                LIMIT 1
                """, cancellationToken),
            _ => throw new InvalidOperationException("Control Plane access requires an explicit workspace or system context.")
        };
        return results.SingleOrDefault();
    }

    public Task<StoredResource<AgentDeployment>?> FindDeploymentByRevisionAsync(string revisionName, CancellationToken cancellationToken) =>
        FindDeploymentByRevisionAsync(ResourceNamespace.Default, revisionName, cancellationToken);

    public async Task<StoredResource<AgentDeployment>?> FindDeploymentByRevisionAsync(ResourceNamespace @namespace, string revisionName, CancellationToken cancellationToken)
    {
        var namespaceValue = @namespace.Value;
        var results = requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.System => await LoadFilteredAsync<AgentDeployment>($"""
                SELECT * FROM ControlPlaneResources
                WHERE Kind = {ResourceKinds.AgentDeployment}
                  AND Namespace = {namespaceValue}
                  AND json_extract(Payload, '$.revisionName') = {revisionName}
                ORDER BY json_extract(Payload, '$.updatedAt') DESC
                LIMIT 1
                """, cancellationToken),
            ControlPlaneAccessMode.Workspace => await LoadFilteredAsync<AgentDeployment>($"""
                SELECT * FROM ControlPlaneResources
                WHERE TenantId = {requestContext.Current.TenantId}
                  AND WorkspaceId = {requestContext.Current.WorkspaceId}
                  AND Kind = {ResourceKinds.AgentDeployment}
                  AND Namespace = {namespaceValue}
                  AND json_extract(Payload, '$.revisionName') = {revisionName}
                ORDER BY json_extract(Payload, '$.updatedAt') DESC
                LIMIT 1
                """, cancellationToken),
            _ => throw new InvalidOperationException("Control Plane access requires an explicit workspace or system context.")
        };
        return results.SingleOrDefault();
    }

    public Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsForAgentAsync(string agentName, CancellationToken cancellationToken) =>
        ListDeploymentsForAgentAsync(ResourceNamespace.Default, agentName, cancellationToken);

    public async Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsForAgentAsync(ResourceNamespace @namespace, string agentName, CancellationToken cancellationToken) =>
        (await LoadFilteredAsync<AgentDeployment>($"""
            SELECT * FROM ControlPlaneResources
            WHERE Kind = {ResourceKinds.AgentDeployment}
              AND Namespace = {@namespace.Value}
              AND json_extract(Payload, '$.agentName') = {agentName}
            """, cancellationToken))
        .OrderByDescending(value => value.Value.UpdatedAt)
        .ToArray();

    public Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsAsync(CancellationToken cancellationToken) =>
        LoadKindAsync<AgentDeployment>(ResourceKinds.AgentDeployment, cancellationToken);

    private async Task<IReadOnlyList<StoredResource<T>>> LoadKindAsync<T>(string kind, CancellationToken cancellationToken) where T : Resource
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await Scoped(context.Documents.AsNoTracking())
            .Where(value => value.Kind == kind)
            .OrderBy(value => value.Namespace).ThenBy(value => value.Name)
            .ToArrayAsync(cancellationToken);
        return documents.Select(Deserialize<T>).ToArray();
    }

    private async Task<IReadOnlyList<StoredResource<T>>> LoadFilteredAsync<T>(FormattableString sql, CancellationToken cancellationToken) where T : Resource
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await Scoped(context.Documents.FromSqlInterpolated(sql).AsNoTracking())
            .ToArrayAsync(cancellationToken);
        return documents.Select(Deserialize<T>).ToArray();
    }

    private IQueryable<ControlPlaneDocument> Scoped(IQueryable<ControlPlaneDocument> query)
    {
        return requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.System => query,
            ControlPlaneAccessMode.Tenant => query.Where(value => value.ScopeKey == ResourceScope.Tenant(requestContext.Current.TenantId).Key),
            ControlPlaneAccessMode.Workspace => query.Where(value => value.TenantId == requestContext.Current.TenantId
                && value.WorkspaceId == requestContext.Current.WorkspaceId),
            _ => throw new InvalidOperationException("Control Plane access requires an explicit workspace or system context.")
        };
    }

    private ResourceScope ResolveScope(Resource resource) => ResolveWriteScope(resource);

    private ResourceScope ResolveWriteScope(Resource resource)
    {
        if (requestContext.AccessMode == ControlPlaneAccessMode.Workspace)
        {
            var current = requestContext.Current;
            if (resource.TenantId != Guid.Empty && resource.TenantId != current.TenantId
                || resource.WorkspaceId != Guid.Empty && resource.WorkspaceId != current.WorkspaceId)
                throw new InvalidOperationException("A workspace-scoped operation cannot write a resource into another scope.");
            return ResourceScope.Workspace(current.TenantId, current.WorkspaceId);
        }
        if (requestContext.AccessMode == ControlPlaneAccessMode.Tenant)
        {
            var current = requestContext.Current;
            if (resource.TenantId != Guid.Empty && resource.TenantId != current.TenantId || resource.WorkspaceId != Guid.Empty)
                throw new InvalidOperationException("A tenant-scoped operation cannot write a resource into another scope.");
            return ResourceScope.Tenant(current.TenantId);
        }
        if (requestContext.AccessMode == ControlPlaneAccessMode.System)
        {
            if (resource.WorkspaceId != Guid.Empty) return ResourceScope.Workspace(resource.TenantId, resource.WorkspaceId);
            if (resource.TenantId != Guid.Empty) return ResourceScope.Tenant(resource.TenantId);
            return ResourceScope.Instance;
        }
        throw new InvalidOperationException("Control Plane access requires an explicit workspace or system context.");
    }

    private async Task<IReadOnlyList<StoredResource<T>>> ListScopedAsync<T>(ResourceScope target, string kind, int skip, int take, bool visible, CancellationToken cancellationToken) where T : Resource
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        EnsureCanRead(target);
        take = Math.Min(take, 1000);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Documents.AsNoTracking().Where(value => value.Kind == kind);
        query = visible ? VisibleFrom(query, target) : query.Where(value => value.ScopeKey == target.Key);
        var documents = await query.OrderBy(value => value.ScopeType).ThenBy(value => value.ScopeKey).ThenBy(value => value.Namespace).ThenBy(value => value.Name).Skip(skip).Take(take).ToArrayAsync(cancellationToken);
        return documents.Select(Deserialize<T>).ToArray();
    }

    private static IQueryable<ControlPlaneDocument> VisibleFrom(IQueryable<ControlPlaneDocument> query, ResourceScope target) => target.Type switch
    {
        ResourceScopeType.Instance => query.Where(value => value.ScopeKey == "instance"),
        ResourceScopeType.Tenant => query.Where(value => value.ScopeKey == "instance" || value.ScopeKey == target.Key),
        ResourceScopeType.Workspace => query.Where(value => value.ScopeKey == "instance" || value.ScopeKey == ResourceScope.Tenant(target.TenantId).Key || value.ScopeKey == target.Key),
        _ => throw new InvalidOperationException($"Unsupported resource scope type '{target.Type}'.")
    };

    private ResourceScope LegacyExactScope() => requestContext.AccessMode switch
    {
        ControlPlaneAccessMode.System => ResourceScope.Instance,
        ControlPlaneAccessMode.Tenant => ResourceScope.Tenant(requestContext.Current.TenantId),
        ControlPlaneAccessMode.Workspace => ResourceScope.Workspace(requestContext.Current.TenantId, requestContext.Current.WorkspaceId),
        _ => throw new InvalidOperationException("Control Plane access requires an explicit tenant, workspace, or system context.")
    };

    private bool CanRead(ResourceScope scope) => requestContext.AccessMode switch
    {
        ControlPlaneAccessMode.System => true,
        ControlPlaneAccessMode.Tenant => scope.IsVisibleFrom(ResourceScope.Tenant(requestContext.Current.TenantId)),
        ControlPlaneAccessMode.Workspace => scope.IsVisibleFrom(ResourceScope.Workspace(requestContext.Current.TenantId, requestContext.Current.WorkspaceId)),
        _ => false
    };

    private void EnsureCanRead(ResourceScope scope)
    {
        if (!CanRead(scope)) throw new InvalidOperationException($"The current Control Plane context cannot read scope '{scope}'.");
    }

    private void EnsureCanWrite(ResourceScope scope)
    {
        var allowed = requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.System => true,
            ControlPlaneAccessMode.Tenant => scope == ResourceScope.Tenant(requestContext.Current.TenantId),
            ControlPlaneAccessMode.Workspace => scope == ResourceScope.Workspace(requestContext.Current.TenantId, requestContext.Current.WorkspaceId),
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"The current Control Plane context cannot write scope '{scope}'.");
    }

    private static ResourceScope ScopeOf(ControlPlaneDocument document) => ResourceScope.From(
        Enum.Parse<ResourceScopeType>(document.ScopeType, ignoreCase: true), document.TenantId ?? Guid.Empty, document.WorkspaceId ?? Guid.Empty);

    private static string ScopeName(ResourceScopeType type) => type.ToString().ToLowerInvariant();

    private static async Task EnsureResourceScopeSchemaAsync(ControlPlaneDbContext context, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(ControlPlaneResources)";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await columns.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(1));
        if (!names.Contains("ScopeType")) await context.Database.ExecuteSqlRawAsync("ALTER TABLE ControlPlaneResources ADD COLUMN ScopeType TEXT NOT NULL DEFAULT 'workspace'", cancellationToken);
        if (!names.Contains("ScopeKey")) await context.Database.ExecuteSqlRawAsync("ALTER TABLE ControlPlaneResources ADD COLUMN ScopeKey TEXT NOT NULL DEFAULT ''", cancellationToken);
        await context.Database.ExecuteSqlRawAsync("""
            UPDATE ControlPlaneResources
            SET ScopeType = CASE WHEN WorkspaceId IS NOT NULL AND WorkspaceId <> '00000000-0000-0000-0000-000000000000' THEN 'workspace' WHEN TenantId IS NOT NULL AND TenantId <> '00000000-0000-0000-0000-000000000000' THEN 'tenant' ELSE 'instance' END,
                ScopeKey = CASE WHEN WorkspaceId IS NOT NULL AND WorkspaceId <> '00000000-0000-0000-0000-000000000000' THEN 'workspace:' || lower(WorkspaceId) WHEN TenantId IS NOT NULL AND TenantId <> '00000000-0000-0000-0000-000000000000' THEN 'tenant:' || lower(TenantId) ELSE 'instance' END
            WHERE ScopeKey = ''
            """, cancellationToken);
        await context.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS IX_ControlPlaneResources_WorkspaceId_Namespace_Kind_Name", cancellationToken);
        await context.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_ControlPlaneResources_ScopeKey_Namespace_Kind_Name ON ControlPlaneResources (ScopeKey, Namespace, Kind, Name)", cancellationToken);
    }

    private static async Task SaveAsync(ControlPlaneDbContext context, CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new ControlPlaneConcurrencyException(exception.InnerException?.Message ?? exception.Message); }
    }

    private static T ApplySystemState<T>(T resource, Guid uid, Guid tenantId, Guid workspaceId, string etag) where T : Resource =>
        (T)resource.WithSystemState(uid, tenantId, workspaceId, etag);

    private static string NewETag() => $"\"{Guid.NewGuid():N}\"";

}

public static class SqliteControlPlaneServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteControlPlane(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.TryAddSingleton<ICurrentRequestContext, UnavailableRequestContext>();
        services.AddDbContextFactory<ControlPlaneDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IControlPlaneStore, SqliteControlPlaneStore>();
        services.AddSingleton<IAgentResourceQueries>(provider => (SqliteControlPlaneStore)provider.GetRequiredService<IControlPlaneStore>());
        services.AddSingleton<ITriggerOccurrenceStore, SqliteTriggerOccurrenceStore>();
        services.AddSingleton<SqliteIdentityStore>();
        services.AddSingleton<IIdentityStore>(provider => provider.GetRequiredService<SqliteIdentityStore>());
        services.AddSingleton<ISecurityAuditStore>(provider => provider.GetRequiredService<SqliteIdentityStore>());
        services.AddSingleton<IPersonalAccessTokenStore>(provider => provider.GetRequiredService<SqliteIdentityStore>());
        return services;
    }
}
