using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.ResourceManagement.Storage.PostgreSql;

internal sealed class ResourceAccessPolicyRow
{
    public Guid ResourceUid { get; set; }
    public required string OwnerScopeRef { get; set; }
    public required string Namespace { get; set; }
    public required string Kind { get; set; }
    public long? Generation { get; set; }
    public string? Version { get; set; }
    public required string GrantsJson { get; set; }
    public required string ETag { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PostgreSqlResourceAccessPolicyStore(
    IDbContextFactory<ResourceManagementDbContext> contextFactory,
    ICurrentRequestContext requestContext,
    IResourceScopeResolver scopes,
    TimeProvider timeProvider) : IResourceAccessPolicyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<StoredResourceAccessPolicy>> ListAsync(ResourceScopeRef ownerScopeRef,
        CancellationToken cancellationToken)
    {
        await EnsureCanReadAsync(ownerScopeRef, cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return (await context.ResourceAccessPolicies.AsNoTracking()
            .Where(value => value.OwnerScopeRef == ownerScopeRef.Value)
            .OrderBy(value => value.Kind).ThenBy(value => value.Namespace).ThenBy(value => value.ResourceUid)
            .ToArrayAsync(cancellationToken)).Select(Map).ToArray();
    }

    public async Task<StoredResourceAccessPolicy?> GetAsync(Guid resourceUid, ResourceScopeRef ownerScopeRef,
        CancellationToken cancellationToken)
    {
        await EnsureCanReadAsync(ownerScopeRef, cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.ResourceAccessPolicies.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ResourceUid == resourceUid
                && value.OwnerScopeRef == ownerScopeRef.Value, cancellationToken);
        return row is null ? null : Map(row);
    }

    public async Task<StoredResourceAccessPolicy> PutAsync(ResourceAccessPolicy policy, string? ifMatch,
        CancellationToken cancellationToken)
    {
        EnsureCanWrite(policy.Resource.OwnerScopeRef);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.ResourceAccessPolicies
            .SingleOrDefaultAsync(value => value.ResourceUid == policy.Resource.Uid, cancellationToken);
        if (row is null && ifMatch is not null)
            throw new ResourceConcurrencyException("If-Match cannot update an access policy that does not exist.");
        if (row is not null && row.OwnerScopeRef != policy.Resource.OwnerScopeRef.Value)
            throw new ResourceConcurrencyException("The owner scope of an access policy is immutable.");
        if (row is not null && ifMatch is not null && !string.Equals(row.ETag, ifMatch, StringComparison.Ordinal))
            throw new ResourceConcurrencyException("The supplied ETag does not match the current access policy version.");
        var etag = NewETag();
        var now = timeProvider.GetUtcNow();
        if (row is null)
        {
            row = new()
            {
                ResourceUid = policy.Resource.Uid,
                OwnerScopeRef = policy.Resource.OwnerScopeRef.Value,
                Namespace = policy.Resource.Namespace.Value,
                Kind = policy.Resource.Kind,
                Generation = policy.Resource.Generation,
                Version = policy.Resource.Version,
                GrantsJson = Serialize(policy.Grants),
                ETag = etag,
                UpdatedAt = now
            };
            context.ResourceAccessPolicies.Add(row);
        }
        else
        {
            row.Namespace = policy.Resource.Namespace.Value;
            row.Kind = policy.Resource.Kind;
            row.Generation = policy.Resource.Generation;
            row.Version = policy.Resource.Version;
            row.GrantsJson = Serialize(policy.Grants);
            row.ETag = etag;
            row.UpdatedAt = now;
        }
        await SaveAsync(context, cancellationToken);
        return Map(row);
    }

    public async Task DeleteAsync(Guid resourceUid, ResourceScopeRef ownerScopeRef, string? ifMatch,
        CancellationToken cancellationToken)
    {
        EnsureCanWrite(ownerScopeRef);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.ResourceAccessPolicies.SingleOrDefaultAsync(value => value.ResourceUid == resourceUid
            && value.OwnerScopeRef == ownerScopeRef.Value, cancellationToken)
            ?? throw new ResourceAccessPolicyException("policy_not_found", $"Access policy for resource '{resourceUid:D}' was not found.");
        if (ifMatch is not null && !string.Equals(row.ETag, ifMatch, StringComparison.Ordinal))
            throw new ResourceConcurrencyException("The supplied ETag does not match the current access policy version.");
        context.ResourceAccessPolicies.Remove(row);
        await SaveAsync(context, cancellationToken);
    }

    private async Task EnsureCanReadAsync(ResourceScopeRef ownerScopeRef, CancellationToken cancellationToken)
    {
        if (requestContext.AccessMode == ControlPlaneAccessMode.System) return;
        var currentRef = requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.Tenant => ResourceScopeRef.Tenant(requestContext.Current.TenantId),
            ControlPlaneAccessMode.Workspace => ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId),
            _ => throw new InvalidOperationException("Control Plane access requires an explicit scope.")
        };
        var current = await scopes.ResolveAsync(currentRef, cancellationToken);
        if (current is null || ownerScopeRef != currentRef
            && !current.Ancestors.Any(value => value.Ref == ownerScopeRef))
            throw new InvalidOperationException($"The current Control Plane context cannot read scope '{ownerScopeRef}'.");
    }

    private void EnsureCanWrite(ResourceScopeRef ownerScopeRef)
    {
        var allowed = requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.System => true,
            ControlPlaneAccessMode.Tenant => ownerScopeRef == ResourceScopeRef.Tenant(requestContext.Current.TenantId),
            ControlPlaneAccessMode.Workspace => ownerScopeRef == ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId),
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"The current Control Plane context cannot write scope '{ownerScopeRef}'.");
    }

    private static StoredResourceAccessPolicy Map(ResourceAccessPolicyRow row) => new(
        new(new(row.ResourceUid, ResourceScopeRef.Parse(row.OwnerScopeRef), ResourceNamespace.Parse(row.Namespace),
            row.Kind, row.Generation, row.Version),
            JsonSerializer.Deserialize<ResourceAccessGrant[]>(row.GrantsJson, JsonOptions) ?? []),
        row.ETag, row.UpdatedAt);
    private static string Serialize(IReadOnlyList<ResourceAccessGrant> grants) =>
        JsonSerializer.Serialize(grants, JsonOptions);
    private static string NewETag() => $"\"{Guid.NewGuid():N}\"";
    private static async Task SaveAsync(ResourceManagementDbContext context, CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception)
        { throw new ResourceConcurrencyException(exception.InnerException?.Message ?? exception.Message); }
    }
}
