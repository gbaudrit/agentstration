using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Security.Contracts;
using Agentstration.Sources.Contracts;
using Agentstration.Triggers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.ResourceManagement.Storage.Sqlite;

public sealed class ResourceManagementDbContext(DbContextOptions<ResourceManagementDbContext> options) : DbContext(options)
{
    internal DbSet<ControlPlaneDocument> Documents => Set<ControlPlaneDocument>();
    internal DbSet<ResourceScopeRow> ResourceScopes => Set<ResourceScopeRow>();
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
    internal DbSet<ResourceAccessPolicyRow> ResourceAccessPolicies => Set<ResourceAccessPolicyRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var document = modelBuilder.Entity<ControlPlaneDocument>();
        document.ToTable("ControlPlaneResources");
        document.HasKey(value => value.StorageKey);
        document.Property(value => value.StorageKey).HasColumnName("ResourceId").HasMaxLength(1024);
        document.Property(value => value.Kind).HasMaxLength(256);
        document.Property(value => value.Name).HasMaxLength(256);
        document.Property(value => value.Namespace).HasMaxLength(128);
        document.Property(value => value.ETag).HasMaxLength(64).IsConcurrencyToken();
        document.HasIndex(value => value.Uid).IsUnique();
        document.HasIndex(value => new { value.ScopeId, value.Namespace, value.Kind, value.Name }).IsUnique();
        document.HasOne(value => value.Scope).WithMany().HasForeignKey(value => value.ScopeId).OnDelete(DeleteBehavior.Restrict);
        var resourceScope = modelBuilder.Entity<ResourceScopeRow>();
        resourceScope.ToTable("ResourceScopes");
        resourceScope.HasKey(value => value.Id);
        resourceScope.Property(value => value.Ref).HasMaxLength(128);
        resourceScope.Property(value => value.Kind).HasMaxLength(32);
        resourceScope.Property(value => value.TargetKey).HasMaxLength(64);
        resourceScope.HasIndex(value => value.Ref).IsUnique();
        resourceScope.HasIndex(value => new { value.Kind, value.TargetKey }).IsUnique();
        resourceScope.HasIndex(value => value.ParentScopeId);
        resourceScope.HasOne<ResourceScopeRow>().WithMany().HasForeignKey(value => value.ParentScopeId).OnDelete(DeleteBehavior.Restrict);
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
        var accessPolicy = modelBuilder.Entity<ResourceAccessPolicyRow>();
        accessPolicy.ToTable("ResourceAccessPolicies");
        accessPolicy.HasKey(value => value.ResourceUid);
        accessPolicy.Property(value => value.OwnerScopeRef).HasMaxLength(128);
        accessPolicy.Property(value => value.Namespace).HasMaxLength(128);
        accessPolicy.Property(value => value.Kind).HasMaxLength(256);
        accessPolicy.Property(value => value.Version).HasMaxLength(128);
        accessPolicy.Property(value => value.ETag).HasMaxLength(64).IsConcurrencyToken();
        accessPolicy.HasIndex(value => value.OwnerScopeRef);
        modelBuilder.ConfigureIdentityModel();
    }
}

internal sealed class ControlPlaneDocument
{
    public required string StorageKey { get; set; }
    public Guid Uid { get; set; }
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
    public long ScopeId { get; set; }
    public ResourceScopeRow Scope { get; set; } = null!;
    public required string Payload { get; set; }
    public required string ETag { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ResourceScopeRow
{
    public long Id { get; set; }
    public required string Ref { get; set; }
    public required string Kind { get; set; }
    public required string TargetKey { get; set; }
    public long? ParentScopeId { get; set; }
}

public sealed class SqliteResourceStore(
    IDbContextFactory<ResourceManagementDbContext> contextFactory,
    TimeProvider timeProvider,
    ICurrentRequestContext requestContext,
    IResourceGovernanceContext governanceContext,
    ISecurityAuditWriter? audit = null) : IResourceStore, IAgentResourceQueries, IResourceScopeResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        try
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT OR IGNORE INTO ResourceScopes (Ref, Kind, TargetKey, ParentScopeId)
                VALUES ({ResourceScopeRef.Instance.Value}, {"instance"}, {ResourceScopeRef.Instance.TargetKey}, NULL)
                """, cancellationToken);
            _ = await context.ResourceAccessPolicies.AsNoTracking().AnyAsync(cancellationToken);
        }
        catch (System.Data.Common.DbException exception)
        {
            throw new InvalidOperationException("The Management database schema is incompatible with current resource scopes and access policies. Delete the pre-release database and reseed the instance.", exception);
        }
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_ControlPlaneResources_AgentRevisionLookup ON ControlPlaneResources (ScopeId, Kind, json_extract(Payload, '$.agentUid'), json_extract(Payload, '$.agentVersion'))",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_ControlPlaneResources_DeploymentRevision ON ControlPlaneResources (ScopeId, Kind, json_extract(Payload, '$.revisionName'))",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_ControlPlaneResources_DeploymentAgent ON ControlPlaneResources (ScopeId, Kind, json_extract(Payload, '$.agentName'))",
            cancellationToken);
    }

    public async Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Documents.AsNoTracking().Include(value => value.Scope)
            .Where(value => value.Namespace == key.Namespace.Value && value.Kind == key.Kind && value.Name == key.Name);
        if (requestContext.AccessMode != ControlPlaneAccessMode.System)
        {
            var target = await RequireScopeAsync(context, CurrentScopeRef(), cancellationToken);
            var visibleScopeIds = await EffectiveVisibleScopeIdsAsync(context, target, cancellationToken);
            query = query.Where(value => visibleScopeIds.Contains(value.ScopeId));
        }
        var matches = await FilterDiscoverableAsync(context,
            await query.ToArrayAsync(cancellationToken), cancellationToken);
        return matches.Length switch
        {
            0 => null,
            1 => Deserialize<T>(matches[0]),
            _ => throw new AmbiguousResourceException(key)
        };
    }

    public async Task<StoredResource<T>?> GetByUidAsync<T>(Guid uid, CancellationToken cancellationToken) where T : Resource
    {
        if (uid == Guid.Empty) throw new ArgumentException("A resource UID cannot be empty.", nameof(uid));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().Include(value => value.Scope).SingleOrDefaultAsync(value => value.Uid == uid, cancellationToken);
        if (document is null || !await CanReadAsync(context, document.Scope, cancellationToken)
            || !await CanDiscoverAsync(context, document, cancellationToken)) return null;
        return Deserialize<T>(document);
    }

    public async Task<ResourceIdentitySnapshot?> GetIdentityByUidAsync(Guid uid, CancellationToken cancellationToken)
    {
        if (uid == Guid.Empty) throw new ArgumentException("A resource UID cannot be empty.", nameof(uid));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().Include(value => value.Scope)
            .SingleOrDefaultAsync(value => value.Uid == uid, cancellationToken);
        if (document is null || !await CanReadAsync(context, document.Scope, cancellationToken)
            || !await CanDiscoverAsync(context, document, cancellationToken)) return null;
        using var payload = JsonDocument.Parse(document.Payload);
        var root = payload.RootElement;
        var generation = root.TryGetProperty("generation", out var generationValue)
            && generationValue.TryGetInt64(out var parsedGeneration) ? parsedGeneration : 0;
        var version = root.TryGetProperty("version", out var versionValue)
            && versionValue.ValueKind == JsonValueKind.String ? versionValue.GetString() : null;
        return new(document.Uid, ResourceScopeRef.Parse(document.Scope.Ref),
            ResourceNamespace.Parse(document.Namespace), document.Kind, document.Name, generation, version, document.ETag);
    }

    public async Task<StoredResource<T>?> GetExactAsync<T>(ScopedResourceAddress address, CancellationToken cancellationToken) where T : Resource
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var scope = await RequireScopeAsync(context, address.ScopeRef, cancellationToken);
        await EnsureCanReadAsync(context, scope, cancellationToken);
        var document = await context.Documents.AsNoTracking().Include(value => value.Scope).SingleOrDefaultAsync(value => value.ScopeId == scope.Id
            && value.Namespace == address.Namespace.Value && value.Kind == address.Kind && value.Name == address.Name, cancellationToken);
        return document is null || !await CanDiscoverAsync(context, document, cancellationToken)
            ? null
            : Deserialize<T>(document);
    }

    public Task<IReadOnlyList<StoredResource<T>>> ListExactAsync<T>(ResourceScopeRef scopeRef, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
        ListScopedAsync<T>(scopeRef, kind, skip, take, visible: false, cancellationToken);

    public Task<IReadOnlyList<StoredResource<T>>> ListVisibleAsync<T>(ResourceScopeRef targetScopeRef, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
        ListScopedAsync<T>(targetScopeRef, kind, skip, take, visible: true, cancellationToken);

    public async Task<IReadOnlyList<ResourceInventoryEntry>> ListExactInventoryAsync(
        ResourceScopeRef scopeRef,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 1000);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var scope = await RequireScopeAsync(context, scopeRef, cancellationToken);
        await EnsureCanReadAsync(context, scope, cancellationToken);
        var documents = await context.Documents.AsNoTracking()
            .Where(value => value.ScopeId == scope.Id)
            .OrderBy(value => value.Kind)
            .ThenBy(value => value.Namespace)
            .ThenBy(value => value.Name)
            .Skip(skip)
            .Take(take)
            .ToArrayAsync(cancellationToken);
        return documents.Select(value => new ResourceInventoryEntry(
            value.Uid,
            scopeRef,
            ResourceNamespace.Parse(value.Namespace),
            value.Kind,
            value.Name,
            value.UpdatedAt)).ToArray();
    }

    public async Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 1000);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = Scoped(context.Documents.AsNoTracking()).Where(value => value.Kind == kind);
        var candidates = await query.Include(value => value.Scope).OrderBy(value => value.Namespace).ThenBy(value => value.Name).ToArrayAsync(cancellationToken);
        var documents = (await FilterDiscoverableAsync(context, candidates, cancellationToken)).Skip(skip).Take(take);
        return documents.Select(Deserialize<T>).ToArray();
    }

    public async Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        => await PutExactAsync(ResolveWriteScopeRef(resource), resource, ifMatch, ifNoneMatch, cancellationToken);

    public async Task<StoredResource<T>> PutExactAsync<T>(ResourceScopeRef scopeRef, T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
    {
        if (resource is IImmutableResource or AgentRevision or SourceResource or SourceVersionResource or SourceChannelSnapshotResource)
            throw new InvalidOperationException($"Resource kind '{resource.Kind}' is immutable and must be created through CreateImmutableAsync.");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var scope = await RequireScopeAsync(context, scopeRef, cancellationToken);
        EnsureCanWrite(scopeRef);
        var namespaceValue = resource.Namespace.Value;
        var existing = await context.Documents.SingleOrDefaultAsync(
            value => value.ScopeId == scope.Id && value.Namespace == namespaceValue && value.Kind == resource.Kind && value.Name == resource.Metadata.Name, cancellationToken);
        if (existing is null && resource.Uid != Guid.Empty && !ifNoneMatch)
        {
            var byUid = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Uid == resource.Uid, cancellationToken);
            if (byUid is not null && byUid.ScopeId != scope.Id)
                throw new ResourceConcurrencyException("The ownership scope of an existing resource is immutable.");
        }
        if (existing is null && ifMatch is not null) throw new ResourceConcurrencyException("If-Match cannot update a resource that does not exist.");
        if (existing is not null && ifNoneMatch) throw new ResourceConcurrencyException("If-None-Match prevented replacement of an existing resource.");
        if (existing is not null && ifMatch is not null && !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
            throw new ResourceConcurrencyException("The supplied ETag does not match the current resource version.");

        resource = await ApplyGovernanceAsync(resource, existing is null ? null : DeserializeValue<T>(existing), cancellationToken);

        var etag = NewETag();
        var now = timeProvider.GetUtcNow();
        var uid = existing?.Uid ?? Guid.NewGuid();
        if (existing is not null && resource.Uid != Guid.Empty && resource.Uid != uid)
            throw new ResourceConcurrencyException("The UID of an existing resource is immutable.");
        var versioned = ApplySystemState(resource, uid, scopeRef, etag);
        if (existing is null)
        {
            context.Documents.Add(new ControlPlaneDocument
            {
                StorageKey = uid.ToString("N"),
                Uid = uid,
                Kind = resource.Kind,
                Name = resource.Metadata.Name,
                Namespace = namespaceValue,
                ScopeId = scope.Id,
                Scope = scope,
                Payload = JsonSerializer.Serialize(versioned, JsonOptions),
                ETag = etag,
                UpdatedAt = now
            });
        }
        else
        {
            existing.Kind = resource.Kind;
            existing.Name = resource.Metadata.Name;
            existing.Namespace = namespaceValue;
            existing.ScopeId = scope.Id;
            existing.Scope = scope;
            existing.Payload = JsonSerializer.Serialize(versioned, JsonOptions);
            existing.ETag = etag;
            existing.UpdatedAt = now;
        }
        await SaveAsync(context, cancellationToken);
        await AuditReconciliationAsync(versioned.Governance, cancellationToken);
        return new StoredResource<T>(versioned, etag, now);
    }

    public async Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource
    {
        resource = await ApplyGovernanceAsync(resource, null, cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var etag = NewETag();
        var now = timeProvider.GetUtcNow();
        var uid = Guid.NewGuid();
        var scopeRef = ResolveWriteScopeRef(resource);
        EnsureCanWrite(scopeRef);
        var scope = await RequireScopeAsync(context, scopeRef, cancellationToken);
        var versioned = ApplySystemState(resource, uid, scopeRef, etag);
        context.Documents.Add(new ControlPlaneDocument
        {
            StorageKey = uid.ToString("N"),
            Uid = uid,
            Kind = resource.Kind,
            Name = resource.Metadata.Name,
            Namespace = resource.Namespace.Value,
            ScopeId = scope.Id,
            Scope = scope,
            Payload = JsonSerializer.Serialize(versioned, JsonOptions),
            ETag = etag,
            UpdatedAt = now
        });
        try { await SaveAsync(context, cancellationToken); }
        catch (ResourceConcurrencyException exception) { throw new ResourceConcurrencyException($"Immutable resource '{resource.Kind}/{resource.Metadata.Name}' already exists: {exception.Message}"); }
        await AuditReconciliationAsync(versioned.Governance, cancellationToken);
        return new StoredResource<T>(versioned, etag, now);
    }

    public async Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken)
    {
        if (requestContext.AccessMode != ControlPlaneAccessMode.System)
        {
            await DeleteExactAsync(key.AtScope(CurrentScopeRef()), ifMatch, cancellationToken);
            return;
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var matches = await context.Documents.Where(value => value.Namespace == key.Namespace.Value && value.Kind == key.Kind && value.Name == key.Name).Take(2).ToArrayAsync(cancellationToken);
        var existing = matches.Length switch
        {
            0 => throw new ResourceNotFoundException(key),
            1 => matches[0],
            _ => throw new AmbiguousResourceException(key)
        };
        if (ifMatch is not null && !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
            throw new ResourceConcurrencyException("The supplied ETag does not match the current resource version.");
        var governance = ReadGovernance(existing.Payload);
        await EnsureManagedMutationAsync(governance, cancellationToken);
        var accessPolicy = await context.ResourceAccessPolicies
            .SingleOrDefaultAsync(value => value.ResourceUid == existing.Uid, cancellationToken);
        if (accessPolicy is not null) context.ResourceAccessPolicies.Remove(accessPolicy);
        context.Documents.Remove(existing);
        await SaveAsync(context, cancellationToken);
        await AuditReconciliationAsync(governance, cancellationToken);
    }

    public async Task DeleteExactAsync(ScopedResourceAddress address, string? ifMatch, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        EnsureCanWrite(address.ScopeRef);
        var scope = await RequireScopeAsync(context, address.ScopeRef, cancellationToken);
        var existing = await context.Documents.SingleOrDefaultAsync(value => value.ScopeId == scope.Id && value.Namespace == address.Namespace.Value && value.Kind == address.Kind && value.Name == address.Name, cancellationToken)
            ?? throw new ResourceNotFoundException(new ResourceKey(address.Kind, address.Name, address.Namespace));
        if (ifMatch is not null && !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
            throw new ResourceConcurrencyException("The supplied ETag does not match the current resource version.");
        var governance = ReadGovernance(existing.Payload);
        await EnsureManagedMutationAsync(governance, cancellationToken);
        var accessPolicy = await context.ResourceAccessPolicies
            .SingleOrDefaultAsync(value => value.ResourceUid == existing.Uid, cancellationToken);
        if (accessPolicy is not null) context.ResourceAccessPolicies.Remove(accessPolicy);
        context.Documents.Remove(existing);
        await SaveAsync(context, cancellationToken);
        await AuditReconciliationAsync(governance, cancellationToken);
    }

    private static StoredResource<T> Deserialize<T>(ControlPlaneDocument document) where T : Resource
    {
        var value = DeserializeValue<T>(document);
        value = ApplySystemState(value, document.Uid, ResourceScopeRef.Parse(document.Scope.Ref), document.ETag);
        return new StoredResource<T>(value, document.ETag, document.UpdatedAt);
    }

    private async Task<T> ApplyGovernanceAsync<T>(T resource, T? existing, CancellationToken cancellationToken) where T : Resource
    {
        try
        {
            var governance = existing is null
                ? ResourceGovernanceEnforcement.PrepareCreate(resource.Governance, governanceContext.Current)
                : ResourceGovernanceEnforcement.PrepareUpdate(existing.Governance, resource.Governance, governanceContext.Current);
            return (T)resource.WithGovernance(governance);
        }
        catch (ResourceGovernanceViolationException exception)
        {
            await AuditDeniedAsync(exception.Code, cancellationToken);
            throw new ResourceConcurrencyException($"{exception.Code}: {exception.Message}");
        }
    }

    private async Task EnsureManagedMutationAsync(ResourceGovernance governance, CancellationToken cancellationToken)
    {
        try { ResourceGovernanceEnforcement.EnsureManager(governance, governanceContext.Current); }
        catch (ResourceGovernanceViolationException exception)
        {
            await AuditDeniedAsync(exception.Code, cancellationToken);
            throw new ResourceConcurrencyException($"{exception.Code}: {exception.Message}");
        }
    }

    private Task AuditReconciliationAsync(ResourceGovernance governance, CancellationToken cancellationToken) =>
        audit is null || !governance.IsManaged
            ? Task.CompletedTask
            : audit.WriteAsync(new(SecurityAuditActions.ManagedResourceReconciled), cancellationToken);

    private Task AuditDeniedAsync(string reasonCode, CancellationToken cancellationToken) =>
        audit?.WriteAsync(new(SecurityAuditActions.ManagedResourceMutationDenied, SecurityAuditOutcome.Failed, ReasonCode: reasonCode), cancellationToken)
        ?? Task.CompletedTask;

    private static T DeserializeValue<T>(ControlPlaneDocument document) where T : Resource =>
        JsonSerializer.Deserialize<T>(document.Payload, JsonOptions)
        ?? throw new InvalidOperationException($"Stored resource '{document.Kind}/{document.Name}' is invalid.");

    private static ResourceGovernance ReadGovernance(string payload) =>
        JsonSerializer.Deserialize<ResourceGovernanceEnvelope>(payload, JsonOptions)?.Governance ?? ResourceGovernance.User;

    public Task<IReadOnlyList<StoredResource<T>>> ListAllAsync<T>(string kind, CancellationToken cancellationToken) where T : Resource =>
        LoadKindAsync<T>(kind, cancellationToken);

    public async Task<StoredResource<AgentRevision>?> FindRevisionAsync(Guid agentUid, long generation, CancellationToken cancellationToken) =>
        (await LoadFilteredAsync<AgentRevision>($"""
            SELECT * FROM ControlPlaneResources
            WHERE Kind = {AgentResourceKinds.AgentRevision}
              AND json_extract(Payload, '$.agentUid') = {agentUid.ToString()}
              AND json_extract(Payload, '$.agentVersion') = {generation}
            """, cancellationToken)).SingleOrDefault();

    public async Task<StoredResource<AgentRevision>?> FindLatestRevisionAsync(Guid agentUid, CancellationToken cancellationToken)
    {
        var results = requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.System => await LoadFilteredAsync<AgentRevision>($"""
                SELECT * FROM ControlPlaneResources
                WHERE Kind = {AgentResourceKinds.AgentRevision}
                  AND json_extract(Payload, '$.agentUid') = {agentUid.ToString()}
                ORDER BY json_extract(Payload, '$.agentVersion') DESC,
                         json_extract(Payload, '$.createdAt') DESC
                LIMIT 1
                """, cancellationToken),
            ControlPlaneAccessMode.Workspace => await LoadFilteredAsync<AgentRevision>($"""
                SELECT * FROM ControlPlaneResources
                WHERE ScopeId = (SELECT Id FROM ResourceScopes WHERE Ref = {ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId).Value})
                  AND Kind = {AgentResourceKinds.AgentRevision}
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
                WHERE Kind = {AgentResourceKinds.AgentDeployment}
                  AND Namespace = {namespaceValue}
                  AND json_extract(Payload, '$.revisionName') = {revisionName}
                ORDER BY json_extract(Payload, '$.updatedAt') DESC
                LIMIT 1
                """, cancellationToken),
            ControlPlaneAccessMode.Workspace => await LoadFilteredAsync<AgentDeployment>($"""
                SELECT * FROM ControlPlaneResources
                WHERE ScopeId = (SELECT Id FROM ResourceScopes WHERE Ref = {ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId).Value})
                  AND Kind = {AgentResourceKinds.AgentDeployment}
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
            WHERE Kind = {AgentResourceKinds.AgentDeployment}
              AND Namespace = {@namespace.Value}
              AND json_extract(Payload, '$.agentName') = {agentName}
            """, cancellationToken))
        .OrderByDescending(value => value.Value.UpdatedAt)
        .ToArray();

    public Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsAsync(CancellationToken cancellationToken) =>
        LoadKindAsync<AgentDeployment>(AgentResourceKinds.AgentDeployment, cancellationToken);

    private async Task<IReadOnlyList<StoredResource<T>>> LoadKindAsync<T>(string kind, CancellationToken cancellationToken) where T : Resource
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await Scoped(context.Documents.AsNoTracking()).Include(value => value.Scope)
            .Where(value => value.Kind == kind)
            .OrderBy(value => value.Namespace).ThenBy(value => value.Name)
            .ToArrayAsync(cancellationToken);
        return (await FilterDiscoverableAsync(context, documents, cancellationToken)).Select(Deserialize<T>).ToArray();
    }

    private async Task<IReadOnlyList<StoredResource<T>>> LoadFilteredAsync<T>(FormattableString sql, CancellationToken cancellationToken) where T : Resource
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await Scoped(context.Documents.FromSqlInterpolated(sql).AsNoTracking()).Include(value => value.Scope)
            .ToArrayAsync(cancellationToken);
        return (await FilterDiscoverableAsync(context, documents, cancellationToken)).Select(Deserialize<T>).ToArray();
    }

    private IQueryable<ControlPlaneDocument> Scoped(IQueryable<ControlPlaneDocument> query)
    {
        return requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.System => query,
            ControlPlaneAccessMode.Instance => query.Where(value =>
                value.Scope.Ref == ResourceScopeRef.Instance.Value),
            ControlPlaneAccessMode.TenantAdministration => query.Where(value =>
                value.Scope.Ref == ResourceScopeRef.Tenant(requestContext.Current.TenantId).Value),
            ControlPlaneAccessMode.Tenant => query.Where(value =>
                value.Scope.Ref == ResourceScopeRef.Tenant(requestContext.Current.TenantId).Value
                || value.Scope.Ref == ResourceScopeRef.Instance.Value),
            ControlPlaneAccessMode.Workspace => query.Where(value =>
                value.Scope.Ref == ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId).Value
                || value.Scope.Ref == ResourceScopeRef.Tenant(requestContext.Current.TenantId).Value
                || value.Scope.Ref == ResourceScopeRef.Instance.Value),
            _ => throw new InvalidOperationException("Control Plane access requires an explicit workspace or system context.")
        };
    }

    public async Task<ResolvedResourceScope?> ResolveAsync(ResourceScopeRef scopeRef, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.ResourceScopes.AsNoTracking().SingleOrDefaultAsync(value => value.Ref == scopeRef.Value, cancellationToken);
        if (row is null) return null;
        var ancestors = new List<ResourceScope>();
        var parentId = row.ParentScopeId;
        while (parentId is not null)
        {
            var parent = await context.ResourceScopes.AsNoTracking().SingleOrDefaultAsync(value => value.Id == parentId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Resource scope '{scopeRef}' has a missing parent scope.");
            ancestors.Add(Map(parent));
            parentId = parent.ParentScopeId;
        }
        return new(Map(row), ancestors);
    }

    private ResourceScopeRef ResolveWriteScopeRef(Resource resource)
    {
        if (requestContext.AccessMode == ControlPlaneAccessMode.Workspace)
            return ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId);
        if (requestContext.AccessMode == ControlPlaneAccessMode.Tenant)
            return ResourceScopeRef.Tenant(requestContext.Current.TenantId);
        if (requestContext.AccessMode == ControlPlaneAccessMode.TenantAdministration)
            return ResourceScopeRef.Tenant(requestContext.Current.TenantId);
        if (requestContext.AccessMode == ControlPlaneAccessMode.Instance)
            return ResourceScopeRef.Instance;
        if (requestContext.AccessMode == ControlPlaneAccessMode.System)
            return resource.ScopeRef ?? ResourceScopeRef.Instance;
        throw new InvalidOperationException("Control Plane access requires an explicit workspace or system context.");
    }

    private async Task<IReadOnlyList<StoredResource<T>>> ListScopedAsync<T>(ResourceScopeRef targetScopeRef, string kind, int skip, int take, bool visible, CancellationToken cancellationToken) where T : Resource
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 1000);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var target = await RequireScopeAsync(context, targetScopeRef, cancellationToken);
        await EnsureCanReadAsync(context, target, cancellationToken);
        var scopeIds = visible ? await EffectiveVisibleScopeIdsAsync(context, target, cancellationToken) : [target.Id];
        var query = context.Documents.AsNoTracking().Where(value => value.Kind == kind);
        query = query.Where(value => scopeIds.Contains(value.ScopeId));
        var candidates = await query.Include(value => value.Scope).OrderBy(value => value.ScopeId).ThenBy(value => value.Namespace).ThenBy(value => value.Name).ToArrayAsync(cancellationToken);
        var documents = visible
            ? (await FilterDiscoverableAsync(context, candidates, cancellationToken)).Skip(skip).Take(take)
            : candidates.Skip(skip).Take(take);
        return documents.Select(Deserialize<T>).ToArray();
    }

    private ResourceScopeRef CurrentScopeRef() => requestContext.AccessMode switch
    {
        ControlPlaneAccessMode.System => ResourceScopeRef.Instance,
        ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
        ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(requestContext.Current.TenantId),
        ControlPlaneAccessMode.Tenant => ResourceScopeRef.Tenant(requestContext.Current.TenantId),
        ControlPlaneAccessMode.Workspace => ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId),
        _ => throw new InvalidOperationException("Control Plane access requires an explicit tenant, workspace, or system context.")
    };

    private async Task<bool> CanReadAsync(ResourceManagementDbContext context, ResourceScopeRow scope, CancellationToken cancellationToken)
    {
        if (requestContext.AccessMode == ControlPlaneAccessMode.System) return true;
        if (requestContext.AccessMode is not (ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration or ControlPlaneAccessMode.Tenant or ControlPlaneAccessMode.Workspace)) return false;
        var current = await RequireScopeAsync(context, CurrentScopeRef(), cancellationToken);
        return (await EffectiveVisibleScopeIdsAsync(context, current, cancellationToken)).Contains(scope.Id);
    }

    private async Task<bool> CanDiscoverAsync(ResourceManagementDbContext context, ControlPlaneDocument document,
        CancellationToken cancellationToken)
    {
        if (requestContext.AccessMode != ControlPlaneAccessMode.Workspace
            || document.Scope.Ref == ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId).Value)
            return true;
        return (await FilterDiscoverableAsync(context, [document], cancellationToken)).Length == 1;
    }

    private async Task<ControlPlaneDocument[]> FilterDiscoverableAsync(ResourceManagementDbContext context,
        IEnumerable<ControlPlaneDocument> documents, CancellationToken cancellationToken)
    {
        var candidates = documents.ToArray();
        if (requestContext.AccessMode != ControlPlaneAccessMode.Workspace || candidates.Length == 0)
            return candidates;
        var workspaceRef = ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId);
        var tenantRef = ResourceScopeRef.Tenant(requestContext.Current.TenantId);
        var ancestorUids = candidates.Where(value => value.Scope.Ref != workspaceRef.Value)
            .Select(value => value.Uid).Distinct().ToArray();
        var policies = await context.ResourceAccessPolicies.AsNoTracking()
            .Where(value => ancestorUids.Contains(value.ResourceUid))
            .ToDictionaryAsync(value => value.ResourceUid, cancellationToken);
        return candidates.Where(value => value.Scope.Ref == workspaceRef.Value
            || policies.TryGetValue(value.Uid, out var policy)
            && GrantsDiscover(policy.GrantsJson, workspaceRef, tenantRef)).ToArray();
    }

    private static bool GrantsDiscover(string grantsJson, ResourceScopeRef workspaceRef, ResourceScopeRef tenantRef) =>
        (JsonSerializer.Deserialize<ResourceAccessGrant[]>(grantsJson, JsonOptions) ?? []).Any(grant =>
            grant.Actions.Contains(ResourceAccessAction.Discover)
            && (grant.TargetScopeRef == workspaceRef
                || grant.TargetScopeRef == tenantRef && grant.IncludeDescendants));

    private async Task EnsureCanReadAsync(ResourceManagementDbContext context, ResourceScopeRow scope, CancellationToken cancellationToken)
    {
        if (!await CanReadAsync(context, scope, cancellationToken))
            throw new InvalidOperationException($"The current Control Plane context cannot read scope '{scope.Ref}'.");
    }

    private void EnsureCanWrite(ResourceScopeRef scopeRef)
    {
        var allowed = requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.System => true,
            ControlPlaneAccessMode.Instance => scopeRef == ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => scopeRef == ResourceScopeRef.Tenant(requestContext.Current.TenantId),
            ControlPlaneAccessMode.Tenant => scopeRef == ResourceScopeRef.Tenant(requestContext.Current.TenantId),
            ControlPlaneAccessMode.Workspace => scopeRef == ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId),
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"The current Control Plane context cannot write scope '{scopeRef}'.");
    }

    private static async Task<ResourceScopeRow> RequireScopeAsync(ResourceManagementDbContext context, ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
        await context.ResourceScopes.SingleOrDefaultAsync(value => value.Ref == scopeRef.Value, cancellationToken)
        ?? throw new InvalidOperationException($"Resource scope '{scopeRef}' does not exist.");

    private static async Task<long[]> VisibleScopeIdsAsync(ResourceManagementDbContext context, ResourceScopeRow target, CancellationToken cancellationToken)
    {
        var result = new List<long> { target.Id };
        var parentId = target.ParentScopeId;
        while (parentId is not null)
        {
            var parent = await context.ResourceScopes.AsNoTracking().SingleOrDefaultAsync(value => value.Id == parentId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Resource scope '{target.Ref}' has a missing parent scope.");
            result.Add(parent.Id);
            parentId = parent.ParentScopeId;
        }
        return [.. result];
    }

    private Task<long[]> EffectiveVisibleScopeIdsAsync(
        ResourceManagementDbContext context,
        ResourceScopeRow target,
        CancellationToken cancellationToken) =>
        requestContext.AccessMode is ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration
            ? Task.FromResult(new[] { target.Id })
            : VisibleScopeIdsAsync(context, target, cancellationToken);

    private static ResourceScope Map(ResourceScopeRow row) => new(
        row.Id,
        ResourceScopeRef.Parse(row.Ref),
        Enum.Parse<ResourceScopeKind>(row.Kind, true),
        row.TargetKey,
        row.ParentScopeId);

    private static async Task SaveAsync(ResourceManagementDbContext context, CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new ResourceConcurrencyException(exception.InnerException?.Message ?? exception.Message); }
    }

    private static T ApplySystemState<T>(T resource, Guid uid, ResourceScopeRef scopeRef, string etag) where T : Resource =>
        (T)resource.WithSystemState(uid, scopeRef, etag);

    private static string NewETag() => $"\"{Guid.NewGuid():N}\"";

    private sealed record ResourceGovernanceEnvelope
    {
        public ResourceGovernance Governance { get; init; } = ResourceGovernance.User;
    }

}

public static class SqliteResourceManagementServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteResourceManagement(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.TryAddSingleton<ICurrentRequestContext, UnavailableRequestContext>();
        services.TryAddSingleton<IResourceGovernanceContext, ResourceGovernanceContext>();
        services.TryAddSingleton<ITriggerExecutionContext, TriggerExecutionContextAdapter>();
        services.AddDbContextFactory<ResourceManagementDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IResourceStore, SqliteResourceStore>();
        services.AddSingleton<IAgentResourceQueries>(provider => (SqliteResourceStore)provider.GetRequiredService<IResourceStore>());
        services.AddSingleton<IResourceScopeResolver>(provider => (SqliteResourceStore)provider.GetRequiredService<IResourceStore>());
        services.AddSingleton<IResourceAccessPolicyStore, SqliteResourceAccessPolicyStore>();
        services.AddSingleton<ITriggerOccurrenceStore, SqliteTriggerOccurrenceStore>();
        services.AddSingleton<SqliteIdentityStore>();
        services.AddSingleton<IIdentityStore>(provider => provider.GetRequiredService<SqliteIdentityStore>());
        services.AddSingleton<ISecurityAuditStore>(provider => provider.GetRequiredService<SqliteIdentityStore>());
        services.AddSingleton<IPersonalAccessTokenStore>(provider => provider.GetRequiredService<SqliteIdentityStore>());
        return services;
    }
}

internal sealed class TriggerExecutionContextAdapter(ICurrentRequestContext context) : ITriggerExecutionContext
{
    public bool IsInitialized => context.IsInitialized;
    public TriggerExecutionIdentity Current => new(context.Current.TenantId, context.Current.WorkspaceId, context.Current.PrincipalId);
}

public static class SqliteControlPlaneServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteControlPlane(this IServiceCollection services, string connectionString) =>
        services.AddSqliteResourceManagement(connectionString);
}
