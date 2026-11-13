using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Storage;

public sealed class StorageManagementService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    DescendantResourceUseAuthorizer useAuthorizer,
    IResourceAccessPolicyService accessPolicies,
    IConnectedServiceExportInspector exports,
    IStorageTypeAvailabilityProvider availability,
    IEnumerable<IStorageUsageProvider> usageProviders,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    IPlatformAuthorizationService platformAuthorization,
    TimeProvider timeProvider,
    IEnumerable<IStorageDefinitionValidator>? definitionValidators = null) : IStorageResolver
{
    public async Task<IReadOnlyList<StoredResource<StorageResource>>> ListAsync(
        ResourceScopeRef scope, int skip, int take, CancellationToken token)
    {
        var resources = await store.ListVisibleAsync<StorageResource>(scope, StorageResourceKinds.Storage, skip, take, token);
        var result = new List<StoredResource<StorageResource>>();
        foreach (var resource in resources) result.Add(await ObservedAsync(resource, token));
        return result;
    }

    public async Task<StoredResource<StorageResource>?> GetExactAsync(
        ResourceScopeRef scope, ResourceNamespace ns, string name, CancellationToken token)
    {
        var stored = await store.GetExactAsync<StorageResource>(Address(scope, ns, name), token);
        return stored is null ? null : await ObservedAsync(stored, token);
    }

    public async Task ValidateForCreateAsync(StorageResource resource, CancellationToken token)
    {
        var scope = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(StorageResourceKinds.Storage);
        await ValidateAsync(resource with { ScopeRef = scope }, token);
    }

    public async Task<StoredResource<StorageResource>> CreateAsync(StorageResource resource, CancellationToken token)
    {
        var scope = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(StorageResourceKinds.Storage);
        var scoped = resource with
        {
            ScopeRef = scope,
            Definition = resource.Definition with
            { Collections = resource.Definition.Collections.Select(c => c with { Uid = Guid.NewGuid() }).ToArray() }
        };
        return await scopeOperations.WriteAsync(scoped, scope, AuthorizationPermissions.ResourcesWrite, async ct =>
        {
            await ValidateAsync(scoped, ct);
            return await store.PutExactAsync(scope, scoped with
            {
                Generation = 1,
                ObservedState = Ready(scoped.Definition),
                Status = new() { ProvisioningState = ProvisioningState.Succeeded }
            }, null, true, ct);
        }, token);
    }

    public async Task<StoredResource<StorageResource>> PutExactAsync(ResourceScopeRef scope, ResourceNamespace ns,
        string name, StorageProperties definition, string? etag, CancellationToken token)
    {
        var stored = await RequireAsync(scope, ns, name, token);
        StorageValidation.ETag(etag, stored.ETag);
        Shape(stored.Value with { Definition = definition });
        if (StorageDigests.Binding(stored.Value.Definition) != StorageDigests.Binding(definition))
            throw new StorageException("storage_reconciliation_required",
                "Binding, type, configuration and envelope changes require explicit reconciliation.", 409);
        return await ReplaceAsync(stored, definition, etag!, token);
    }

    public async Task<StoredResource<StorageResource>> ReconcileAsync(ResourceScopeRef scope, ResourceNamespace ns,
        string name, StorageProperties? definition, string? etag, CancellationToken token)
    {
        var stored = await RequireAsync(scope, ns, name, token);
        StorageValidation.ETag(etag, stored.ETag);
        if (definition is not null) return await ReplaceAsync(stored, definition, etag!, token);
        return await scopeOperations.WriteAsync(stored.Value, scope, AuthorizationPermissions.ResourcesWrite, async ct =>
        {
            var observed = await ObserveAsync(stored.Value, ct);
            return await store.PutExactAsync(scope, stored.Value with { ObservedState = observed }, etag, false, ct);
        }, token);
    }

    public async Task<StoredResource<StorageResource>> DeactivateAsync(ResourceScopeRef scope, ResourceNamespace ns,
        string name, string? etag, CancellationToken token)
    {
        var stored = await RequireAsync(scope, ns, name, token);
        StorageValidation.ETag(etag, stored.ETag);
        return await scopeOperations.WriteAsync(stored.Value, scope, AuthorizationPermissions.ResourcesWrite, async ct =>
        {
            await EnsureUnusedAsync(stored.Value, ct);
            var definition = stored.Value.Definition with { Enabled = false };
            return await store.PutExactAsync(scope, stored.Value with
            {
                Generation = checked(stored.Value.Generation + 1),
                Definition = definition,
                ObservedState = Ready(definition)
            }, etag, false, ct);
        }, token);
    }

    public async Task<IReadOnlyList<StorageUsage>> GetUsagesAsync(ResourceScopeRef scope, ResourceNamespace ns,
        string name, CancellationToken token)
    {
        _ = await RequireAsync(scope, ns, name, token);
        return await UsagesAsync(Address(scope, ns, name), token);
    }

    public async Task<ProtectedStorageContext> ResolveAsync(StorageRevisionReference storage,
        StorageResolutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(context);
        EnsureConsumerContext(context.ConsumerScopeRef);
        if (requestContext.AccessMode == ControlPlaneAccessMode.Instance)
        {
            if (requestContext.ActorPrincipalId is not { } principal
                || !await platformAuthorization.IsPlatformAdministratorAsync(principal, cancellationToken))
                throw new StorageException("storage_access_denied", "Instance resolution requires platform administration rights.", 403);
        }
        else if (requestContext.AccessMode != ControlPlaneAccessMode.System)
            await authorization.EnsurePermissionAsync(requestContext.Current, AuthorizationPermissions.ResourcesRead, cancellationToken);
        if (storage.Storage.Kind != StorageResourceKinds.Storage || storage.StorageUid == Guid.Empty || storage.Generation < 1)
            throw new StorageException("storage_reference_invalid", "An exact Storage revision is required.");
        var stored = await RequireAsync(storage.Storage.ScopeRef, storage.Storage.Namespace, storage.Storage.Name, cancellationToken);
        if (stored.Value.Uid != storage.StorageUid || stored.Value.Generation != storage.Generation
            || StorageDigests.Binding(stored.Value.Definition) != storage.BindingDigest)
            throw new StorageException("storage_revision_stale", "The selected Storage revision is stale.", 409);
        await EnsureUseAsync(stored.Value, context.ConsumerScopeRef, cancellationToken);
        if (!stored.Value.Definition.Enabled)
            throw new StorageException("storage_disabled", "The Storage is disabled.", 409);
        var version = await ValidateAsync(stored.Value, cancellationToken);
        return new(storage, stored.Value.Definition.Type, stored.Value.Definition.ConnectedService,
            stored.Value.Definition.ConnectedServiceType,
            stored.Value.Definition.Configuration.Clone(), stored.Value.Definition.Envelope.Clone(),
            stored.Value.Definition.Bindings.ToArray(), version.Capabilities, version.Limits, version.Documents, stored.Value.Definition.Collections);
    }

    private async Task<StoredResource<StorageResource>> ReplaceAsync(StoredResource<StorageResource> stored,
        StorageProperties definition, string etag, CancellationToken token)
    {
        var oldIds = stored.Value.Definition.Collections.Select(c => c.Uid).ToHashSet();
        if (definition.Collections.Any(c => c.Uid != Guid.Empty && !oldIds.Contains(c.Uid)))
            throw new StorageException("storage_collection_identity_invalid", "New collections must receive a server-owned identity.");
        definition = definition with { Collections = definition.Collections.Select(c => c.Uid == Guid.Empty ? c with { Uid = Guid.NewGuid() } : c).ToArray() };
        var scope = stored.Value.ScopeRef!.Value;
        return await scopeOperations.WriteAsync(stored.Value, scope, AuthorizationPermissions.ResourcesWrite, async ct =>
        {
            var changed = stored.Value with { Definition = definition };
            Shape(changed);
            if (!definition.Enabled || StorageDigests.Binding(stored.Value.Definition) != StorageDigests.Binding(definition))
                await EnsureUnusedAsync(stored.Value, ct);
            await ValidateAsync(changed, ct);
            return await store.PutExactAsync(scope, changed with
            {
                Generation = checked(stored.Value.Generation + 1),
                ObservedState = Ready(definition)
            }, etag, false, ct);
        }, token);
    }

    private async Task<StorageTypeVersionDescriptor> ValidateAsync(StorageResource resource, CancellationToken token)
    {
        Shape(resource);
        var definition = resource.Definition;
        var scope = resource.ScopeRef!.Value;
        ResourceScopePolicy.EnsureAllowed(resource, scope);
        await useAuthorizer.ValidateAsync(scope, definition.UsePolicy, token);
        var reference = definition.Type;
        var typeScope = reference.Type.ScopeRef!.Value;
        var address = reference.Type.Resolve(resource.Namespace, StorageResourceKinds.StorageType);
        var type = (await store.GetExactAsync<StorageTypeResource>(new(typeScope, address.Namespace, address.Kind, address.Name), token))?.Value
            ?? throw new StorageException("storage_type_unavailable", "The pinned Storage type is missing or inaccessible.", 409);
        await EnsureUseAsync(type, scope, token);
        if (type.Uid != reference.TypeUid)
            throw new StorageException("storage_type_stale", "The pinned Storage type identity is stale.", 409);
        if (!type.Definition.Enabled)
            throw new StorageException("storage_type_unavailable", "The Storage type is disabled.", 409);
        var version = type.Definition.Versions.SingleOrDefault(v => v.Version == reference.Version);
        if (version is null || version.SchemaDigest != reference.SchemaDigest
            || StorageDigests.Schema(version.ConfigurationSchema) != reference.SchemaDigest
            || StorageDigests.Version(version) != reference.ContractDigest)
            throw new StorageException("storage_type_stale", "The pinned type contract has changed.", 409);
        if (!await availability.IsAvailableAsync(type, version, token))
            throw new StorageException("storage_provider_unavailable", "The exact Storage provider is unavailable.", 409);
        await StorageValidation.InstanceAsync(version.ConfigurationSchema, definition.Configuration, token);
        await StorageValidation.InstanceAsync(version.EnvelopeSchema, definition.Envelope, token);
        if (definition.Collections.Count > 32 || definition.Collections.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != definition.Collections.Count
            || definition.Collections.Where(c => c.Uid != Guid.Empty).Select(c => c.Uid).Distinct().Count() != definition.Collections.Count(c => c.Uid != Guid.Empty)
            || definition.Collections.Count > 0 && version.Documents is null)
            throw new StorageException("storage_collections_invalid", "Collections require a document-capable Storage and unique bounded identities.");
        foreach (var collection in definition.Collections)
        {
            StorageValidation.Name(collection.Name); StorageValidation.Display(collection.DisplayName, null);
            StorageValidation.Json(collection.Schema); await StorageValidation.SchemaAsync(collection.Schema, token);
            var policy = collection.Policy;
            if (policy.MaximumPageSize is < 1 or > 100 || policy.MaximumDocumentBytes is < 1 or > 65536
                || policy.MaximumResultBytes is < 65536 or > 196608 || policy.QueryProfiles is not { Count: <= 8 } || policy.Query && policy.QueryProfiles.Count == 0
                || policy.QueryProfiles.Any(id => !version.Documents!.QueryProfiles.Any(p => p.Id == id)))
                throw new StorageException("storage_collection_policy_invalid", "The collection requests unsupported query profiles or limits.");
        }
        foreach (var validator in definitionValidators ?? []) validator.Validate(type, version, definition);
        await ValidateMappingAsync(resource, version, token);
        return version;
    }

    private async Task ValidateMappingAsync(StorageResource resource, StorageTypeVersionDescriptor version, CancellationToken token)
    {
        var definition = resource.Definition;
        if (definition.ConnectedService is not { } selected)
        {
            if (version.RequiresConnectedService || definition.ConnectedServiceType is not null || definition.Bindings.Count != 0)
                throw new StorageException("storage_service_required", "The type requires an exact Connected Service and explicit bindings.");
            return;
        }
        if (!version.RequiresConnectedService)
            throw new StorageException("storage_service_unexpected", "This local type does not accept a Connected Service.");
        var serviceAddress = selected.Service.Address;
        var service = (await store.GetExactAsync<ConnectedServiceResource>(new(selected.Service.ScopeRef,
            serviceAddress.Namespace, serviceAddress.Kind, serviceAddress.Name), token))?.Value
            ?? throw new StorageException("storage_service_unavailable", "The Connected Service is missing or inaccessible.", 409);
        await EnsureUseAsync(service, resource.ScopeRef!.Value, token);
        ConnectedServiceExportMetadata metadata;
        try
        {
            metadata = await exports.InspectAsync(selected, new(resource.ScopeRef.Value, resource.Address), token);
        }
        catch (ConnectedServiceExportInspectionException exception)
        {
            throw new StorageException(exception.Code, "The exact Connected Service or its exports are unavailable, stale or inaccessible.",
                exception.Code == "storage_service_access_denied" ? 403 : 409);
        }
        if (definition.ConnectedServiceType is null || metadata.Type != definition.ConnectedServiceType)
            throw new StorageException("storage_exports_stale", "The pinned Connected Service type is stale.", 409);
        var requirements = version.Requirements.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in definition.Bindings)
        {
            if (!seen.Add(binding.RequirementId) || !requirements.TryGetValue(binding.RequirementId, out var requirement))
                throw new StorageException("storage_mapping_invalid", "Bindings must name unique declared requirements.");
            var export = metadata.Exports.SingleOrDefault(e => e.Id == binding.Export.Id);
            if (export is null || export != binding.Export)
                throw new StorageException("storage_export_stale", "A selected export is missing or its exact descriptor is stale.", 409);
            if (export.Type != requirement.Type || export.Protection != requirement.Protection
                || (requirement.Format is not null && export.Format != requirement.Format))
                throw new StorageException("storage_export_incompatible", "An export does not match the requirement type, protection or format.");
        }
        if (version.Requirements.Any(r => r.Required && !seen.Contains(r.Id)))
            throw new StorageException("storage_mapping_incomplete", "Every mandatory requirement must be bound explicitly.");
    }

    private static void Shape(StorageResource resource)
    {
        if (resource.Kind != StorageResourceKinds.Storage || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw new StorageException("storage_invalid", "Invalid Storage resource envelope.");
        StorageValidation.Name(resource.Name);
        var definition = resource.Definition ?? throw new StorageException("storage_invalid", "Storage properties are required.");
        StorageValidation.Display(definition.DisplayName, definition.Description);
        if (definition.Type?.Type?.ScopeRef is null || string.IsNullOrWhiteSpace(definition.Type.Type.Name)
            || definition.Type.TypeUid == Guid.Empty || string.IsNullOrWhiteSpace(definition.Type.Version)
            || string.IsNullOrWhiteSpace(definition.Type.SchemaDigest) || string.IsNullOrWhiteSpace(definition.Type.ContractDigest))
            throw new StorageException("storage_type_reference_invalid", "An exact type UID, scope, version and digests are required.");
        if (definition.Bindings is null || definition.Bindings.Count > 64
            || definition.Bindings.Any(b => b?.Export is null || string.IsNullOrWhiteSpace(b.RequirementId)
                || b.RequirementId.Length > 128 || b.Export.Id is not { Length: >= 1 and <= 64 }
                || b.Export.Description?.Length > 2000 || b.Export.Format?.Length > 128))
            throw new StorageException("storage_mapping_invalid", "At most 64 non-null explicit bindings are allowed.");
        if (definition.ConnectedService is { } selected
            && (selected.Service is null || selected.Service.Address.Kind != ConnectedServiceResourceKinds.ConnectedService
                || selected.ServiceUid == Guid.Empty || selected.Generation < 1))
            throw new StorageException("storage_service_reference_invalid", "An exact Connected Service UID, scope and generation are required.");
        StorageValidation.Json(definition.Configuration);
        StorageValidation.Json(definition.Envelope);
    }

    private async Task EnsureUseAsync(Resource resource, ResourceScopeRef consumer, CancellationToken token)
    {
        var scope = resource.ScopeRef!.Value;
        if (!await useAuthorizer.IsSameOrDescendantAsync(scope, consumer, token))
            throw new StorageException("storage_access_denied", "The dependency is outside the consumer's scope hierarchy.", 403);
        // Generic Use is the current Storage-family authority. Do not impose an unadministrable
        // second grant layer while UsePolicy is deferred to #1078. Connected Service export
        // inspection retains the independent restrictions owned by that family.
        var decision = await accessPolicies.EvaluateAsync(new(resource.Uid, scope, resource.Namespace, resource.Kind,
            resource.Generation), consumer, ResourceAccessAction.Use, token);
        if (!decision.Allowed)
            throw new StorageException("storage_access_denied", $"The resource cannot be used from this scope ({decision.ReasonCode}).", 403);
    }

    private void EnsureConsumerContext(ResourceScopeRef consumer)
    {
        if (requestContext.AccessMode == ControlPlaneAccessMode.System) return;
        var current = requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.Tenant or ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(requestContext.Current.TenantId),
            ControlPlaneAccessMode.Workspace => ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId),
            _ => default
        };
        if (consumer != current || current == default)
            throw new StorageException("storage_access_denied", "The consumer scope must match the authenticated request context.", 403);
    }

    private async Task<StoredResource<StorageResource>> RequireAsync(ResourceScopeRef scope, ResourceNamespace ns, string name, CancellationToken token) =>
        await store.GetExactAsync<StorageResource>(Address(scope, ns, name), token)
        ?? throw new StorageException("storage_not_found", "Storage not found.", 404);

    private async Task<IReadOnlyList<StorageUsage>> UsagesAsync(ScopedResourceAddress address, CancellationToken token)
    {
        var result = new List<StorageUsage>();
        foreach (var provider in usageProviders) result.AddRange(await provider.GetUsagesAsync(address, token));
        return result;
    }

    private async Task EnsureUnusedAsync(StorageResource resource, CancellationToken token)
    {
        if ((await UsagesAsync(Address(resource.ScopeRef!.Value, resource.Namespace, resource.Name), token)).Count != 0)
            throw new StorageException("storage_in_use", "The Storage has dependencies and cannot be deactivated or rebound.", 409);
    }

    private async Task<StoredResource<StorageResource>> ObservedAsync(StoredResource<StorageResource> stored, CancellationToken token) =>
        stored with { Value = stored.Value with { ObservedState = await ObserveAsync(stored.Value, token) } };

    private async Task<StorageObservedState> ObserveAsync(StorageResource resource, CancellationToken token)
    {
        if (!resource.Definition.Enabled) return Ready(resource.Definition);
        try
        {
            await ValidateAsync(resource, token);
            return Ready(resource.Definition);
        }
        catch (StorageException exception)
        {
            return new(exception.Code.Contains("stale", StringComparison.Ordinal) ? StorageAvailability.Stale : StorageAvailability.Unavailable,
                timeProvider.GetUtcNow(), StorageDigests.Binding(resource.Definition), [new(exception.Code, exception.Message)]);
        }
    }

    private StorageObservedState Ready(StorageProperties definition) =>
        new(definition.Enabled ? StorageAvailability.Ready : StorageAvailability.Disabled,
            timeProvider.GetUtcNow(), StorageDigests.Binding(definition), []);

    private static ScopedResourceAddress Address(ResourceScopeRef scope, ResourceNamespace ns, string name) =>
        new(scope, ns, StorageResourceKinds.Storage, name);
}
