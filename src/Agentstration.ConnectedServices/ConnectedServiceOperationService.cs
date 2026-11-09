using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.ConnectedServices;

public sealed class ConnectedServiceOperationService(
    IResourceStore store,
    IResourceScopeOperations scopes,
    IResourceAccessPolicyService access,
    DescendantResourceUseAuthorizer hierarchy,
    ConnectedServiceManagementService services,
    ConnectedServiceOperationTypeManagementService types)
{
    public async Task<IReadOnlyList<ConnectedServiceOperationResource>> ListAsync(CancellationToken cancellationToken) =>
        (await store.ListAllAsync<ConnectedServiceOperationResource>(ConnectedServiceResourceKinds.ConnectedServiceOperation, cancellationToken))
            .Select(item => item.Value).ToArray();

    public async Task<StoredResource<ConnectedServiceOperationResource>> SetEnabledAsync(
        ScopedResourceAddress address, bool enabled, string? etag, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(etag)) throw Error("operation_etag_required", "An exact current ETag is required.");
        var stored = await store.GetExactAsync<ConnectedServiceOperationResource>(address, cancellationToken)
            ?? throw Error("operation_not_found", "The operation is unavailable.");
        if (enabled)
            _ = await ResolveAsync(new(address, stored.Value.Uid, stored.Value.Generation,
                stored.Value.Definition.DefinitionDigest), address.ScopeRef, false, cancellationToken);
        var updated = stored.Value with { Enabled = enabled, Generation = checked(stored.Value.Generation + 1) };
        return await scopes.WriteAsync(updated, address.ScopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(address.ScopeRef, updated, etag, false, token), cancellationToken);
    }

    public async Task<(ConnectedServiceOperationResource Operation, ConnectedServiceOperationTypeVersionDescriptor Type)>
        ResolveAsync(ConnectedServiceOperationRevisionReference reference, ResourceScopeRef consumer,
            bool requireEnabled, CancellationToken cancellationToken)
    {
        var address = reference.Operation;
        if (address.Kind != ConnectedServiceResourceKinds.ConnectedServiceOperation
            || !await hierarchy.IsSameOrDescendantAsync(address.ScopeRef, consumer, cancellationToken))
            throw Error("operation_access_denied", "The operation is outside the consumer scope.");
        var operation = (await store.GetExactAsync<ConnectedServiceOperationResource>(address, cancellationToken))?.Value
            ?? throw Error("operation_not_found", "The operation is unavailable.");
        if (operation.Uid != reference.OperationUid || operation.Generation != reference.Generation
            || operation.Definition.DefinitionDigest != reference.DefinitionDigest)
            throw Error("operation_revision_stale", "The exact operation revision is unavailable. Reconcile explicitly.");
        if (requireEnabled && !operation.Enabled
            || operation.ObservedState.Availability != ConnectedServiceOperationAvailability.Available)
            throw Error("operation_unavailable", "The operation is disabled, stale or unavailable.");
        if (!(await access.EvaluateAsync(new(operation.Uid, address.ScopeRef, address.Namespace,
                address.Kind, operation.Generation), consumer, ResourceAccessAction.Use, cancellationToken)).Allowed)
            throw Error("operation_access_denied", "The operation does not authorize this consumer.");
        _ = await services.InspectAsync(operation.Definition.Service,
            new(consumer, operation.Address), cancellationToken);
        var (type, version) = await types.ResolveExactAsync(operation.Definition.Type, operation.Namespace, consumer, cancellationToken);
        if (!(await access.EvaluateAsync(new(type.Uid, type.ScopeRef!.Value, type.Namespace,
                type.Kind, type.Generation), consumer, ResourceAccessAction.Use, cancellationToken)).Allowed)
            throw Error("operation_type_access_denied", "The operation type does not authorize this consumer.");
        return (operation, version);
    }

    private static ConnectedServiceOperationDiscoveryException Error(string code, string message) => new(code, message);
}
