using System.Text.Json;
using Agentstration.Management.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Storage.Sqlite;

public sealed class ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options) : DbContext(options)
{
    internal DbSet<ControlPlaneDocument> Documents => Set<ControlPlaneDocument>();
    internal DbSet<TenantRow> Tenants => Set<TenantRow>();
    internal DbSet<WorkspaceRow> Workspaces => Set<WorkspaceRow>();
    internal DbSet<UserRow> Users => Set<UserRow>();
    internal DbSet<TenantMembershipRow> TenantMemberships => Set<TenantMembershipRow>();
    internal DbSet<RoleDefinitionRow> RoleDefinitions => Set<RoleDefinitionRow>();
    internal DbSet<RoleAssignmentRow> RoleAssignments => Set<RoleAssignmentRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var document = modelBuilder.Entity<ControlPlaneDocument>();
        document.ToTable("ControlPlaneResources");
        document.HasKey(value => value.StorageKey);
        document.Property(value => value.StorageKey).HasColumnName("ResourceId").HasMaxLength(1024);
        document.Property(value => value.LegacyResourceType).HasColumnName("ResourceType").HasMaxLength(256);
        document.Property(value => value.Kind).HasMaxLength(256);
        document.Property(value => value.Name).HasMaxLength(256);
        document.Property(value => value.TenantId);
        document.Property(value => value.WorkspaceId);
        document.Property(value => value.ETag).HasMaxLength(64).IsConcurrencyToken();
        document.HasIndex(value => new { value.WorkspaceId, value.Kind, value.Name }).IsUnique();
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
    public Guid? TenantId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public required string Payload { get; set; }
    public required string ETag { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SqliteControlPlaneStore(
    IDbContextFactory<ControlPlaneDbContext> contextFactory,
    TimeProvider timeProvider,
    ICurrentRequestContext requestContext) : IControlPlaneStore, IAgentResourceQueries, IResourceScopeMigrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await SqliteIdentitySchema.EnsureAsync(context, cancellationToken);
    }

    public async Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = Scoped(context.Documents.AsNoTracking());
        var document = await query.SingleOrDefaultAsync(value => value.Kind == key.Kind && value.Name == key.Name, cancellationToken);
        return document is null ? null : Deserialize<T>(document);
    }

    public async Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 1000);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = Scoped(context.Documents.AsNoTracking()).Where(value => value.Kind == kind);
        var documents = await query.OrderBy(value => value.Name).Skip(skip).Take(take).ToArrayAsync(cancellationToken);
        return documents.Select(Deserialize<T>).ToArray();
    }

    public async Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
    {
        if (resource is AgentRevision) throw new InvalidOperationException("Published agent revisions are immutable and must be created through CreateImmutableAsync.");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await Scoped(context.Documents).SingleOrDefaultAsync(
            value => value.Kind == resource.Kind && value.Name == resource.Metadata.Name, cancellationToken);
        if (existing is null && ifMatch is not null) throw new ControlPlaneConcurrencyException("If-Match cannot update a resource that does not exist.");
        if (existing is not null && ifNoneMatch) throw new ControlPlaneConcurrencyException("If-None-Match prevented replacement of an existing resource.");
        if (existing is not null && ifMatch is not null && !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
            throw new ControlPlaneConcurrencyException("The supplied ETag does not match the current resource version.");

        var etag = NewETag();
        var now = timeProvider.GetUtcNow();
        var uid = existing?.Uid ?? Guid.NewGuid();
        if (existing is not null && resource.Uid != Guid.Empty && resource.Uid != uid)
            throw new ControlPlaneConcurrencyException("The UID of an existing resource is immutable.");
        var scope = ResolveScope(resource);
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
                TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId,
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
            existing.TenantId = scope.TenantId;
            existing.WorkspaceId = scope.WorkspaceId;
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
        var versioned = ApplySystemState(resource, uid, scope.TenantId, scope.WorkspaceId, etag);
        context.Documents.Add(new ControlPlaneDocument
        {
            StorageKey = uid.ToString("N"),
            LegacyResourceType = resource.Kind,
            Uid = uid,
            Kind = resource.Kind,
            Name = resource.Metadata.Name,
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
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
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await Scoped(context.Documents).SingleOrDefaultAsync(value => value.Kind == key.Kind && value.Name == key.Name, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(key);
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

    public async Task BackfillUnscopedResourcesAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Documents
            .Where(value => value.TenantId == null || value.WorkspaceId == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.TenantId, tenantId)
                .SetProperty(value => value.WorkspaceId, workspaceId), cancellationToken);
    }

    public Task<IReadOnlyList<StoredResource<T>>> ListAllAsync<T>(string kind, CancellationToken cancellationToken) where T : Resource =>
        LoadKindAsync<T>(kind, cancellationToken);

    public async Task<StoredResource<AgentRevision>?> FindRevisionAsync(Guid agentUid, long generation, CancellationToken cancellationToken) =>
        (await LoadKindAsync<AgentRevision>(ResourceKinds.AgentRevision, cancellationToken))
        .SingleOrDefault(value => value.Value.AgentUid == agentUid && value.Value.AgentVersion == generation);

    public async Task<StoredResource<AgentRevision>?> FindLatestRevisionAsync(Guid agentUid, CancellationToken cancellationToken) =>
        (await LoadKindAsync<AgentRevision>(ResourceKinds.AgentRevision, cancellationToken))
        .Where(value => value.Value.AgentUid == agentUid)
        .OrderByDescending(value => value.Value.AgentVersion)
        .ThenByDescending(value => value.Value.CreatedAt)
        .FirstOrDefault();

    public async Task<StoredResource<AgentDeployment>?> FindDeploymentByRevisionAsync(string revisionName, CancellationToken cancellationToken) =>
        (await LoadKindAsync<AgentDeployment>(ResourceKinds.AgentDeployment, cancellationToken))
        .Where(value => string.Equals(value.Value.RevisionName, revisionName, StringComparison.Ordinal))
        .OrderByDescending(value => value.Value.UpdatedAt)
        .FirstOrDefault();

    public async Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsForAgentAsync(string agentName, CancellationToken cancellationToken) =>
        (await LoadKindAsync<AgentDeployment>(ResourceKinds.AgentDeployment, cancellationToken))
        .Where(value => string.Equals(value.Value.AgentName, agentName, StringComparison.Ordinal))
        .OrderByDescending(value => value.Value.UpdatedAt)
        .ToArray();

    public Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsAsync(CancellationToken cancellationToken) =>
        LoadKindAsync<AgentDeployment>(ResourceKinds.AgentDeployment, cancellationToken);

    private async Task<IReadOnlyList<StoredResource<T>>> LoadKindAsync<T>(string kind, CancellationToken cancellationToken) where T : Resource
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = await Scoped(context.Documents.AsNoTracking())
            .Where(value => value.Kind == kind)
            .OrderBy(value => value.Name)
            .ToArrayAsync(cancellationToken);
        return documents.Select(Deserialize<T>).ToArray();
    }

    private IQueryable<ControlPlaneDocument> Scoped(IQueryable<ControlPlaneDocument> query)
    {
        return requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.System => query,
            ControlPlaneAccessMode.Workspace => query.Where(value => value.TenantId == requestContext.Current.TenantId
                && value.WorkspaceId == requestContext.Current.WorkspaceId),
            _ => throw new InvalidOperationException("Control Plane access requires an explicit workspace or system context.")
        };
    }

    private (Guid TenantId, Guid WorkspaceId) ResolveScope(Resource resource)
    {
        if (requestContext.AccessMode == ControlPlaneAccessMode.Workspace)
        {
            var current = requestContext.Current;
            if (resource.TenantId != Guid.Empty && resource.TenantId != current.TenantId
                || resource.WorkspaceId != Guid.Empty && resource.WorkspaceId != current.WorkspaceId)
                throw new InvalidOperationException("A workspace-scoped operation cannot write a resource into another scope.");
            return (current.TenantId, current.WorkspaceId);
        }
        if (requestContext.AccessMode == ControlPlaneAccessMode.System)
        {
            if (resource.TenantId == Guid.Empty || resource.WorkspaceId == Guid.Empty)
                return (resource.TenantId, resource.WorkspaceId);
            return (resource.TenantId, resource.WorkspaceId);
        }
        throw new InvalidOperationException("Control Plane access requires an explicit workspace or system context.");
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
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ICurrentRequestContext)))
            services.AddSingleton<ICurrentRequestContext, SystemOperationRequestContext>();
        services.AddDbContextFactory<ControlPlaneDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IControlPlaneStore, SqliteControlPlaneStore>();
        services.AddSingleton<IAgentResourceQueries>(provider => (SqliteControlPlaneStore)provider.GetRequiredService<IControlPlaneStore>());
        services.AddSingleton<IResourceScopeMigrator>(provider => (SqliteControlPlaneStore)provider.GetRequiredService<IControlPlaneStore>());
        services.AddSingleton<IIdentityStore, SqliteIdentityStore>();
        return services;
    }

    private sealed class SystemOperationRequestContext : ICurrentRequestContext
    {
        public bool IsInitialized => false;
        public ControlPlaneAccessMode AccessMode => ControlPlaneAccessMode.System;
        public RequestContext Current => throw new InvalidOperationException("No request context is configured for this control-plane store.");
    }
}
