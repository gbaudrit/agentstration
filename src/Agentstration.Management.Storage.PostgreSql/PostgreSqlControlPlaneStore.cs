using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Storage.PostgreSql;

public sealed class ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options) : DbContext(options)
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("management");
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
public sealed class PostgreSqlControlPlaneStore(
    IDbContextFactory<ControlPlaneDbContext> contextFactory,
    TimeProvider timeProvider,
    ICurrentRequestContext requestContext) : IControlPlaneStore, IAgentResourceQueries, IResourceScopeResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.Database.CanConnectAsync(cancellationToken))
            throw new InvalidOperationException("The PostgreSQL Management store is not accessible.");
        try { await EnsureInstanceScopeAsync(context, cancellationToken); }
        catch (System.Data.Common.DbException exception)
        {
            throw new InvalidOperationException("The PostgreSQL Management schema is incompatible with explicit resource scopes. Recreate the pre-release database from the new initial migration.", exception);
        }
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

    public async Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Documents.AsNoTracking().Include(value => value.Scope)
            .Where(value => value.Namespace == key.Namespace.Value && value.Kind == key.Kind && value.Name == key.Name);
        if (requestContext.AccessMode != ControlPlaneAccessMode.System)
        {
            var target = await RequireScopeAsync(context, CurrentScopeRef(), cancellationToken);
            var visibleScopeIds = await VisibleScopeIdsAsync(context, target, cancellationToken);
            query = query.Where(value => visibleScopeIds.Contains(value.ScopeId));
        }
        var matches = await query.Take(2).ToArrayAsync(cancellationToken);
        return matches.Length switch
        {
            0 => null,
            1 => Deserialize<T>(matches[0]),
            _ => throw new ControlPlaneAmbiguousResourceException(key)
        };
    }

    public async Task<StoredResource<T>?> GetByUidAsync<T>(Guid uid, CancellationToken cancellationToken) where T : Resource
    {
        if (uid == Guid.Empty) throw new ArgumentException("A resource UID cannot be empty.", nameof(uid));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().Include(value => value.Scope).SingleOrDefaultAsync(value => value.Uid == uid, cancellationToken);
        if (document is null || !await CanReadAsync(context, document.Scope, cancellationToken)) return null;
        return Deserialize<T>(document);
    }

    public async Task<StoredResource<T>?> GetExactAsync<T>(ScopedResourceAddress address, CancellationToken cancellationToken) where T : Resource
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var scope = await RequireScopeAsync(context, address.ScopeRef, cancellationToken);
        await EnsureCanReadAsync(context, scope, cancellationToken);
        var document = await context.Documents.AsNoTracking().Include(value => value.Scope).SingleOrDefaultAsync(value => value.ScopeId == scope.Id
            && value.Namespace == address.Namespace.Value && value.Kind == address.Kind && value.Name == address.Name, cancellationToken);
        return document is null ? null : Deserialize<T>(document);
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
        var documents = await query.Include(value => value.Scope).OrderBy(value => value.Namespace).ThenBy(value => value.Name).Skip(skip).Take(take).ToArrayAsync(cancellationToken);
        return documents.Select(Deserialize<T>).ToArray();
    }

    public async Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        => await PutExactAsync(ResolveWriteScopeRef(resource), resource, ifMatch, ifNoneMatch, cancellationToken);

    public async Task<StoredResource<T>> PutExactAsync<T>(ResourceScopeRef scopeRef, T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
    {
        if (resource is AgentRevision or SourceResource or SourceVersionResource)
            throw new InvalidOperationException($"Resource kind '{resource.Kind}' is immutable and must be created through CreateImmutableAsync.");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        EnsureCanWrite(scopeRef);
        var scope = await RequireScopeAsync(context, scopeRef, cancellationToken);
        var namespaceValue = resource.Namespace.Value;
        var existing = await context.Documents.SingleOrDefaultAsync(
            value => value.ScopeId == scope.Id && value.Namespace == namespaceValue && value.Kind == resource.Kind && value.Name == resource.Metadata.Name, cancellationToken);
        if (existing is null && resource.Uid != Guid.Empty && !ifNoneMatch)
        {
            var byUid = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.Uid == resource.Uid, cancellationToken);
            if (byUid is not null && byUid.ScopeId != scope.Id)
                throw new ControlPlaneConcurrencyException("The ownership scope of an existing resource is immutable.");
        }
        if (existing is null && ifMatch is not null) throw new ControlPlaneConcurrencyException("If-Match cannot update a resource that does not exist.");
        if (existing is not null && ifNoneMatch) throw new ControlPlaneConcurrencyException("If-None-Match prevented replacement of an existing resource.");
        if (existing is not null && ifMatch is not null && !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
            throw new ControlPlaneConcurrencyException("The supplied ETag does not match the current resource version.");

        var etag = NewETag();
        var now = timeProvider.GetUtcNow();
        var uid = existing?.Uid ?? Guid.NewGuid();
        if (existing is not null && resource.Uid != Guid.Empty && resource.Uid != uid)
            throw new ControlPlaneConcurrencyException("The UID of an existing resource is immutable.");
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
        return new StoredResource<T>(versioned, etag, now);
    }

    public async Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource
    {
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
        catch (ControlPlaneConcurrencyException exception) { throw new ControlPlaneConcurrencyException($"Immutable resource '{resource.Kind}/{resource.Metadata.Name}' already exists: {exception.Message}"); }
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
            0 => throw new ControlPlaneResourceNotFoundException(key),
            1 => matches[0],
            _ => throw new ControlPlaneAmbiguousResourceException(key)
        };
        if (ifMatch is not null && !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
            throw new ControlPlaneConcurrencyException("The supplied ETag does not match the current resource version.");
        context.Documents.Remove(existing);
        await SaveAsync(context, cancellationToken);
    }

    public async Task DeleteExactAsync(ScopedResourceAddress address, string? ifMatch, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        EnsureCanWrite(address.ScopeRef);
        var scope = await RequireScopeAsync(context, address.ScopeRef, cancellationToken);
        var existing = await context.Documents.SingleOrDefaultAsync(value => value.ScopeId == scope.Id && value.Namespace == address.Namespace.Value && value.Kind == address.Kind && value.Name == address.Name, cancellationToken)
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
        value = ApplySystemState(value, document.Uid, ResourceScopeRef.Parse(document.Scope.Ref), document.ETag);
        return new StoredResource<T>(value, document.ETag, document.UpdatedAt);
    }

    public Task<IReadOnlyList<StoredResource<T>>> ListAllAsync<T>(string kind, CancellationToken cancellationToken) where T : Resource =>
        LoadKindAsync<T>(kind, cancellationToken);

    public async Task<StoredResource<AgentRevision>?> FindRevisionAsync(Guid agentUid, long generation, CancellationToken cancellationToken) =>
        (await LoadKindAsync<AgentRevision>(ResourceKinds.AgentRevision, cancellationToken)).SingleOrDefault(value => value.Value.AgentUid == agentUid && value.Value.AgentVersion == generation);

    public async Task<StoredResource<AgentRevision>?> FindLatestRevisionAsync(Guid agentUid, CancellationToken cancellationToken) =>
        (await LoadKindAsync<AgentRevision>(ResourceKinds.AgentRevision, cancellationToken)).Where(value => value.Value.AgentUid == agentUid).OrderByDescending(value => value.Value.AgentVersion).ThenByDescending(value => value.Value.CreatedAt).FirstOrDefault();

    public Task<StoredResource<AgentDeployment>?> FindDeploymentByRevisionAsync(string revisionName, CancellationToken cancellationToken) =>
        FindDeploymentByRevisionAsync(ResourceNamespace.Default, revisionName, cancellationToken);

    public async Task<StoredResource<AgentDeployment>?> FindDeploymentByRevisionAsync(ResourceNamespace @namespace, string revisionName, CancellationToken cancellationToken) =>
        (await LoadKindAsync<AgentDeployment>(ResourceKinds.AgentDeployment, cancellationToken)).Where(value => value.Value.Namespace == @namespace && value.Value.RevisionName == revisionName).OrderByDescending(value => value.Value.UpdatedAt).FirstOrDefault();

    public Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsForAgentAsync(string agentName, CancellationToken cancellationToken) => ListDeploymentsForAgentAsync(ResourceNamespace.Default, agentName, cancellationToken);

    public async Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsForAgentAsync(ResourceNamespace @namespace, string agentName, CancellationToken cancellationToken) =>
        (await LoadKindAsync<AgentDeployment>(ResourceKinds.AgentDeployment, cancellationToken)).Where(value => value.Value.Namespace == @namespace && value.Value.AgentName == agentName).OrderByDescending(value => value.Value.UpdatedAt).ToArray();

    public Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsAsync(CancellationToken cancellationToken) =>
        LoadKindAsync<AgentDeployment>(ResourceKinds.AgentDeployment, cancellationToken);

    private async Task<IReadOnlyList<StoredResource<T>>> LoadKindAsync<T>(string kind, CancellationToken cancellationToken) where T : Resource
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await Scoped(context.Documents.AsNoTracking()).Include(value => value.Scope)
            .Where(value => value.Kind == kind)
            .OrderBy(value => value.Namespace).ThenBy(value => value.Name)
            .ToArrayAsync(cancellationToken);
        return documents.Select(Deserialize<T>).ToArray();
    }

    private IQueryable<ControlPlaneDocument> Scoped(IQueryable<ControlPlaneDocument> query)
    {
        return requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.System => query,
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

    private ResourceScopeRef ResolveWriteScopeRef(Resource resource)
    {
        if (requestContext.AccessMode == ControlPlaneAccessMode.Workspace)
            return ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId);
        if (requestContext.AccessMode == ControlPlaneAccessMode.Tenant)
            return ResourceScopeRef.Tenant(requestContext.Current.TenantId);
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
        var scopeIds = visible ? await VisibleScopeIdsAsync(context, target, cancellationToken) : [target.Id];
        var query = context.Documents.AsNoTracking().Where(value => value.Kind == kind);
        query = query.Where(value => scopeIds.Contains(value.ScopeId));
        var documents = await query.Include(value => value.Scope).OrderBy(value => value.ScopeId).ThenBy(value => value.Namespace).ThenBy(value => value.Name).Skip(skip).Take(take).ToArrayAsync(cancellationToken);
        return documents.Select(Deserialize<T>).ToArray();
    }

    private ResourceScopeRef CurrentScopeRef() => requestContext.AccessMode switch
    {
        ControlPlaneAccessMode.System => ResourceScopeRef.Instance,
        ControlPlaneAccessMode.Tenant => ResourceScopeRef.Tenant(requestContext.Current.TenantId),
        ControlPlaneAccessMode.Workspace => ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId),
        _ => throw new InvalidOperationException("Control Plane access requires an explicit tenant, workspace, or system context.")
    };

    private async Task<bool> CanReadAsync(ControlPlaneDbContext context, ResourceScopeRow scope, CancellationToken cancellationToken)
    {
        if (requestContext.AccessMode == ControlPlaneAccessMode.System) return true;
        if (requestContext.AccessMode is not (ControlPlaneAccessMode.Tenant or ControlPlaneAccessMode.Workspace)) return false;
        var current = await RequireScopeAsync(context, CurrentScopeRef(), cancellationToken);
        return (await VisibleScopeIdsAsync(context, current, cancellationToken)).Contains(scope.Id);
    }

    private async Task EnsureCanReadAsync(ControlPlaneDbContext context, ResourceScopeRow scope, CancellationToken cancellationToken)
    {
        if (!await CanReadAsync(context, scope, cancellationToken))
            throw new InvalidOperationException($"The current Control Plane context cannot read scope '{scope.Ref}'.");
    }

    private void EnsureCanWrite(ResourceScopeRef scopeRef)
    {
        var allowed = requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.System => true,
            ControlPlaneAccessMode.Tenant => scopeRef == ResourceScopeRef.Tenant(requestContext.Current.TenantId),
            ControlPlaneAccessMode.Workspace => scopeRef == ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId),
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"The current Control Plane context cannot write scope '{scopeRef}'.");
    }

    private static async Task<ResourceScopeRow> RequireScopeAsync(ControlPlaneDbContext context, ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
        await context.ResourceScopes.SingleOrDefaultAsync(value => value.Ref == scopeRef.Value, cancellationToken)
        ?? throw new InvalidOperationException($"Resource scope '{scopeRef}' does not exist.");

    private static async Task<long[]> VisibleScopeIdsAsync(ControlPlaneDbContext context, ResourceScopeRow target, CancellationToken cancellationToken)
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

    private static async Task EnsureInstanceScopeAsync(ControlPlaneDbContext context, CancellationToken cancellationToken)
    {
        if (await context.ResourceScopes.AnyAsync(value => value.Ref == ResourceScopeRef.Instance.Value, cancellationToken)) return;
        context.ResourceScopes.Add(new ResourceScopeRow { Ref = ResourceScopeRef.Instance.Value, Kind = "instance", TargetKey = ResourceScopeRef.Instance.TargetKey });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static ResourceScope Map(ResourceScopeRow row) => new(
        row.Id,
        ResourceScopeRef.Parse(row.Ref),
        Enum.Parse<ResourceScopeKind>(row.Kind, true),
        row.TargetKey,
        row.ParentScopeId);

    private static async Task SaveAsync(ControlPlaneDbContext context, CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new ControlPlaneConcurrencyException(exception.InnerException?.Message ?? exception.Message); }
    }

    private static T ApplySystemState<T>(T resource, Guid uid, ResourceScopeRef scopeRef, string etag) where T : Resource =>
        (T)resource.WithSystemState(uid, scopeRef, etag);

    private static string NewETag() => $"\"{Guid.NewGuid():N}\"";

}

public static class PostgreSqlControlPlaneServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlControlPlane(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.TryAddSingleton<ICurrentRequestContext, UnavailableRequestContext>();
        services.AddDbContextFactory<ControlPlaneDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "management").EnableRetryOnFailure()).AddInterceptors(new UtcDateTimeOffsetInterceptor()));
        services.AddSingleton<IControlPlaneStore, PostgreSqlControlPlaneStore>();
        services.AddSingleton<IAgentResourceQueries>(provider => (PostgreSqlControlPlaneStore)provider.GetRequiredService<IControlPlaneStore>());
        services.AddSingleton<IResourceScopeResolver>(provider => (PostgreSqlControlPlaneStore)provider.GetRequiredService<IControlPlaneStore>());
        services.AddSingleton<ITriggerOccurrenceStore, PostgreSqlTriggerOccurrenceStore>();
        services.AddSingleton<PostgreSqlIdentityStore>();
        services.AddSingleton<IIdentityStore>(provider => provider.GetRequiredService<PostgreSqlIdentityStore>());
        services.AddSingleton<ISecurityAuditStore>(provider => provider.GetRequiredService<PostgreSqlIdentityStore>());
        services.AddSingleton<IPersonalAccessTokenStore>(provider => provider.GetRequiredService<PostgreSqlIdentityStore>());
        return services;
    }
}
