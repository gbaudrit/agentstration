using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
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
}

public sealed class KnowledgeSourceManagementService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    IKnowledgeFlowResolver flows,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    public const int MaximumDisplayNameLength = 200;
    public const int MaximumDescriptionLength = 2_000;
    public const int MaximumAcquisitionConfigurationBytes = 64 * 1024;

    public Task<IReadOnlyList<StoredResource<KnowledgeSourceResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<KnowledgeSourceResource>(KnowledgeResourceKinds.KnowledgeSource, cancellationToken);

    public Task<StoredResource<KnowledgeSourceResource>?> GetAsync(
        KnowledgeSourceId id,
        CancellationToken cancellationToken) =>
        store.GetAsync<KnowledgeSourceResource>(Key(id), cancellationToken);

    public Task<StoredResource<KnowledgeSourceResource>?> GetExactAsync(
        ResourceScopeRef scopeRef,
        KnowledgeSourceId id,
        CancellationToken cancellationToken) =>
        store.GetExactAsync<KnowledgeSourceResource>(Scoped(scopeRef, id), cancellationToken);

    public async Task ValidateForCreateAsync(KnowledgeSourceResource resource, CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(KnowledgeResourceKinds.KnowledgeSource);
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        await ValidateAsync(resource with { ScopeRef = scopeRef }, cancellationToken);
    }

    public async Task<StoredResource<KnowledgeSourceResource>> CreateAsync(
        KnowledgeSourceResource resource,
        CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(KnowledgeResourceKinds.KnowledgeSource);
        var scoped = resource with { ScopeRef = scopeRef };
        var created = await scopeOperations.WriteAsync(scoped, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var readiness = await ValidateAsync(scoped, token);
            return await store.PutExactAsync(scopeRef, scoped with
            {
                Generation = 1,
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
        return await EvaluateReadinessAsync(existing.Value, rejectInvalidBindings: false, cancellationToken);
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
            var readiness = await ValidateAsync(changed, token);
            return await store.PutExactAsync(scopeRef, changed with
            {
                Generation = checked(existing.Value.Generation + 1),
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
        ResourceScopeRef scopeRef,
        Guid sourceUid,
        CancellationToken cancellationToken)
    {
        const int pageSize = 1000;
        for (var skip = 0; ; skip += pageSize)
        {
            var page = await store.ListExactAsync<KnowledgeSnapshotResource>(scopeRef,
                KnowledgeResourceKinds.KnowledgeSnapshot, skip, pageSize, cancellationToken);
            if (page.Any(value => value.Value.KnowledgeSourceUid == sourceUid)) return true;
            if (page.Count < pageSize) return false;
        }
    }

    private async Task<KnowledgeSourceReadiness> ValidateAsync(
        KnowledgeSourceResource resource,
        CancellationToken cancellationToken)
    {
        ValidateStructure(resource);
        return await EvaluateReadinessAsync(resource, rejectInvalidBindings: true, cancellationToken);
    }

    public static void ValidateStructure(KnowledgeSourceResource resource)
    {
        if (resource.Kind != KnowledgeResourceKinds.KnowledgeSource || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw new KnowledgeSourceValidationException("knowledge_source_identity_invalid", "Invalid KnowledgeSource resource envelope.");
        if (resource.ScopeRef is not { Kind: ResourceScopeKind.Workspace })
            throw new KnowledgeSourceValidationException("knowledge_source_scope_invalid", "A KnowledgeSource must belong to a Workspace scope.");
        ValidateName(resource.Name);
        if (string.IsNullOrWhiteSpace(resource.Definition?.DisplayName)
            || resource.Definition.DisplayName.Length > MaximumDisplayNameLength)
            throw new KnowledgeSourceValidationException("knowledge_source_display_name_invalid",
                $"KnowledgeSource display names must contain 1 to {MaximumDisplayNameLength} characters.");
        if (resource.Definition.Description?.Length > MaximumDescriptionLength)
            throw new KnowledgeSourceValidationException("knowledge_source_description_too_long",
                $"KnowledgeSource descriptions cannot exceed {MaximumDescriptionLength} characters.");
        if (resource.Definition.AcquisitionConfiguration.ValueKind != JsonValueKind.Object)
            throw new KnowledgeSourceValidationException("knowledge_source_acquisition_configuration_invalid",
                "KnowledgeSource acquisitionConfiguration must be a JSON object.");
        if (Encoding.UTF8.GetByteCount(resource.Definition.AcquisitionConfiguration.GetRawText())
            > MaximumAcquisitionConfigurationBytes)
            throw new KnowledgeSourceValidationException("knowledge_source_acquisition_configuration_too_large",
                $"KnowledgeSource acquisitionConfiguration cannot exceed {MaximumAcquisitionConfigurationBytes} bytes.");
        ValidateTarget(resource.Definition.IngestionFlow, "ingestion");
        ValidateTarget(resource.Definition.RetrievalFlow, "retrieval");
        if (resource.Definition.Enabled
            && (resource.Definition.IngestionFlow is null || resource.Definition.RetrievalFlow is null))
            throw new KnowledgeSourceValidationException("knowledge_source_flow_bindings_required",
                "An enabled KnowledgeSource requires both ingestionFlow and retrievalFlow bindings.");
    }

    private async Task<KnowledgeSourceReadiness> EvaluateReadinessAsync(
        KnowledgeSourceResource resource,
        bool rejectInvalidBindings,
        CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        ResolvedKnowledgeFlowBinding? ingestion = null;
        ResolvedKnowledgeFlowBinding? retrieval = null;
        if (resource.Definition.IngestionFlow is null) issues.Add("Ingestion Flow is not configured.");
        else ingestion = await ResolveAsync(resource, resource.Definition.IngestionFlow, "ingestion", issues, rejectInvalidBindings, cancellationToken);
        if (resource.Definition.RetrievalFlow is null) issues.Add("Retrieval Flow is not configured.");
        else retrieval = await ResolveAsync(resource, resource.Definition.RetrievalFlow, "retrieval", issues, rejectInvalidBindings, cancellationToken);
        if (!resource.Definition.Enabled) issues.Add("KnowledgeSource is disabled.");
        return new(resource.Definition.Enabled && ingestion is not null && retrieval is not null,
            resource.Definition.Enabled, ingestion, retrieval, issues);
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
                Message = readiness.Ready ? "The ingestion and retrieval Flow bindings are ready." : string.Join(' ', readiness.Issues),
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
        audit.WriteAsync(new(action, WorkspaceId: scopeRef.TargetId), cancellationToken);
}
