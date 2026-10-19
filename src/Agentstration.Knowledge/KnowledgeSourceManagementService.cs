using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.Knowledge;

public sealed class KnowledgeSourceValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class KnowledgeSourceNotFoundException(KnowledgeSourceId id)
    : Exception($"KnowledgeSource '{id}' was not found.");

public interface IKnowledgeFlowResolver
{
    Task<ResolvedKnowledgeFlowBinding> ResolveAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace ownerNamespace,
        KnowledgeFlowTarget target,
        CancellationToken cancellationToken);

    Task ValidatePinnedAsync(
        ResolvedKnowledgeFlowBinding binding,
        CancellationToken cancellationToken);
}

public sealed class KnowledgeSourceManagementService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    IKnowledgeFlowResolver flows,
    IResourceAccessPolicyService accessPolicies,
    IRequestContextScopeFactory requestContexts,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider,
    IKnowledgeProjectionInputResolver? projectionInputs = null)
{
    public const int MaximumDisplayNameLength = 200;
    public const int MaximumDescriptionLength = 2_000;

    public Task<IReadOnlyList<StoredResource<KnowledgeSourceResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<KnowledgeSourceResource>(KnowledgeResourceKinds.KnowledgeSource, cancellationToken);

    public Task<StoredResource<KnowledgeSourceResource>?> GetAsync(
        KnowledgeSourceId id,
        CancellationToken cancellationToken) => id.ScopeRef is { } exact
            ? GetExactAsync(exact, id, cancellationToken)
            : store.GetAsync<KnowledgeSourceResource>(Key(id), cancellationToken);

    public async Task<StoredResource<KnowledgeSourceResource>> ResolveForUseAsync(
        KnowledgeSourceId id,
        ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken)
    {
        var source = await GetAsync(id, cancellationToken) ?? throw new KnowledgeSourceNotFoundException(id);
        var ownerScopeRef = RequireScope(source.Value);
        if (ownerScopeRef != consumerScopeRef)
        {
            var decision = await accessPolicies.EvaluateAsync(new ResourceAccessIdentity(source.Value.Uid,
                ownerScopeRef, source.Value.Namespace, KnowledgeResourceKinds.KnowledgeSource,
                source.Value.Generation), consumerScopeRef, ResourceAccessAction.Use, cancellationToken);
            if (!decision.Allowed)
                throw new KnowledgeSourceValidationException("knowledge_source_access_denied",
                    $"KnowledgeSource '{source.Value.Address}' does not authorize use from '{consumerScopeRef}' ({decision.ReasonCode}).");
        }
        return source;
    }

    public Task<StoredResource<KnowledgeSourceResource>?> GetExactAsync(
        ResourceScopeRef scopeRef,
        KnowledgeSourceId id,
        CancellationToken cancellationToken) =>
        store.GetExactAsync<KnowledgeSourceResource>(Scoped(scopeRef, id), cancellationToken);

    public async Task ValidateForCreateAsync(KnowledgeSourceResource resource, CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(KnowledgeResourceKinds.KnowledgeSource);
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        _ = await ValidateAsync(resource with { ScopeRef = scopeRef }, cancellationToken);
    }

    public async Task<StoredResource<KnowledgeSourceResource>> CreateAsync(
        KnowledgeSourceResource resource,
        CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(KnowledgeResourceKinds.KnowledgeSource);
        var scoped = resource with { ScopeRef = scopeRef };
        var created = await scopeOperations.WriteAsync(scoped, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var (readiness, composition) = await ValidateAsync(scoped, token);
            return await store.PutExactAsync(scopeRef, scoped with
            {
                Generation = 1,
                Composition = composition,
                Status = Status(readiness)
            }, null, true, token);
        }, cancellationToken);
        await AuditAsync(SecurityAuditActions.KnowledgeSourceCreated, scopeRef, cancellationToken);
        return created;
    }

    public async Task<StoredResource<KnowledgeSourceResource>> PutAsync(
        KnowledgeSourceId id,
        KnowledgeSourceProperties definition,
        string? etag,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(id, cancellationToken) ?? throw new KnowledgeSourceNotFoundException(id);
        return await PutAsync(existing, definition, etag, SecurityAuditActions.KnowledgeSourceUpdated, cancellationToken);
    }

    public async Task<StoredResource<KnowledgeSourceResource>> PutExactAsync(
        ResourceScopeRef scopeRef,
        KnowledgeSourceId id,
        KnowledgeSourceProperties definition,
        string? etag,
        CancellationToken cancellationToken)
    {
        var existing = await GetExactAsync(scopeRef, id, cancellationToken) ?? throw new KnowledgeSourceNotFoundException(id);
        return await PutAsync(existing, definition, etag, SecurityAuditActions.KnowledgeSourceUpdated, cancellationToken);
    }

    public async Task<StoredResource<KnowledgeSourceResource>> SetEnabledAsync(
        KnowledgeSourceId id,
        bool enabled,
        string? etag,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(id, cancellationToken) ?? throw new KnowledgeSourceNotFoundException(id);
        return await PutAsync(existing, existing.Value.Definition with { Enabled = enabled }, etag,
            enabled ? SecurityAuditActions.KnowledgeSourceEnabled : SecurityAuditActions.KnowledgeSourceDisabled,
            cancellationToken);
    }

    public async Task<KnowledgeSourceReadiness> GetReadinessAsync(
        KnowledgeSourceId id,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(id, cancellationToken) ?? throw new KnowledgeSourceNotFoundException(id);
        var ownerScope = RequireScope(existing.Value);
        return await EvaluateReadinessAsync(existing.Value, ownerScope,
            includeOperationalState: ownerScope.Kind == ResourceScopeKind.Workspace,
            rejectInvalidBindings: false, cancellationToken);
    }

    public async Task<KnowledgeSourceReadiness> GetReadinessForUseAsync(
        KnowledgeSourceId id,
        ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken)
    {
        var existing = await ResolveForUseAsync(id, consumerScopeRef, cancellationToken);
        return await EvaluateReadinessAsync(existing.Value, consumerScopeRef,
            includeOperationalState: true, rejectInvalidBindings: false, cancellationToken);
    }

    public async Task DeleteAsync(KnowledgeSourceId id, string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(id, cancellationToken) ?? throw new KnowledgeSourceNotFoundException(id);
        await DeleteAsync(existing, etag, cancellationToken);
    }

    public async Task DeleteExactAsync(
        ResourceScopeRef scopeRef,
        KnowledgeSourceId id,
        string? etag,
        CancellationToken cancellationToken)
    {
        var existing = await GetExactAsync(scopeRef, id, cancellationToken) ?? throw new KnowledgeSourceNotFoundException(id);
        await DeleteAsync(existing, etag, cancellationToken);
    }

    private async Task<StoredResource<KnowledgeSourceResource>> PutAsync(
        StoredResource<KnowledgeSourceResource> existing,
        KnowledgeSourceProperties definition,
        string? etag,
        string auditAction,
        CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(existing.Value);
        var changed = existing.Value with { Definition = definition };
        var updated = await scopeOperations.WriteAsync(changed, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var (readiness, composition) = await ValidateAsync(changed, token);
            return await store.PutExactAsync(scopeRef, changed with
            {
                Generation = checked(existing.Value.Generation + 1),
                Composition = composition,
                Status = Status(readiness)
            }, etag, false, token);
        }, cancellationToken);
        await AuditAsync(auditAction, scopeRef, cancellationToken);
        return updated;
    }

    private async Task DeleteAsync(
        StoredResource<KnowledgeSourceResource> existing,
        string? etag,
        CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(existing.Value);
        await scopeOperations.WriteAsync(KnowledgeResourceKinds.KnowledgeSource, scopeRef,
            AuthorizationPermissions.ResourcesDelete, async token =>
            {
                if (await HasSnapshotAsync(scopeRef, existing.Value.Uid, token))
                    throw new KnowledgeSourceValidationException("knowledge_source_in_use_by_snapshot",
                        $"KnowledgeSource '{existing.Value.Address}' is retained by an immutable Knowledge Snapshot.");
                await store.DeleteExactAsync(Scoped(scopeRef, new(existing.Value.Name, existing.Value.Namespace)), etag, token);
                return true;
            }, cancellationToken);
        await AuditAsync(SecurityAuditActions.KnowledgeSourceDeleted, scopeRef, cancellationToken);
    }

    private async Task<bool> HasSnapshotAsync(
        ResourceScopeRef sourceScopeRef,
        Guid sourceUid,
        CancellationToken cancellationToken)
    {
        using var system = requestContexts.PushSystem();
        return (await store.ListAllAsync<KnowledgeSnapshotResource>(
            KnowledgeResourceKinds.KnowledgeSnapshot, cancellationToken)).Any(value =>
                value.Value.KnowledgeSourceUid == sourceUid
                && value.Value.KnowledgeSourceScopeRef == sourceScopeRef);
    }

    private async Task<(KnowledgeSourceReadiness Readiness, ResolvedKnowledgeSourceComposition Composition)> ValidateAsync(
        KnowledgeSourceResource resource,
        CancellationToken cancellationToken)
    {
        ValidateStructure(resource);
        var composition = await ResolveCompositionAsync(resource, cancellationToken);
        var readiness = await EvaluateReadinessAsync(resource with { Composition = composition }, RequireScope(resource),
            includeOperationalState: false, rejectInvalidBindings: true, cancellationToken);
        return (readiness, composition);
    }

    public static void ValidateStructure(KnowledgeSourceResource resource)
    {
        if (resource.Kind != KnowledgeResourceKinds.KnowledgeSource || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw new KnowledgeSourceValidationException("knowledge_source_identity_invalid", "Invalid KnowledgeSource resource envelope.");
        if (resource.ScopeRef is not { } scopeRef)
            throw new KnowledgeSourceValidationException("knowledge_source_scope_invalid", "A KnowledgeSource requires an ownership scope.");
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        ValidateName(resource.Name);
        if (string.IsNullOrWhiteSpace(resource.Definition?.DisplayName)
            || resource.Definition.DisplayName.Length > MaximumDisplayNameLength)
            throw new KnowledgeSourceValidationException("knowledge_source_display_name_invalid",
                $"KnowledgeSource display names must contain 1 to {MaximumDisplayNameLength} characters.");
        if (resource.Definition.Description?.Length > MaximumDescriptionLength)
            throw new KnowledgeSourceValidationException("knowledge_source_description_too_long",
                $"KnowledgeSource descriptions cannot exceed {MaximumDescriptionLength} characters.");
        ValidateTarget(resource.Definition.ProjectionFlow, "projection");
        ValidateTarget(resource.Definition.RetrievalFlow, "retrieval");
        if (resource.Definition.DataSources.Count is < 1 or > KnowledgeProjectionService.MaximumInputs)
            throw new KnowledgeSourceValidationException("knowledge_source_data_sources_invalid",
                $"A KnowledgeSource requires 1 to {KnowledgeProjectionService.MaximumInputs} Data Source bindings.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in resource.Definition.DataSources)
        {
            if (string.IsNullOrWhiteSpace(binding.Name) || !names.Add(binding.Name))
                throw new KnowledgeSourceValidationException("knowledge_source_data_source_binding_invalid",
                    "Data Source binding names must be non-empty and unique.");
            if (string.IsNullOrWhiteSpace(binding.DataSource.Name))
                throw new KnowledgeSourceValidationException("knowledge_source_data_source_binding_invalid",
                    $"Binding '{binding.Name}' requires a Data Source reference.");
            if (binding.AcquisitionTarget is { } target && string.IsNullOrWhiteSpace(target.Name))
                throw new KnowledgeSourceValidationException("knowledge_source_acquisition_target_invalid",
                    $"Binding '{binding.Name}' has an invalid acquisition target reference.");
            if (binding.Configuration.ValueKind != JsonValueKind.Object)
                throw new KnowledgeSourceValidationException("knowledge_source_transformation_configuration_invalid",
                    $"Binding '{binding.Name}' transformation configuration must be a JSON object.");
            if (binding.MaximumAge is { } maximumAge && maximumAge <= TimeSpan.Zero)
                throw new KnowledgeSourceValidationException("knowledge_source_data_source_maximum_age_invalid",
                    $"Binding '{binding.Name}' maximum age must be greater than zero.");
            ValidateTarget(binding.TransformationFlow, "transformation");
        }
        if (resource.Definition.Enabled
            && (resource.Definition.ProjectionFlow is null || resource.Definition.RetrievalFlow is null))
            throw new KnowledgeSourceValidationException("knowledge_source_projection_bindings_required",
                "An enabled KnowledgeSource requires projection and retrieval Flow bindings.");
    }

    private async Task<KnowledgeSourceReadiness> EvaluateReadinessAsync(
        KnowledgeSourceResource resource,
        ResourceScopeRef executionScope,
        bool includeOperationalState,
        bool rejectInvalidBindings,
        CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        var composition = resource.Composition;
        ResolvedKnowledgeFlowBinding? retrieval = composition?.RetrievalFlow;
        ResolvedKnowledgeFlowBinding? projection = composition?.ProjectionFlow;
        var dataSources = new List<KnowledgeDataSourceBindingReadiness>();
        if (composition is null)
        {
            AddOrThrow("knowledge_source_composition_missing",
                "The KnowledgeSource does not retain an exact dependency composition and must be recreated.");
        }
        else
        {
            try
            {
                await flows.ValidatePinnedAsync(composition.ProjectionFlow, cancellationToken);
                await flows.ValidatePinnedAsync(composition.RetrievalFlow, cancellationToken);
                foreach (var transformation in composition.TransformationFlows.Values)
                    await flows.ValidatePinnedAsync(transformation, cancellationToken);
            }
            catch (KnowledgeSourceValidationException exception)
            {
                AddOrThrow(exception.Code, exception.Message);
                projection = null;
                retrieval = null;
            }
        }
        foreach (var binding in resource.Definition.DataSources)
        {
            KnowledgeDataSourceBindingReadiness readiness;
            var dependency = composition?.DataSources.SingleOrDefault(value =>
                string.Equals(value.BindingName, binding.Name, StringComparison.Ordinal));
            if (dependency is null)
                readiness = new(binding.Name, null, null, false,
                    $"Binding '{binding.Name}' has no exact Data Source dependency pin.", null);
            else
            {
                try
                {
                    readiness = projectionInputs is null
                        ? new(binding.Name, dependency.DataSourceScopeRef, dependency.DataSourceUid, true, null, null)
                        : includeOperationalState
                            ? await projectionInputs.GetReadinessAsync(executionScope, resource.Namespace,
                                binding, dependency, cancellationToken)
                            : await ValidateDependencyAsync(resource, binding, dependency, cancellationToken);
                    if (composition!.TransformationFlows.TryGetValue(binding.Name, out var transformation))
                        readiness = readiness with { Transformation = transformation };
                }
                catch (Exception exception) when (!rejectInvalidBindings
                    && exception is KnowledgeSourceValidationException or KnowledgeProjectionException
                        or ResourceReferenceOutsideScopeException or ResourceReferenceAmbiguousException)
                {
                    readiness = new(binding.Name, dependency.DataSourceScopeRef, dependency.DataSourceUid,
                        false, exception.Message, null);
                }
            }
            dataSources.Add(readiness);
            if (!readiness.Ready && binding.Required)
            {
                if (rejectInvalidBindings)
                    throw new KnowledgeSourceValidationException("knowledge_source_dependency_stale",
                        readiness.Issue ?? $"Binding '{binding.Name}' is not ready.");
                issues.Add(readiness.Issue ?? $"Binding '{binding.Name}' is not ready.");
            }
        }
        if (!resource.Definition.Enabled) issues.Add("KnowledgeSource is disabled.");
        return new(resource.Definition.Enabled && projection is not null && retrieval is not null
                && dataSources.Where((_, index) => resource.Definition.DataSources[index].Required).All(value => value.Ready),
            resource.Definition.Enabled, retrieval, issues, projection, dataSources);

        void AddOrThrow(string code, string message)
        {
            if (rejectInvalidBindings) throw new KnowledgeSourceValidationException(code, message);
            issues.Add(message);
            projection = null;
        }
    }

    private async Task<ResolvedKnowledgeSourceComposition> ResolveCompositionAsync(
        KnowledgeSourceResource resource,
        CancellationToken cancellationToken)
    {
        var ownerScope = RequireScope(resource);
        var projectionTarget = resource.Definition.ProjectionFlow
            ?? throw new KnowledgeSourceValidationException("knowledge_source_projection_bindings_required",
                "A KnowledgeSource requires a projection Flow binding.");
        var retrievalTarget = resource.Definition.RetrievalFlow
            ?? throw new KnowledgeSourceValidationException("knowledge_source_projection_bindings_required",
                "A KnowledgeSource requires a retrieval Flow binding.");
        var projection = await flows.ResolveAsync(ownerScope, resource.Namespace, projectionTarget, cancellationToken);
        if (!string.Equals(projection.Contract, KnowledgeFlowContracts.Projection, StringComparison.Ordinal))
            throw new KnowledgeSourceValidationException("knowledge_source_projection_contract_invalid",
                $"Projection Flow must declare flow.contract '{KnowledgeFlowContracts.Projection}'.");
        var retrieval = await flows.ResolveAsync(ownerScope, resource.Namespace, retrievalTarget, cancellationToken);
        var dependencies = new List<ResolvedKnowledgeDataSourceBinding>(resource.Definition.DataSources.Count);
        var transformations = new Dictionary<string, ResolvedKnowledgeFlowBinding>(StringComparer.Ordinal);
        foreach (var binding in resource.Definition.DataSources)
        {
            if (projectionInputs is not null)
                dependencies.Add(await projectionInputs.ResolveDependencyAsync(ownerScope, resource.Namespace,
                    binding, cancellationToken));
            if (binding.TransformationFlow is null) continue;
            var transformation = await flows.ResolveAsync(ownerScope, resource.Namespace,
                binding.TransformationFlow, cancellationToken);
            if (!string.Equals(transformation.Contract, KnowledgeFlowContracts.ArtifactTransformation, StringComparison.Ordinal))
                throw new KnowledgeSourceValidationException("knowledge_source_transformation_contract_invalid",
                    $"Transformation Flow for binding '{binding.Name}' must declare flow.contract '{KnowledgeFlowContracts.ArtifactTransformation}'.");
            transformations.Add(binding.Name, transformation);
        }
        var evidence = JsonSerializer.SerializeToElement(new
        {
            projection = FlowEvidence(projection),
            retrieval = FlowEvidence(retrieval),
            dataSources = dependencies.OrderBy(value => value.BindingName, StringComparer.Ordinal),
            transformations = transformations.OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => new { value.Key, Flow = FlowEvidence(value.Value) })
        });
        return new()
        {
            ProjectionFlow = projection,
            RetrievalFlow = retrieval,
            DataSources = dependencies,
            TransformationFlows = transformations,
            DependencyHash = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(evidence.GetRawText())))
        };
    }

    private static object FlowEvidence(ResolvedKnowledgeFlowBinding flow) => new
    {
        flow.ScopeRef,
        flow.Namespace,
        flow.Name,
        flow.Version,
        flow.DefinitionHash
    };

    internal async Task<ResolvedKnowledgeFlowBinding> ResolveRetrievalAsync(
        KnowledgeSourceResource resource,
        CancellationToken cancellationToken)
    {
        var readiness = await EvaluateReadinessAsync(resource, RequireScope(resource),
            includeOperationalState: false, rejectInvalidBindings: true, cancellationToken);
        if (!readiness.Ready || readiness.Retrieval is null)
            throw new KnowledgeSourceValidationException("knowledge_source_not_ready", string.Join(' ', readiness.Issues));
        return readiness.Retrieval;
    }

    private async Task<KnowledgeDataSourceBindingReadiness> ValidateDependencyAsync(
        KnowledgeSourceResource resource,
        KnowledgeDataSourceBinding binding,
        ResolvedKnowledgeDataSourceBinding dependency,
        CancellationToken cancellationToken)
    {
        var current = await projectionInputs!.ResolveDependencyAsync(RequireScope(resource),
            resource.Namespace, binding, cancellationToken);
        if (current != dependency)
            throw new KnowledgeSourceValidationException("knowledge_source_dependency_stale",
                $"Pinned Data Source dependency for binding '{binding.Name}' is stale.");
        return new(binding.Name, dependency.DataSourceScopeRef, dependency.DataSourceUid, true, null, null);
    }

    private async Task<ResolvedKnowledgeFlowBinding?> ResolveAsync(
        KnowledgeSourceResource resource,
        KnowledgeFlowTarget target,
        string role,
        List<string> issues,
        bool rejectInvalidBindings,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await flows.ResolveAsync(RequireScope(resource), resource.Namespace, target, cancellationToken);
            ValidateSchema(resolved.InputSchema, role, "input");
            ValidateSchema(resolved.OutputSchema, role, "output");
            return resolved;
        }
        catch (Exception exception) when (!rejectInvalidBindings && exception is KnowledgeSourceValidationException)
        {
            issues.Add(exception.Message);
            return null;
        }
    }

    private static void ValidateSchema(JsonElement? schema, string role, string direction)
    {
        if (schema is not null && schema.Value.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
            throw new KnowledgeSourceValidationException($"knowledge_source_{role}_flow_schema_invalid",
                $"The {role} Flow {direction} schema must be a JSON object when specified.");
    }

    private static void ValidateTarget(KnowledgeFlowTarget? target, string role)
    {
        if (target is null) return;
        if (string.IsNullOrWhiteSpace(target.Name))
            throw new KnowledgeSourceValidationException($"knowledge_source_{role}_flow_required", $"The {role} Flow name is required.");
        if ((target.UseActiveVersion && !string.IsNullOrWhiteSpace(target.Version))
            || (!target.UseActiveVersion && string.IsNullOrWhiteSpace(target.Version)))
            throw new KnowledgeSourceValidationException($"knowledge_source_{role}_flow_reference_invalid",
                $"Select either the active {role} Flow version or one exact version.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !char.IsLetterOrDigit(name[0])
            || name.Any(character => !char.IsLower(character) && !char.IsDigit(character) && character is not '-' and not '.'))
            throw new KnowledgeSourceValidationException("knowledge_source_name_invalid",
                "KnowledgeSource names must contain 1 to 128 lowercase letters, digits, '-' or '.', and start with a letter or digit.");
    }

    private ResourceStatus Status(KnowledgeSourceReadiness readiness) => new()
    {
        ProvisioningState = ProvisioningState.Succeeded,
        Conditions =
        [
            new ResourceCondition
            {
                Type = "Ready",
                Status = readiness.Ready ? "True" : "False",
                Reason = readiness.Ready ? "FlowBindingsResolved" : readiness.Enabled ? "FlowBindingsIncomplete" : "Disabled",
                Message = readiness.Ready ? "The projection and retrieval Flow bindings are ready." : string.Join(' ', readiness.Issues),
                LastTransitionTime = timeProvider.GetUtcNow()
            }
        ]
    };

    private static ResourceScopeRef RequireScope(Resource resource) => resource.ScopeRef
        ?? throw new KnowledgeSourceValidationException("knowledge_source_scope_invalid", "A KnowledgeSource requires an ownership scope.");
    private static ResourceKey Key(KnowledgeSourceId id) => new(KnowledgeResourceKinds.KnowledgeSource, id.Value, id.Namespace);
    private static ScopedResourceAddress Scoped(ResourceScopeRef scopeRef, KnowledgeSourceId id) =>
        ScopedResourceAddress.Create(scopeRef, id.Namespace, KnowledgeResourceKinds.KnowledgeSource, id.Value);

    private Task AuditAsync(string action, ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
        audit.WriteAsync(new(action,
            TenantId: scopeRef.Kind == ResourceScopeKind.Tenant ? scopeRef.TargetId : null,
            WorkspaceId: scopeRef.Kind == ResourceScopeKind.Workspace ? scopeRef.TargetId : null), cancellationToken);
}
