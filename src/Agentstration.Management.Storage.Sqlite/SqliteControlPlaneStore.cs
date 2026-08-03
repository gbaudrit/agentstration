using System.Text.Json;
using Agentstration.Management.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Storage.Sqlite;

public sealed class ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options) : DbContext(options)
{
    internal DbSet<ControlPlaneDocument> Documents => Set<ControlPlaneDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var document = modelBuilder.Entity<ControlPlaneDocument>();
        document.ToTable("ControlPlaneResources");
        document.HasKey(value => value.ResourceId);
        document.Property(value => value.ResourceId).HasMaxLength(1024);
        document.Property(value => value.ResourceType).HasMaxLength(256);
        document.Property(value => value.ResourceGroup).HasMaxLength(256);
        document.Property(value => value.ETag).HasMaxLength(64).IsConcurrencyToken();
        document.HasIndex(value => new { value.ResourceType, value.ResourceGroup, value.ResourceId });
    }
}

internal sealed class ControlPlaneDocument
{
    public required string ResourceId { get; set; }
    public required string ResourceType { get; set; }
    public string? ResourceGroup { get; set; }
    public required string Payload { get; set; }
    public required string ETag { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SqliteControlPlaneStore(IDbContextFactory<ControlPlaneDbContext> contextFactory, TimeProvider timeProvider) : IControlPlaneStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task<StoredResource<T>?> GetAsync<T>(string resourceId, CancellationToken cancellationToken) where T : Resource
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(value => value.ResourceId == resourceId, cancellationToken);
        return document is null ? null : Deserialize<T>(document);
    }

    public async Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string resourceType, string? resourceGroup, int skip, int take, CancellationToken cancellationToken) where T : Resource
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 1000);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Documents.AsNoTracking().Where(value => value.ResourceType == resourceType);
        if (!string.IsNullOrWhiteSpace(resourceGroup)) query = query.Where(value => value.ResourceGroup == resourceGroup);
        var documents = await query.OrderBy(value => value.ResourceId).Skip(skip).Take(take).ToArrayAsync(cancellationToken);
        return documents.Select(Deserialize<T>).ToArray();
    }

    public async Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
    {
        if (resource is AgentRevision) throw new InvalidOperationException("Published agent revisions are immutable and must be created through CreateImmutableAsync.");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.Documents.SingleOrDefaultAsync(value => value.ResourceId == resource.Id, cancellationToken);
        if (existing is null && ifMatch is not null) throw new ControlPlaneConcurrencyException("If-Match cannot update a resource that does not exist.");
        if (existing is not null && ifNoneMatch) throw new ControlPlaneConcurrencyException("If-None-Match prevented replacement of an existing resource.");
        if (existing is not null && ifMatch is not null && !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
            throw new ControlPlaneConcurrencyException("The supplied ETag does not match the current resource version.");

        var etag = NewETag();
        var now = timeProvider.GetUtcNow();
        var versioned = WithETag(resource, etag);
        if (existing is null)
        {
            context.Documents.Add(new ControlPlaneDocument
            {
                ResourceId = resource.Id,
                ResourceType = resource.Type,
                ResourceGroup = resource.ResourceGroup,
                Payload = JsonSerializer.Serialize(versioned, JsonOptions),
                ETag = etag,
                UpdatedAt = now
            });
        }
        else
        {
            existing.ResourceType = resource.Type;
            existing.ResourceGroup = resource.ResourceGroup;
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
        var versioned = WithETag(resource, etag);
        context.Documents.Add(new ControlPlaneDocument
        {
            ResourceId = resource.Id,
            ResourceType = resource.Type,
            ResourceGroup = resource.ResourceGroup,
            Payload = JsonSerializer.Serialize(versioned, JsonOptions),
            ETag = etag,
            UpdatedAt = now
        });
        try { await SaveAsync(context, cancellationToken); }
        catch (ControlPlaneConcurrencyException exception) { throw new ControlPlaneConcurrencyException($"Immutable resource '{resource.Id}' already exists: {exception.Message}"); }
        return new StoredResource<T>(versioned, etag, now);
    }

    public async Task DeleteAsync(string resourceId, string? ifMatch, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.Documents.SingleOrDefaultAsync(value => value.ResourceId == resourceId, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(resourceId);
        if (ifMatch is not null && !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
            throw new ControlPlaneConcurrencyException("The supplied ETag does not match the current resource version.");
        context.Documents.Remove(existing);
        await SaveAsync(context, cancellationToken);
    }

    private static StoredResource<T> Deserialize<T>(ControlPlaneDocument document) where T : Resource
    {
        var value = JsonSerializer.Deserialize<T>(document.Payload, JsonOptions)
            ?? throw new InvalidOperationException($"Stored resource '{document.ResourceId}' is invalid.");
        return new StoredResource<T>(WithETag(value, document.ETag), document.ETag, document.UpdatedAt);
    }

    private static async Task SaveAsync(ControlPlaneDbContext context, CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new ControlPlaneConcurrencyException(exception.InnerException?.Message ?? exception.Message); }
    }

    private static T WithETag<T>(T resource, string etag) where T : Resource
    {
        var status = resource.Status with { ResourceVersion = etag };
        return (T)(Resource)(resource switch
    {
        AgentTypeResource value => value with { ETag = etag, Status = status },
        AgentResource value => value with { ETag = etag, Status = status },
        AgentRevision value => value with { ETag = etag, Status = status },
        AgentDeployment value => value with { ETag = etag, Status = status },
        ManagementOperation value => value with { ETag = etag, Status = status },
        ModelProfileResource value => value with { ETag = etag, Status = status },
        RuntimeProfileResource value => value with { ETag = etag, Status = status },
        _ => throw new NotSupportedException($"Resource type '{resource.GetType().Name}' is not supported by the control plane store.")
    });
    }

    private static string NewETag() => $"\"{Guid.NewGuid():N}\"";
}

public static class SqliteControlPlaneServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteControlPlane(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContextFactory<ControlPlaneDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IControlPlaneStore, SqliteControlPlaneStore>();
        return services;
    }
}
