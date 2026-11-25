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
    ConnectedServiceOperationTypeManagementService types,
    IRequestContextScopeFactory? requestContexts = null,
    Agentstration.Schemas.Contracts.ISchemaBindingResolver? schemaBindings = null)
{
    public async Task<StoredResource<ConnectedServiceOperationResource>> AdoptContentAsync(
        ScopedResourceAddress address, AdoptOperationContentContractRequest request, string? etag, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(etag)) throw Error("operation_etag_required", "An exact current ETag is required.");
        var stored = await store.GetExactAsync<ConnectedServiceOperationResource>(address, ct)
            ?? throw Error("operation_not_found", "The operation is unavailable.");
        if (request.Schema is null || request.ContentPointer is not ("" or "/content")
            || request.MediaType is not ("application/json" or "application/xml" or "text/xml" or "application/rss+xml" or "application/atom+xml")
            || schemaBindings is null) throw Error("operation_content_contract_invalid", "Select an exact published contract and a supported response content boundary.");
        return await scopes.WriteAsync(stored.Value, address.ScopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var format = request.MediaType == "application/json" ? Agentstration.Schemas.SchemaFormatIds.JsonSchema
                : Agentstration.Schemas.BuiltinXmlSchemaFormatHandler.Format;
            if (format != Agentstration.Schemas.SchemaFormatIds.JsonSchema && request.ContentPointer != "/content")
                throw Error("operation_content_contract_invalid", "XML contracts apply to the envelope content string.");
            var snapshot = await schemaBindings.ResolveAsync("$.operation.content", new() { Reference = request.Schema },
                new(address.ScopeRef, address.Namespace, format), token);
            if (stored.Value.Definition.ContentContractRevisions?.Count >= 64)
                throw Error("operation_content_revision_limit", "The operation content revision retention limit has been reached.");
            var revision = new OperationContentContractRevision(stored.Value.Generation, stored.Value.Definition.DefinitionDigest,
                stored.Value.Definition.Service, stored.Value.Definition.Type, stored.Value.Definition.AdoptedContentContract);
            var updated = stored.Value with
            {
                Generation = checked(stored.Value.Generation + 1),
                Definition = stored.Value.Definition with { AdoptedContentContract = new(request.MediaType, request.ContentPointer, snapshot) { ProducerDefinitionDigest = stored.Value.Definition.DefinitionDigest }, ContentContractRevisions = [.. stored.Value.Definition.ContentContractRevisions ?? [], revision] }
            };
            return await store.PutExactAsync(address.ScopeRef, updated, etag, false, token);
        }, ct);
    }

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
        using var ownerRead = requestContexts?.PushSystem();
        var operation = (await store.GetExactAsync<ConnectedServiceOperationResource>(address, cancellationToken))?.Value
            ?? throw Error(consumer == address.ScopeRef ? "operation_not_found" : "operation_access_denied", "The operation is unavailable.");
        if (!(await access.EvaluateAsync(new(operation.Uid, address.ScopeRef, address.Namespace,
                address.Kind, operation.Generation), consumer, ResourceAccessAction.Use, cancellationToken)).Allowed)
            throw Error("operation_access_denied", "The operation does not authorize this consumer.");
        if (operation.ScopeRef != operation.Definition.Service.Service.ScopeRef
            || operation.Namespace != operation.Definition.Service.Service.Address.Namespace)
            throw Error("operation_access_denied", "The operation does not match its owning service.");
        if (operation.Generation != reference.Generation
            && operation.Definition.ContentContractRevisions?.SingleOrDefault(r => r.Generation == reference.Generation && r.DefinitionDigest == reference.DefinitionDigest) is { } prior)
        {
            // Administrative content adoption keeps old consumer pins; current access/availability still wins.
            operation = operation with
            {
                Generation = prior.Generation,
                Definition = operation.Definition with
                { Service = prior.Service, Type = prior.Type, AdoptedContentContract = prior.Content }
            };
        }
        if (operation.Uid != reference.OperationUid || operation.Generation != reference.Generation
            || operation.Definition.DefinitionDigest != reference.DefinitionDigest)
            throw Error("operation_revision_stale", "The exact operation revision is unavailable. Reconcile explicitly.");
        if (requireEnabled && !operation.Enabled
            || operation.ObservedState.Availability != ConnectedServiceOperationAvailability.Available)
            throw Error("operation_unavailable", "The operation is disabled, stale or unavailable.");
        _ = await services.InspectAsync(operation.Definition.Service,
            new(consumer, operation.Address), cancellationToken);
        // The consumer uses the configured operation; its owner's exact adapter dependencies stay private.
        var (type, version) = await types.ResolveExactAsync(operation.Definition.Type, operation.Namespace, address.ScopeRef, cancellationToken);
        if (!(await access.EvaluateAsync(new(type.Uid, type.ScopeRef!.Value, type.Namespace,
                type.Kind, type.Generation), address.ScopeRef, ResourceAccessAction.Use, cancellationToken)).Allowed)
            throw Error("operation_type_access_denied", "The operation type does not authorize this consumer.");
        if (operation.Definition.AdoptedContentContract is { } adopted)
        {
            if (adopted.ProducerDefinitionDigest is { } producer && producer != operation.Definition.DefinitionDigest)
                throw Error("operation_content_producer_changed", "The discovered producer contract changed. Review content adoption explicitly.");
            if (schemaBindings is null || adopted.Schema.Source is null) throw Error("operation_content_contract_unavailable", "The adopted content contract is unavailable.");
            var checkedSchema = await schemaBindings.ResolveAsync(adopted.Schema.Path, new() { Reference = adopted.Schema.Source },
                new(consumer, operation.Namespace, adopted.Schema.Format), cancellationToken);
            if (checkedSchema.SchemaUid != adopted.Schema.SchemaUid || checkedSchema.DefinitionHash != adopted.Schema.DefinitionHash
                || checkedSchema.ParentDefinitionHash != adopted.Schema.ParentDefinitionHash)
                throw Error("operation_content_contract_changed", "The exact adopted content contract changed. Adopt explicitly.");
        }
        return (operation, version);
    }

    public async Task<ConnectedServiceOperationContract> InspectContractAsync(
        ConnectedServiceOperationRevisionReference reference, CancellationToken cancellationToken)
    {
        var (_, type) = await ResolveAsync(reference, scopes.DefaultScopeRef(ConnectedServiceResourceKinds.ConnectedServiceOperation), false, cancellationToken);
        return new(reference, type);
    }

    private static ConnectedServiceOperationDiscoveryException Error(string code, string message) => new(code, message);
}
