using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.ConnectedServices;

public sealed record ConnectedServiceDiscoveryFlowRunRequest(
    string RunId,
    ConnectedServiceDiscoveryFlowReference Flow,
    ResourceNamespace OwnerNamespace,
    JsonElement Input,
    string CallerId,
    string CorrelationId,
    string? IdempotencyKey,
    Guid TenantId,
    Guid WorkspaceId,
    Guid PrincipalId)
{
    public ResourceScopeRef? LogicalScopeRef { get; init; }
}

public sealed record ConnectedServiceDiscoveryFlowRunSnapshot(
    string RunId,
    ConnectedServiceOperationDiscoveryState State,
    JsonElement? Output,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? CompletedAt);

public interface IConnectedServiceOperationDiscoveryFlowGateway
{
    Task<ConnectedServiceDiscoveryFlowRunSnapshot> StartAsync(
        ConnectedServiceDiscoveryFlowRunRequest request, CancellationToken cancellationToken);
    Task<ConnectedServiceDiscoveryFlowRunSnapshot?> GetAsync(
        Guid workspaceId, string runId, Guid tenantId, Guid principalId, CancellationToken cancellationToken);
    Task<ConnectedServiceDiscoveryFlowRunSnapshot> CancelAsync(
        Guid workspaceId, string runId, Guid tenantId, Guid principalId, CancellationToken cancellationToken);
}

public sealed class ConnectedServiceOperationDiscoveryException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ConnectedServiceOperationDiscoveryService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    ICurrentRequestContext requestContext,
    IRequestContextScopeFactory requestScopes,
    ConnectedServiceManagementService services,
    ConnectedServiceOperationTypeManagementService operationTypes,
    IConnectedServiceOperationDiscoveryFlowGateway flowRuns,
    TimeProvider timeProvider)
{
    public const int MaximumOperations = 1_000;
    public const int MaximumManifestBytes = 2 * 1024 * 1024;
    public const int MaximumDefinitionBytes = 256 * 1024;
    public const int MaximumIdempotencyKeyLength = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<T> InWorkspaceAsync<T>(RequestContext context, Func<Task<T>> action)
    {
        using var workspace = requestScopes.Push(context);
        return await action();
    }

    public async Task<(ConnectedServiceOperationDiscoveryResource Discovery, ConnectedServiceResource Service,
        ConnectedServiceTypeResource Type, ConnectedServiceTypeVersionDescriptor Version)> GetExecutionAsync(
        ResourceNamespace @namespace, string id, string runId, CancellationToken cancellationToken)
    {
        var context = RequireContext();
        var stored = await InWorkspaceAsync(context, () => store.GetExactAsync<ConnectedServiceOperationDiscoveryResource>(new(
            ResourceScopeRef.Workspace(context.WorkspaceId), @namespace,
            ConnectedServiceResourceKinds.ConnectedServiceOperationDiscovery, id), cancellationToken))
            ?? throw Error("connected_service_discovery_not_found", "The discovery was not found.");
        var discovery = stored.Value;
        var consumer = requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(context.TenantId),
            _ => ResourceScopeRef.Workspace(context.WorkspaceId)
        };
        if (discovery.FlowRunId != runId || discovery.CreatedBy != context.PrincipalId
            || discovery.TenantId != context.TenantId || discovery.WorkspaceId != context.WorkspaceId
            || (discovery.LogicalScopeRef ?? ResourceScopeRef.Workspace(discovery.WorkspaceId)) != consumer
            || IsTerminal(discovery.State))
            throw Error("connected_service_discovery_execution_denied", "The discovery execution context does not match.");
        var reference = discovery.Service;
        var service = await services.GetExactAsync(reference.Service.ScopeRef, reference.Service.Address.Namespace,
            reference.Service.Address.Name, cancellationToken)
            ?? throw Error("connected_service_unavailable", "The Connected Service is unavailable.");
        if (service.Value.Uid != reference.ServiceUid || service.Value.Generation != reference.Generation
            || service.Value.Namespace != reference.Service.Address.Namespace)
            throw Error("connected_service_revision_changed", "The Connected Service revision changed.");
        EnsureDiscoverable(service.Value);
        var (type, version) = await ResolveServiceTypeAsync(service.Value, cancellationToken);
        if (service.Value.Definition.Type != discovery.ServiceType
            || version.OperationDiscovery?.Flow != discovery.Flow)
            throw Error("connected_service_discovery_revision_changed", "The discovery dependencies changed.");
        await ValidateDependenciesAsync(version.OperationDiscovery, service.Value.Namespace,
            reference.Service.ScopeRef, cancellationToken);
        return (discovery, service.Value, type, version);
    }

    public async Task<StoredResource<ConnectedServiceOperationDiscoveryResource>> StartAsync(
        ResourceScopeRef serviceScope,
        string serviceName,
        string? correlationId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        if (idempotencyKey?.Length > MaximumIdempotencyKeyLength)
            throw Error("connected_service_discovery_idempotency_key_too_long",
                $"Idempotency keys cannot exceed {MaximumIdempotencyKeyLength} characters.");
        var service = await services.GetExactAsync(serviceScope, serviceName, cancellationToken)
            ?? throw new ConnectedServiceNotFoundException(serviceName);
        EnsureDiscoverable(service.Value);
        var (serviceType, version) = await ResolveServiceTypeAsync(service.Value, cancellationToken);
        var descriptor = version.OperationDiscovery
            ?? throw Error("connected_service_operation_discovery_not_supported",
                $"Connected Service type '{serviceType.Address}:{version.Version}' does not define operation discovery.");
        await ValidateDependenciesAsync(descriptor, service.Value.Namespace, serviceScope, cancellationToken);

        var requestHash = Hash(JsonSerializer.SerializeToElement(new
        {
            serviceScope,
            service = new { service.Value.Uid, service.Value.Generation },
            type = service.Value.Definition.Type,
            descriptor.Flow,
            descriptor.ProducedTypes
        }, JsonOptions));
        var workspaceScope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var existing = await InWorkspaceAsync(context, () => store.ListExactAsync<ConnectedServiceOperationDiscoveryResource>(workspaceScope,
            ConnectedServiceResourceKinds.ConnectedServiceOperationDiscovery, 0, 1_000, cancellationToken));
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var previous = existing.SingleOrDefault(item => item.Value.Service.ServiceUid == service.Value.Uid
                && string.Equals(item.Value.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
            if (previous is not null)
            {
                if (!string.Equals(previous.Value.RequestHash, requestHash, StringComparison.Ordinal))
                    throw Error("connected_service_discovery_idempotency_conflict",
                        "The idempotency key is already bound to another discovery request.");
                return await SynchronizeAsync(previous, context, cancellationToken);
            }
        }
        foreach (var active in existing.Where(item => item.Value.Service.ServiceUid == service.Value.Uid))
        {
            var synchronized = await SynchronizeAsync(active, context, cancellationToken);
            if (!IsTerminal(synchronized.Value.State))
                throw Error("connected_service_discovery_already_running",
                    $"Discovery '{synchronized.Value.Name}' is already active.");
        }

        var id = string.IsNullOrWhiteSpace(idempotencyKey)
            ? $"discovery-{Guid.NewGuid():N}"
            : $"discovery-{HashText($"{context.WorkspaceId:D}:{service.Value.Uid:D}:{idempotencyKey}")[..32]}";
        var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId) ? id : correlationId.Trim();
        var serviceReference = new ConnectedServiceRevisionReference
        {
            Service = new(service.Value.Address, serviceScope),
            ServiceUid = service.Value.Uid,
            Generation = service.Value.Generation
        };
        var input = JsonSerializer.SerializeToElement(new ConnectedServiceOperationDiscoveryInput
        {
            Contract = ConnectedServiceOperationDiscoveryContracts.V1,
            DiscoveryId = id,
            Service = serviceReference,
            ServiceType = service.Value.Definition.Type,
            CorrelationId = effectiveCorrelationId,
            Caller = new(context.PrincipalId, context.TenantId, context.WorkspaceId)
        }, JsonOptions);
        var resource = new ConnectedServiceOperationDiscoveryResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceOperationDiscovery,
            Metadata = new() { Name = id, Namespace = service.Value.Namespace },
            ScopeRef = workspaceScope,
            Generation = 1,
            Status = Accepted(),
            Service = serviceReference,
            ServiceType = service.Value.Definition.Type,
            Flow = descriptor.Flow,
            FlowRunId = $"flowrun-connected-service-{id[10..]}",
            State = ConnectedServiceOperationDiscoveryState.Pending,
            CorrelationId = effectiveCorrelationId,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
            RequestHash = requestHash,
            CreatedBy = context.PrincipalId,
            TenantId = context.TenantId,
            WorkspaceId = context.WorkspaceId,
            LogicalScopeRef = requestContext.AccessMode switch
            {
                ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
                ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(context.TenantId),
                _ => workspaceScope
            },
            CreatedAt = timeProvider.GetUtcNow()
        };
        using (requestScopes.Push(context))
        {
            return await scopeOperations.WriteAsync(resource, workspaceScope, AuthorizationPermissions.RunsExecute,
                async token =>
                {
                    var stored = await store.PutExactAsync(workspaceScope, resource, null, true, token);
                    try
                    {
                        _ = await flowRuns.StartAsync(new(resource.FlowRunId, descriptor.Flow,
                            service.Value.Namespace, input, context.PrincipalId.ToString("D"), effectiveCorrelationId,
                            resource.IdempotencyKey, context.TenantId, context.WorkspaceId, context.PrincipalId)
                        { LogicalScopeRef = resource.LogicalScopeRef }, token);
                        return stored;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var failed = resource with
                        {
                            Generation = 2,
                            State = ConnectedServiceOperationDiscoveryState.Failed,
                            ErrorCode = "connected_service_discovery_start_failed",
                            ErrorMessage = exception.Message,
                            CompletedAt = timeProvider.GetUtcNow(),
                            Status = Failed()
                        };
                        _ = await store.PutExactAsync(workspaceScope, failed, stored.ETag, false, token);
                        throw;
                    }
                }, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ConnectedServiceOperationDiscoveryResource>> ListAsync(
        ResourceScopeRef serviceScope, string serviceName, CancellationToken cancellationToken)
    {
        var context = RequireContext();
        var service = await services.GetExactAsync(serviceScope, serviceName, cancellationToken)
            ?? throw new ConnectedServiceNotFoundException(serviceName);
        var stored = await InWorkspaceAsync(context, () => store.ListExactAsync<ConnectedServiceOperationDiscoveryResource>(
            ResourceScopeRef.Workspace(context.WorkspaceId),
            ConnectedServiceResourceKinds.ConnectedServiceOperationDiscovery, 0, 1_000, cancellationToken));
        var result = new List<ConnectedServiceOperationDiscoveryResource>();
        foreach (var item in stored.Where(item => item.Value.Service.ServiceUid == service.Value.Uid)
                     .OrderByDescending(item => item.Value.CreatedAt))
            result.Add((await SynchronizeAsync(item, context, cancellationToken)).Value);
        return result;
    }

    public async Task<StoredResource<ConnectedServiceOperationDiscoveryResource>> GetAsync(
        ResourceNamespace @namespace, string id, CancellationToken cancellationToken)
    {
        var context = RequireContext();
        var address = ScopedResourceAddress.Create(ResourceScopeRef.Workspace(context.WorkspaceId), @namespace,
            ConnectedServiceResourceKinds.ConnectedServiceOperationDiscovery, id);
        var stored = await InWorkspaceAsync(context, () => store.GetExactAsync<ConnectedServiceOperationDiscoveryResource>(address, cancellationToken))
            ?? throw Error("connected_service_discovery_not_found", $"Discovery '{id}' was not found.");
        return await SynchronizeAsync(stored, context, cancellationToken);
    }

    public async Task<StoredResource<ConnectedServiceOperationDiscoveryResource>> CancelAsync(
        ResourceNamespace @namespace, string id, CancellationToken cancellationToken)
    {
        var context = RequireContext();
        var stored = await GetAsync(@namespace, id, cancellationToken);
        if (!IsTerminal(stored.Value.State))
            _ = await flowRuns.CancelAsync(context.WorkspaceId, stored.Value.FlowRunId,
                context.TenantId, context.PrincipalId, cancellationToken);
        return await SynchronizeAsync(stored, context, cancellationToken);
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        var pending = (await store.ListAllAsync<ConnectedServiceOperationDiscoveryResource>(
                ConnectedServiceResourceKinds.ConnectedServiceOperationDiscovery, cancellationToken))
            .Where(item => !IsTerminal(item.Value.State));
        foreach (var stored in pending)
        {
            var value = stored.Value;
            if (await flowRuns.GetAsync(value.WorkspaceId, value.FlowRunId,
                    value.TenantId, value.CreatedBy, cancellationToken) is not null)
                continue;
            var input = JsonSerializer.SerializeToElement(new ConnectedServiceOperationDiscoveryInput
            {
                Contract = ConnectedServiceOperationDiscoveryContracts.V1,
                DiscoveryId = value.Name,
                Service = value.Service,
                ServiceType = value.ServiceType,
                CorrelationId = value.CorrelationId,
                Caller = new(value.CreatedBy, value.TenantId, value.WorkspaceId)
            }, JsonOptions);
            try
            {
                _ = await flowRuns.StartAsync(new(value.FlowRunId, value.Flow,
                    value.Namespace, input, value.CreatedBy.ToString("D"), value.CorrelationId,
                    value.IdempotencyKey, value.TenantId, value.WorkspaceId, value.CreatedBy)
                { LogicalScopeRef = value.LogicalScopeRef }, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failed = value with
                {
                    Generation = checked(value.Generation + 1),
                    State = ConnectedServiceOperationDiscoveryState.Failed,
                    ErrorCode = "connected_service_discovery_recovery_failed",
                    ErrorMessage = exception.Message,
                    CompletedAt = timeProvider.GetUtcNow(),
                    Status = Failed()
                };
                try
                {
                    _ = await store.PutExactAsync(value.ScopeRef!.Value, failed,
                        stored.ETag, false, cancellationToken);
                }
                catch (ResourceConcurrencyException) { }
            }
        }
    }

    public async Task<IReadOnlyList<ConnectedServiceOperationResource>> ListOperationsAsync(
        ResourceScopeRef serviceScope, string serviceName, CancellationToken cancellationToken)
    {
        var service = await services.GetExactAsync(serviceScope, serviceName, cancellationToken)
            ?? throw new ConnectedServiceNotFoundException(serviceName);
        return (await store.ListAllAsync<ConnectedServiceOperationResource>(
                ConnectedServiceResourceKinds.ConnectedServiceOperation, cancellationToken))
            .Where(item => item.Value.Definition.Service.ServiceUid == service.Value.Uid
                && item.Value.Definition.Service.Service.ScopeRef == serviceScope)
            .Select(item => item.Value)
            .OrderBy(item => item.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<StoredResource<ConnectedServiceOperationResource>?> GetOperationAsync(
        ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        store.GetExactAsync<ConnectedServiceOperationResource>(ScopedResourceAddress.Create(scopeRef, @namespace,
            ConnectedServiceResourceKinds.ConnectedServiceOperation, name), cancellationToken);

    private async Task<StoredResource<ConnectedServiceOperationDiscoveryResource>> SynchronizeAsync(
        StoredResource<ConnectedServiceOperationDiscoveryResource> stored,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        if (IsTerminal(stored.Value.State) && stored.Value.CompletedAt is not null) return stored;
        var run = await flowRuns.GetAsync(context.WorkspaceId, stored.Value.FlowRunId,
            context.TenantId, context.PrincipalId, cancellationToken);
        if (run is null || run.State == stored.Value.State && !IsTerminal(run.State)) return stored;
        var state = run.State;
        var errorCode = run.ErrorCode;
        var errorMessage = run.ErrorMessage;
        ConnectedServiceOperationDiscoveryManifest? manifest = stored.Value.Manifest;
        string? manifestDigest = stored.Value.ManifestDigest;
        if (state == ConnectedServiceOperationDiscoveryState.Succeeded)
        {
            try
            {
                manifest = ParseManifest(run.Output);
                manifestDigest = Hash(JsonSerializer.SerializeToElement(manifest, JsonOptions));
                await MaterializeAsync(stored.Value, manifest, manifestDigest, cancellationToken);
            }
            catch (ConnectedServiceOperationDiscoveryException exception)
            {
                state = ConnectedServiceOperationDiscoveryState.Failed;
                errorCode = exception.Code;
                errorMessage = exception.Message;
            }
            catch (Exception exception) when (exception is ConnectedServiceOperationTypeValidationException
                or ConnectedServiceOperationTypeNotFoundException
                or ConnectedServiceOperationTypeRevisionException)
            {
                state = ConnectedServiceOperationDiscoveryState.Failed;
                errorCode = "connected_service_discovery_manifest_invalid";
                errorMessage = exception.Message;
            }
        }
        var updated = stored.Value with
        {
            Generation = checked(stored.Value.Generation + 1),
            State = state,
            Manifest = manifest,
            ManifestDigest = manifestDigest,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            CompletedAt = IsTerminal(state) ? run.CompletedAt ?? timeProvider.GetUtcNow() : null,
            Status = state == ConnectedServiceOperationDiscoveryState.Succeeded
                ? Succeeded() : IsTerminal(state) ? Failed() : Accepted()
        };
        try
        {
            return await InWorkspaceAsync(context, () => store.PutExactAsync(updated.ScopeRef!.Value, updated, stored.ETag, false, cancellationToken));
        }
        catch (ResourceConcurrencyException)
        {
            return await InWorkspaceAsync(context, () => store.GetExactAsync<ConnectedServiceOperationDiscoveryResource>(new(
                updated.ScopeRef!.Value, updated.Namespace, updated.Kind, updated.Name), cancellationToken)) ?? stored;
        }
    }

    private async Task MaterializeAsync(
        ConnectedServiceOperationDiscoveryResource discovery,
        ConnectedServiceOperationDiscoveryManifest manifest,
        string manifestDigest,
        CancellationToken cancellationToken)
    {
        var serviceScope = discovery.Service.Service.ScopeRef;
        var existing = (await store.ListAllAsync<ConnectedServiceOperationResource>(
                ConnectedServiceResourceKinds.ConnectedServiceOperation, cancellationToken))
            .Where(item => item.Value.Definition.Service.ServiceUid == discovery.Service.ServiceUid
                && item.Value.ScopeRef == serviceScope)
            .ToDictionary(item => item.Value.Definition.ExternalId, StringComparer.Ordinal);
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in manifest.Operations)
        {
            discovered.Add(candidate.ExternalId);
            await operationTypes.ResolveExactAsync(candidate.Type, discovery.Namespace, serviceScope, cancellationToken);
            var typeVersion = await ResolveAllowedTypeAsync(discovery, candidate.Type, serviceScope, cancellationToken);
            var issues = ConnectedServiceOptionSchemaValidator.Validate(candidate.Definition,
                typeVersion.DefinitionSchema, "definition");
            if (issues.Count != 0)
                throw Error("connected_service_operation_definition_invalid", string.Join(' ', issues));
            var definitionDigest = Hash(candidate.Definition);
            var provenance = new ConnectedServiceOperationDiscoveryProvenance
            {
                Discovery = new(discovery.ScopeRef!.Value, discovery.Namespace, discovery.Kind, discovery.Name),
                DiscoveryUid = discovery.Uid,
                ManifestDigest = manifestDigest,
                DiscoveredAt = timeProvider.GetUtcNow()
            };
            var operationName = OperationName(discovery.Service.ServiceUid, candidate.ExternalId);
            var resource = new ConnectedServiceOperationResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ConnectedServiceResourceKinds.ConnectedServiceOperation,
                Metadata = new() { Name = operationName, Namespace = discovery.Namespace },
                ScopeRef = serviceScope,
                Generation = existing.TryGetValue(candidate.ExternalId, out var previous)
                    ? checked(previous.Value.Generation + 1) : 1,
                Enabled = previous?.Value.Enabled ?? false,
                Status = Succeeded(),
                Definition = new()
                {
                    DisplayName = candidate.DisplayName,
                    Description = candidate.Description,
                    Service = discovery.Service,
                    Type = candidate.Type,
                    ExternalId = candidate.ExternalId,
                    Definition = candidate.Definition.Clone(),
                    DefinitionDigest = definitionDigest,
                    Provenance = provenance
                },
                ObservedState = new()
                {
                    Availability = ConnectedServiceOperationAvailability.Available,
                    ObservedAt = timeProvider.GetUtcNow()
                }
            };
            await scopeOperations.WriteAsync(resource, serviceScope, AuthorizationPermissions.ResourcesWrite,
                async token => await store.PutExactAsync(serviceScope, resource, previous?.ETag,
                    previous is null, token), cancellationToken);
        }
        foreach (var disappeared in existing.Where(pair => !discovered.Contains(pair.Key)).Select(pair => pair.Value))
        {
            var unavailable = disappeared.Value with
            {
                Generation = checked(disappeared.Value.Generation + 1),
                ObservedState = new()
                {
                    Availability = ConnectedServiceOperationAvailability.Unavailable,
                    ObservedAt = timeProvider.GetUtcNow(),
                    Code = "not_rediscovered",
                    Message = "The operation was not returned by the latest successful discovery."
                }
            };
            await scopeOperations.WriteAsync(unavailable, serviceScope, AuthorizationPermissions.ResourcesWrite,
                async token => await store.PutExactAsync(serviceScope, unavailable,
                    disappeared.ETag, false, token), cancellationToken);
        }
    }

    private async Task<ConnectedServiceOperationTypeVersionDescriptor> ResolveAllowedTypeAsync(
        ConnectedServiceOperationDiscoveryResource discovery,
        ConnectedServiceOperationTypeReference reference,
        ResourceScopeRef serviceScope,
        CancellationToken cancellationToken)
    {
        var (_, serviceTypeVersion) = await ResolveServiceTypeReferenceAsync(
            discovery.ServiceType, discovery.Namespace, cancellationToken);
        var produced = serviceTypeVersion.OperationDiscovery?.ProducedTypes.SingleOrDefault(item =>
            SameReference(item, reference))
            ?? throw Error("connected_service_operation_type_not_allowed",
                $"Operation type '{reference.Type.Name}:{reference.Version}' is not declared by the Connected Service type.");
        var (_, version) = await operationTypes.ResolveExactAsync(produced, discovery.Namespace,
            serviceScope, cancellationToken);
        return version;
    }

    private async Task ValidateDependenciesAsync(
        ConnectedServiceOperationDiscoveryDescriptor descriptor,
        ResourceNamespace ownerNamespace,
        ResourceScopeRef serviceScope,
        CancellationToken cancellationToken)
    {
        foreach (var reference in descriptor.ProducedTypes)
            _ = await operationTypes.ResolveExactAsync(reference, ownerNamespace, serviceScope, cancellationToken);
    }

    private async Task<(ConnectedServiceTypeResource Type, ConnectedServiceTypeVersionDescriptor Version)>
        ResolveServiceTypeAsync(ConnectedServiceResource service, CancellationToken cancellationToken) =>
        await ResolveServiceTypeReferenceAsync(service.Definition.Type, service.Namespace, cancellationToken);

    private async Task<(ConnectedServiceTypeResource Type, ConnectedServiceTypeVersionDescriptor Version)>
        ResolveServiceTypeReferenceAsync(ConnectedServiceTypeReference reference, ResourceNamespace ownerNamespace,
            CancellationToken cancellationToken)
    {
        var scope = reference.Type.ScopeRef
            ?? throw Error("connected_service_type_scope_required", "The Connected Service type reference is not exact.");
        var address = reference.Type.Resolve(ownerNamespace, ConnectedServiceResourceKinds.ConnectedServiceType);
        var stored = await store.GetExactAsync<ConnectedServiceTypeResource>(
            new(scope, address.Namespace, address.Kind, address.Name), cancellationToken)
            ?? throw new ConnectedServiceTypeNotFoundException(address.Namespace, address.Name);
        if (stored.Value.Uid != reference.TypeUid)
            throw Error("connected_service_type_revision_changed", "The Connected Service type UID no longer matches.");
        var version = stored.Value.Definition.Versions.SingleOrDefault(item => item.Version == reference.Version)
            ?? throw Error("connected_service_type_version_missing", $"Version '{reference.Version}' no longer exists.");
        if (!string.Equals(version.SchemaDigest, reference.SchemaDigest, StringComparison.Ordinal))
            throw Error("connected_service_type_revision_changed", "The Connected Service type schema digest no longer matches.");
        return (stored.Value, version);
    }

    private static ConnectedServiceOperationDiscoveryManifest ParseManifest(JsonElement? output)
    {
        if (output is not { ValueKind: JsonValueKind.Object })
            throw Error("connected_service_discovery_manifest_invalid", "The discovery Flow must return a JSON object.");
        if (Encoding.UTF8.GetByteCount(output.Value.GetRawText()) > MaximumManifestBytes)
            throw Error("connected_service_discovery_manifest_too_large",
                $"The discovery manifest cannot exceed {MaximumManifestBytes} bytes.");
        ConnectedServiceOperationDiscoveryManifest manifest;
        try
        {
            manifest = output.Value.Deserialize<ConnectedServiceOperationDiscoveryManifest>(JsonOptions)
                ?? throw new JsonException("The manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw Error("connected_service_discovery_manifest_invalid", exception.Message);
        }
        if (!string.Equals(manifest.Contract, ConnectedServiceOperationDiscoveryContracts.V1, StringComparison.Ordinal))
            throw Error("connected_service_discovery_contract_invalid",
                $"The manifest contract must be '{ConnectedServiceOperationDiscoveryContracts.V1}'.");
        if (manifest.Operations is null || manifest.Operations.Count > MaximumOperations)
            throw Error("connected_service_discovery_manifest_too_large",
                $"A discovery can return at most {MaximumOperations} operations.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in manifest.Operations)
        {
            if (string.IsNullOrWhiteSpace(operation.ExternalId) || operation.ExternalId.Length > 512
                || string.IsNullOrWhiteSpace(operation.DisplayName) || operation.DisplayName.Length > 200
                || operation.Description?.Length > 2_000 || !ids.Add(operation.ExternalId))
                throw Error("connected_service_discovery_manifest_invalid",
                    "Operations require unique bounded external identifiers and display names.");
            if (operation.Definition.ValueKind != JsonValueKind.Object
                || Encoding.UTF8.GetByteCount(operation.Definition.GetRawText()) > MaximumDefinitionBytes)
                throw Error("connected_service_discovery_manifest_invalid",
                    $"Operation '{operation.ExternalId}' requires a bounded JSON object definition.");
        }
        return manifest with
        {
            Operations = manifest.Operations.Select(operation => operation with
            {
                Definition = operation.Definition.Clone()
            }).ToArray()
        };
    }

    private static void EnsureDiscoverable(ConnectedServiceResource service)
    {
        if (!service.Definition.Enabled || service.ObservedState?.Availability != ConnectedServiceAvailability.Ready)
            throw Error("connected_service_unavailable", $"Connected Service '{service.Address}' is not ready.");
    }

    private RequestContext RequireContext()
    {
        if (requestContext.AccessMode is not (ControlPlaneAccessMode.Workspace
                or ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration)
            || requestContext.Current.WorkspaceId == Guid.Empty || requestContext.Current.TenantId == Guid.Empty)
            throw Error("connected_service_discovery_context_required",
                "Operation discovery requires a governed Workspace context.");
        return requestContext.Current;
    }

    private static bool SameReference(ConnectedServiceOperationTypeReference left,
        ConnectedServiceOperationTypeReference right) => left.Type.ScopeRef == right.Type.ScopeRef
        && left.Type.Namespace == right.Type.Namespace && left.Type.Name == right.Type.Name
        && left.TypeUid == right.TypeUid && left.Version == right.Version
        && left.SchemaDigest == right.SchemaDigest;
    private static bool IsTerminal(ConnectedServiceOperationDiscoveryState state) => state is
        ConnectedServiceOperationDiscoveryState.Succeeded or ConnectedServiceOperationDiscoveryState.Failed
        or ConnectedServiceOperationDiscoveryState.Cancelled or ConnectedServiceOperationDiscoveryState.TimedOut;
    private static string OperationName(Guid serviceUid, string externalId) =>
        $"operation-{HashText($"{serviceUid:D}:{externalId}")[..32]}";
    private static string Hash(JsonElement value) => HashText(value.GetRawText());
    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static ConnectedServiceOperationDiscoveryException Error(string code, string message) => new(code, message);
    private static ResourceStatus Accepted() => new() { ProvisioningState = ProvisioningState.Accepted };
    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
    private static ResourceStatus Failed() => new() { ProvisioningState = ProvisioningState.Failed };
}
