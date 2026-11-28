using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Security.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.ResourceManagement.Storage.Sqlite;

internal sealed class ResourceOperationalStateRow
{
    public Guid ResourceUid { get; set; }
    public required string OwnerScopeRef { get; set; }
    public required string Namespace { get; set; }
    public required string Kind { get; set; }
    public bool Enabled { get; set; }
    public required string ETag { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SqliteResourceOperationalStateStore(
    IDbContextFactory<ResourceManagementDbContext> contextFactory,
    ICurrentRequestContext requestContext,
    IResourceScopeResolver scopes,
    TimeProvider timeProvider,
    ISecurityAuditWriter? audit = null) : IResourceOperationalStateStore
{
    public async Task<ResourceOperationalState?> GetAsync(ResourceAccessIdentity resource, CancellationToken cancellationToken)
    {
        await EnsureCanReadAsync(resource.OwnerScopeRef, cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.Set<ResourceOperationalStateRow>().AsNoTracking().SingleOrDefaultAsync(value =>
            value.ResourceUid == resource.Uid && value.OwnerScopeRef == resource.OwnerScopeRef.Value
            && value.Namespace == resource.Namespace.Value && value.Kind == resource.Kind, cancellationToken);
        return row is null ? null : Map(row);
    }

    public async Task<ResourceOperationalState> PutAsync(ResourceAccessIdentity resource, bool enabled, string? ifMatch,
        CancellationToken cancellationToken)
    {
        EnsureCanWrite(resource.OwnerScopeRef);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.Set<ResourceOperationalStateRow>().SingleOrDefaultAsync(value => value.ResourceUid == resource.Uid,
            cancellationToken);
        if (row is null && ifMatch is not null || row is not null && row.ETag != ifMatch)
            throw new ResourceConcurrencyException("The operational state ETag does not match.");
        if (row is not null && (row.OwnerScopeRef != resource.OwnerScopeRef.Value
            || row.Namespace != resource.Namespace.Value || row.Kind != resource.Kind))
            throw new ResourceConcurrencyException("The operational state identity does not match.");
        if (row is null)
        {
            row = new() { ResourceUid = resource.Uid, OwnerScopeRef = resource.OwnerScopeRef.Value,
                Namespace = resource.Namespace.Value, Kind = resource.Kind, ETag = string.Empty };
            context.Add(row);
        }
        row.Enabled = enabled;
        row.ETag = $"\"{Guid.NewGuid():N}\"";
        row.UpdatedAt = timeProvider.GetUtcNow();
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { throw new ResourceConcurrencyException("The operational state changed concurrently."); }
        if (audit is not null) await audit.WriteAsync(new(SecurityAuditActions.ResourceOperationalStateSet,
            TenantId: resource.OwnerScopeRef.Kind == ResourceScopeKind.Tenant ? resource.OwnerScopeRef.TargetId : null,
            WorkspaceId: resource.OwnerScopeRef.Kind == ResourceScopeKind.Workspace ? resource.OwnerScopeRef.TargetId : null,
            ReasonCode: enabled ? "enabled" : "disabled"), cancellationToken);
        return Map(row);
    }

    private async Task EnsureCanReadAsync(ResourceScopeRef ownerScopeRef, CancellationToken cancellationToken)
    {
        if (requestContext.AccessMode is ControlPlaneAccessMode.System or ControlPlaneAccessMode.Instance)
        {
            if (requestContext.AccessMode == ControlPlaneAccessMode.Instance
                && ownerScopeRef != ResourceScopeRef.Instance)
                throw new InvalidOperationException($"The current Control Plane context cannot read scope '{ownerScopeRef}'.");
            return;
        }
        var currentRef = requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.Tenant => ResourceScopeRef.Tenant(requestContext.Current.TenantId),
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(requestContext.Current.TenantId),
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
            ControlPlaneAccessMode.Instance => ownerScopeRef == ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => ownerScopeRef == ResourceScopeRef.Tenant(requestContext.Current.TenantId),
            ControlPlaneAccessMode.Tenant => ownerScopeRef == ResourceScopeRef.Tenant(requestContext.Current.TenantId),
            ControlPlaneAccessMode.Workspace => ownerScopeRef == ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId),
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"The current Control Plane context cannot write scope '{ownerScopeRef}'.");
    }


    private static ResourceOperationalState Map(ResourceOperationalStateRow row) => new(
        new(row.ResourceUid, ResourceScopeRef.Parse(row.OwnerScopeRef), ResourceNamespace.Parse(row.Namespace), row.Kind),
        row.Enabled, row.ETag, row.UpdatedAt);
}
