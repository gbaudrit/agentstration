using Agentstration.Artifacts.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.Artifacts;

public sealed class ArtifactManagementService(
    IResourceStore store,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    IArtifactStagingToolExecutor staging,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    public const int MaximumChunkBytes = 256 * 1024;

    public async Task<IReadOnlyList<StoredResource<ArtifactStagingBindingResource>>> ListBindingsAsync(CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsInspect, cancellationToken);
        return (await store.ListAllAsync<ArtifactStagingBindingResource>(ArtifactResourceKinds.ArtifactStagingBinding, cancellationToken))
            .Where(value => value.Value.ScopeRef == ResourceScopeRef.Workspace(current.WorkspaceId))
            .OrderByDescending(value => value.Value.Definition.IsDefault)
            .ThenBy(value => value.Value.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<StoredResource<ArtifactStagingBindingResource>?> GetBindingAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsInspect, cancellationToken);
        return await store.GetExactAsync<ArtifactStagingBindingResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Workspace(current.WorkspaceId), @namespace,
            ArtifactResourceKinds.ArtifactStagingBinding, name), cancellationToken);
    }

    public async Task<StoredResource<ArtifactStagingBindingResource>> CreateBindingAsync(
        ResourceNamespace @namespace,
        string name,
        ArtifactStagingBindingProperties properties,
        CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsManageRetention, cancellationToken);
        ValidateBinding(name, properties);
        var scope = ResourceScopeRef.Workspace(current.WorkspaceId);
        if (properties.IsDefault && (await ListBindingsSystemAsync(scope, cancellationToken)).Any(value => value.Value.Definition.IsDefault))
            throw Error("artifact_staging_default_exists", "Only one default ArtifactStagingBinding is allowed per Workspace.");
        var resource = new ArtifactStagingBindingResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ArtifactResourceKinds.ArtifactStagingBinding,
            Metadata = new ResourceMetadata { Name = name, Namespace = @namespace },
            ScopeRef = scope,
            Generation = 1,
            Status = Succeeded(),
            Definition = properties
        };
        await staging.ValidateAsync(resource, Command(current, $"artifact-binding:{name}"), cancellationToken);
        var stored = await store.PutExactAsync(scope, resource, null, true, cancellationToken);
        await AuditAsync("artifact.staging-binding.created", current, name, cancellationToken);
        return stored;
    }

    public async Task<IReadOnlyList<StoredResource<StagedArtifactResource>>> ListStagedAsync(CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsInspect, cancellationToken);
        return (await store.ListAllAsync<StagedArtifactResource>(ArtifactResourceKinds.StagedArtifact, cancellationToken))
            .Where(value => value.Value.WorkspaceId.Value == current.WorkspaceId)
            .OrderByDescending(value => value.Value.CreatedAt)
            .ToArray();
    }

    public async Task<StoredResource<StagedArtifactResource>?> GetStagedByProducerIdForWriteAsync(
        string producerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producerId);
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsWrite, cancellationToken);
        var matches = (await store.ListAllAsync<StagedArtifactResource>(ArtifactResourceKinds.StagedArtifact, cancellationToken))
            .Where(value => value.Value.WorkspaceId.Value == current.WorkspaceId
                && string.Equals(value.Value.Producer.Id, producerId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length > 1)
            throw Error("staged_artifact_producer_ambiguous",
                $"More than one StagedArtifact exists for producer identity '{producerId}'.");
        return matches.SingleOrDefault();
    }

    public async Task<StoredResource<StagedArtifactResource>?> GetStagedAsync(
        StagedArtifactId id,
        ArtifactLeaseId? leaseId,
        ArtifactLeaseOperation operation,
        CancellationToken cancellationToken)
    {
        var current = Current();
        var stored = await GetStagedSystemAsync(current.WorkspaceId, id, cancellationToken);
        if (stored is null) return null;
        if (leaseId is not null && HasLease(stored.Value, leaseId.Value, operation, timeProvider.GetUtcNow())) return stored;
        await authorization.EnsurePermissionAsync(current, operation == ArtifactLeaseOperation.Read
            ? AuthorizationPermissions.ArtifactsReadContent
            : AuthorizationPermissions.ArtifactsInspect, cancellationToken);
        return stored;
    }

    public async Task<StoredResource<StagedArtifactResource>> CreateStagedAsync(
        CreateStagedArtifactRequest request,
        CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsWrite, cancellationToken);
        ValidateFileName(request.FileName);
        ValidateMediaType(request.MediaType);
        var binding = await ResolveBindingAsync(current.WorkspaceId, request.Binding, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var expiresAt = request.ExpiresAt ?? now + binding.Value.Definition.DefaultRetention;
        if (expiresAt <= now || expiresAt - now > binding.Value.Definition.MaximumRetention)
            throw Error("staged_artifact_expiration_invalid", "The requested expiration is outside the selected binding retention limits.");
        var id = StagedArtifactId.New();
        var command = Command(current, $"staged-artifact:{id}", request.Producer.CorrelationId, request.Producer.FlowRunId, request.Producer.FlowStepId);
        var created = await staging.CreateAsync(binding.Value, id, request.MediaType, command, cancellationToken);
        var backend = new ArtifactBackendResolution
        {
            BindingName = binding.Value.Name,
            BindingNamespace = binding.Value.Namespace,
            ToolSetName = binding.Value.Definition.ToolSet.Name,
            ToolSetNamespace = binding.Value.Definition.ToolSet.Namespace ?? binding.Value.Namespace,
            ToolSetVersion = binding.Value.Definition.ToolSetVersion,
            BackendReference = created.BackendReference
        };
        var resource = new StagedArtifactResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ArtifactResourceKinds.StagedArtifact,
            Metadata = new ResourceMetadata { Name = id.ToString() },
            ScopeRef = ResourceScopeRef.Workspace(current.WorkspaceId),
            Generation = 1,
            Status = Succeeded(),
            TenantId = current.TenantId,
            WorkspaceId = new WorkspaceId(current.WorkspaceId),
            ArtifactId = id,
            FileName = request.FileName,
            MediaType = request.MediaType,
            ArtifactStatus = StagedArtifactStatus.Open,
            Producer = request.Producer,
            Backend = backend,
            CreatedAt = now,
            ExpiresAt = expiresAt
        };
        try
        {
            var stored = await store.PutExactAsync(resource.ScopeRef.Value, resource, null, true, cancellationToken);
            await AuditAsync("staged-artifact.created", current, id.ToString(), cancellationToken);
            return stored;
        }
        catch
        {
            try { await staging.DeleteAsync(binding.Value, backend, command, CancellationToken.None); }
            catch { }
            throw;
        }
    }

    public async Task<StoredResource<StagedArtifactResource>> WriteAsync(
        StagedArtifactId id,
        long offset,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsWrite, cancellationToken);
        if (content.Length is < 1 or > MaximumChunkBytes)
            throw Error("staged_artifact_chunk_invalid", $"Content chunks must contain between 1 and {MaximumChunkBytes} bytes.");
        var stored = await RequireStagedAsync(current.WorkspaceId, id, cancellationToken);
        if (stored.Value.ArtifactStatus != StagedArtifactStatus.Open)
            throw Error("staged_artifact_not_open", "Only an open StagedArtifact accepts content writes.");
        if (offset != stored.Value.Length)
            throw Error("staged_artifact_offset_conflict", $"The next expected offset is {stored.Value.Length}.");
        var binding = await RequireBindingAsync(stored.Value, cancellationToken);
        if (stored.Value.Length + content.Length > binding.Value.Definition.MaximumArtifactBytes)
            throw Error("staged_artifact_too_large", "The write would exceed the selected binding size limit.");
        var length = await staging.WriteAsync(binding.Value, stored.Value.Backend, offset, content,
            Command(current, $"staged-artifact:{id}:write", stored.Value.Producer.CorrelationId,
                stored.Value.Producer.FlowRunId, stored.Value.Producer.FlowStepId), cancellationToken);
        if (length != stored.Value.Length + content.Length)
            throw Error("staged_artifact_backend_length_mismatch", "The staging backend returned an unexpected content length.");
        return await UpdateAsync(stored, stored.Value with { Length = length }, cancellationToken);
    }

    public async Task<StoredResource<StagedArtifactResource>> SealAsync(StagedArtifactId id, CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsWrite, cancellationToken);
        var stored = await RequireStagedAsync(current.WorkspaceId, id, cancellationToken);
        if (stored.Value.ArtifactStatus == StagedArtifactStatus.Sealed) return stored;
        if (stored.Value.ArtifactStatus != StagedArtifactStatus.Open)
            throw Error("staged_artifact_not_open", "Only an open StagedArtifact can be sealed.");
        var binding = await RequireBindingAsync(stored.Value, cancellationToken);
        var stat = await staging.StatAsync(binding.Value, stored.Value.Backend,
            Command(current, $"staged-artifact:{id}:seal", stored.Value.Producer.CorrelationId,
                stored.Value.Producer.FlowRunId, stored.Value.Producer.FlowStepId), cancellationToken);
        if (stat.Length != stored.Value.Length)
            throw Error("staged_artifact_integrity_mismatch", "The staging backend length does not match the registered content length.");
        var updated = stored.Value with
        {
            ArtifactStatus = StagedArtifactStatus.Sealed,
            Sha256 = stat.Sha256,
            SealedAt = timeProvider.GetUtcNow()
        };
        var result = await UpdateAsync(stored, updated, cancellationToken);
        await AuditAsync("staged-artifact.sealed", current, id.ToString(), cancellationToken);
        return result;
    }

    public async Task<ArtifactContentChunk> ReadAsync(
        StagedArtifactId id,
        long offset,
        int length,
        ArtifactLeaseId? leaseId,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || length is < 1 or > MaximumChunkBytes)
            throw Error("staged_artifact_read_range_invalid", $"Read length must be between 1 and {MaximumChunkBytes} bytes.");
        var stored = await GetStagedAsync(id, leaseId, ArtifactLeaseOperation.Read, cancellationToken)
            ?? throw Error("staged_artifact_not_found", $"StagedArtifact '{id}' was not found.");
        if (stored.Value.ArtifactStatus is StagedArtifactStatus.Open or StagedArtifactStatus.Purged or StagedArtifactStatus.Purging or StagedArtifactStatus.Failed)
            throw Error("staged_artifact_not_readable", $"StagedArtifact '{id}' is not readable in state '{stored.Value.ArtifactStatus}'.");
        var current = Current();
        if (offset == 0)
            await AuditAsync("staged-artifact.content-read", current, id.ToString(), cancellationToken);
        var binding = await RequireBindingAsync(stored.Value, cancellationToken);
        return await staging.ReadAsync(binding.Value, stored.Value.Backend, offset, length,
            Command(current, $"staged-artifact:{id}:read", stored.Value.Producer.CorrelationId,
                stored.Value.Producer.FlowRunId, stored.Value.Producer.FlowStepId), cancellationToken);
    }

    public async Task<StoredResource<StagedArtifactResource>> CreateLeaseAsync(
        StagedArtifactId id,
        CreateArtifactLeaseRequest request,
        CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsWrite, cancellationToken);
        var stored = await RequireStagedAsync(current.WorkspaceId, id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (request.ExpiresAt <= now || request.ExpiresAt > stored.Value.ExpiresAt || request.Operations.Count == 0)
            throw Error("artifact_lease_invalid", "A lease requires operations and must expire before the StagedArtifact.");
        if (string.IsNullOrWhiteSpace(request.ConsumerKind) || string.IsNullOrWhiteSpace(request.ConsumerId))
            throw Error("artifact_lease_consumer_required", "A lease requires a consumer kind and identity.");
        var lease = new ArtifactLease
        {
            Id = ArtifactLeaseId.New(),
            ConsumerKind = request.ConsumerKind,
            ConsumerId = request.ConsumerId,
            Operations = request.Operations.Distinct().ToArray(),
            CreatedAt = now,
            ExpiresAt = request.ExpiresAt
        };
        var updated = await UpdateAsync(stored, stored.Value with { Leases = [.. stored.Value.Leases, lease] }, cancellationToken);
        await AuditAsync("staged-artifact.lease-created", current, $"{id}:{lease.Id}", cancellationToken);
        return updated;
    }

    public async Task<StoredResource<StagedArtifactResource>> ExtendRetentionAsync(
        StagedArtifactId id,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsManageRetention, cancellationToken);
        var stored = await RequireStagedAsync(current.WorkspaceId, id, cancellationToken);
        var binding = await RequireBindingAsync(stored.Value, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (expiresAt <= stored.Value.ExpiresAt || expiresAt - now > binding.Value.Definition.MaximumRetention)
            throw Error("staged_artifact_retention_invalid", "Retention can only be extended within the binding maximum retention.");
        var updated = await UpdateAsync(stored, stored.Value with { ExpiresAt = expiresAt }, cancellationToken);
        await AuditAsync("staged-artifact.retention-extended", current, id.ToString(), cancellationToken);
        return updated;
    }

    public async Task<StoredResource<StagedArtifactResource>> RevokeLeaseAsync(
        StagedArtifactId id,
        ArtifactLeaseId leaseId,
        CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsManageRetention, cancellationToken);
        var stored = await RequireStagedAsync(current.WorkspaceId, id, cancellationToken);
        var lease = stored.Value.Leases.SingleOrDefault(value => value.Id == leaseId)
            ?? throw Error("artifact_lease_not_found", $"Lease '{leaseId}' was not found on StagedArtifact '{id}'.");
        if (lease.RevokedAt is not null) return stored;
        var updated = await UpdateAsync(stored, stored.Value with
        {
            Leases = stored.Value.Leases.Select(value => value.Id == leaseId
                ? value with { RevokedAt = timeProvider.GetUtcNow() }
                : value).ToArray()
        }, cancellationToken);
        await AuditAsync("staged-artifact.lease-revoked", current, $"{id}:{leaseId}", cancellationToken);
        return updated;
    }

    public async Task<ArtifactHandoffResult> HandoffAsync(
        StagedArtifactId sourceId,
        ArtifactHandoffRequest request,
        CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsWrite, cancellationToken);
        ValidateToken(request.OperationId, "artifact_handoff_operation_invalid");
        var source = await RequireStagedAsync(current.WorkspaceId, sourceId, cancellationToken);
        if (source.Value.ArtifactStatus is not (StagedArtifactStatus.Sealed or StagedArtifactStatus.Persisted))
            throw Error("staged_artifact_not_sealed", "Only sealed staged content can be handed off.");
        var targetBinding = await ResolveBindingAsync(current.WorkspaceId, request.TargetBinding, cancellationToken);
        var sameBinding = source.Value.Backend.BindingName == targetBinding.Value.Name
            && source.Value.Backend.BindingNamespace == targetBinding.Value.Namespace;
        var reuse = request.Mode == ArtifactHandoffMode.Reuse
            || request.Mode == ArtifactHandoffMode.ReuseOrCopy && sameBinding;
        if (request.Mode == ArtifactHandoffMode.Reuse && !sameBinding)
            throw Error("artifact_handoff_reuse_binding_mismatch", "Reuse requires the source Artifact binding.");

        if (reuse)
        {
            var delegated = await CreateLeaseAsync(sourceId, new(request.ConsumerKind, request.ConsumerId,
                request.Operations, request.LeaseExpiresAt), cancellationToken);
            return new(ArtifactViews.Staged(delegated.Value), delegated.Value.Leases[^1], true, false);
        }

        var recovered = (await ListExactAsync<StagedArtifactResource>(ArtifactResourceKinds.StagedArtifact,
                ResourceScopeRef.Workspace(current.WorkspaceId), cancellationToken))
            .SingleOrDefault(value => value.Value.SourceArtifactId == sourceId
                && string.Equals(value.Value.TransferOperationId, request.OperationId, StringComparison.Ordinal));
        StoredResource<StagedArtifactResource> copied;
        if (recovered is not null)
        {
            copied = recovered;
            if (copied.Value.ArtifactStatus is not (StagedArtifactStatus.Sealed or StagedArtifactStatus.Persisted)
                || copied.Value.Length != source.Value.Length
                || !string.Equals(copied.Value.Sha256, source.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Error("artifact_handoff_recovery_invalid", "A previous transfer attempt exists but is not a valid sealed copy.");
        }
        else
        {
            copied = await CreateStagedAsync(new(source.Value.FileName, source.Value.MediaType, source.Value.Producer,
                new ResourceReference(targetBinding.Value.Name, targetBinding.Value.ScopeRef, targetBinding.Value.Namespace),
                source.Value.ExpiresAt), cancellationToken);
            copied = await UpdateAsync(copied, copied.Value with
            {
                SourceArtifactId = sourceId,
                TransferOperationId = request.OperationId
            }, cancellationToken);
            var sourceBinding = await RequireBindingAsync(source.Value, cancellationToken);
            long offset = 0;
            while (offset < source.Value.Length)
            {
                var length = checked((int)Math.Min(MaximumChunkBytes, source.Value.Length - offset));
                var chunk = await staging.ReadAsync(sourceBinding.Value, source.Value.Backend, offset, length,
                    Command(current, $"artifact-handoff:{request.OperationId}:read", source.Value.Producer.CorrelationId,
                        source.Value.Producer.FlowRunId, source.Value.Producer.FlowStepId), cancellationToken);
                var bytes = Convert.FromBase64String(chunk.ContentBase64);
                copied = await WriteAsync(copied.Value.ArtifactId, offset, bytes, cancellationToken);
                offset = copied.Value.Length;
            }
            copied = await SealAsync(copied.Value.ArtifactId, cancellationToken);
            if (copied.Value.Length != source.Value.Length
                || !string.Equals(copied.Value.Sha256, source.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Error("artifact_handoff_integrity_mismatch", "The copied Artifact failed digest verification.");
        }
        var leased = await CreateLeaseAsync(copied.Value.ArtifactId, new(request.ConsumerKind, request.ConsumerId,
            request.Operations, request.LeaseExpiresAt), cancellationToken);
        if (request.Mode == ArtifactHandoffMode.Move) await PurgeAsync(sourceId, cancellationToken);
        await AuditAsync("staged-artifact.handed-off", current, $"{sourceId}:{leased.Value.ArtifactId}:{request.Mode}", cancellationToken);
        return new(ArtifactViews.Staged(leased.Value), leased.Value.Leases[^1], false, recovered is not null);
    }

    public async Task<StoredResource<FlowRunArtifactResource>> CompleteFlowRunArtifactAsync(
        StagedArtifactId sourceId,
        CompleteFlowRunArtifactRequest request,
        CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsWrite, cancellationToken);
        var source = await RequireStagedAsync(current.WorkspaceId, sourceId, cancellationToken);
        if (source.Value.ArtifactStatus is not (StagedArtifactStatus.Sealed or StagedArtifactStatus.Persisted))
            throw Error("staged_artifact_not_sealed", "Only a sealed StagedArtifact can complete a FlowRunArtifact.");
        if (!string.Equals(source.Value.Sha256, request.Receipt.Sha256, StringComparison.OrdinalIgnoreCase)
            || source.Value.Length != request.Receipt.Length
            || !string.Equals(source.Value.MediaType, request.Receipt.MediaType, StringComparison.OrdinalIgnoreCase))
            throw Error("artifact_storage_receipt_mismatch", "The storage receipt does not match the sealed StagedArtifact integrity metadata.");
        var existing = (await store.ListAllAsync<FlowRunArtifactResource>(ArtifactResourceKinds.FlowRunArtifact, cancellationToken))
            .SingleOrDefault(value => value.Value.WorkspaceId.Value == current.WorkspaceId
                && value.Value.SourceArtifactId == sourceId
                && string.Equals(value.Value.Receipt.StorageFlowRunId, request.Receipt.StorageFlowRunId, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (!SameReceipt(existing.Value.Receipt, request.Receipt))
                throw Error("artifact_storage_receipt_conflict", "This Storage FlowRun already completed the StagedArtifact with a different receipt.");
            return existing;
        }
        var id = FlowRunArtifactId.New();
        var resource = new FlowRunArtifactResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ArtifactResourceKinds.FlowRunArtifact,
            Metadata = new ResourceMetadata { Name = id.ToString() },
            ScopeRef = ResourceScopeRef.Workspace(current.WorkspaceId),
            Generation = 1,
            Status = Succeeded(),
            WorkspaceId = new WorkspaceId(current.WorkspaceId),
            ArtifactId = id,
            SourceArtifactId = sourceId,
            ProducerFlowRunId = request.ProducerFlowRunId,
            ProducerFlowStepId = request.ProducerFlowStepId,
            Receipt = request.Receipt,
            CreatedAt = timeProvider.GetUtcNow()
        };
        var created = await store.CreateImmutableAsync(resource, cancellationToken);
        if (source.Value.ArtifactStatus != StagedArtifactStatus.Persisted)
            _ = await UpdateAsync(source, source.Value with
            {
                ArtifactStatus = StagedArtifactStatus.Persisted,
                PersistedAt = timeProvider.GetUtcNow()
            }, cancellationToken);
        await AuditAsync("flow-run-artifact.completed", current, id.ToString(), cancellationToken);
        return created;
    }

    public async Task<IReadOnlyList<StoredResource<FlowRunArtifactResource>>> ListFlowRunArtifactsAsync(CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsInspect, cancellationToken);
        return (await store.ListAllAsync<FlowRunArtifactResource>(ArtifactResourceKinds.FlowRunArtifact, cancellationToken))
            .Where(value => value.Value.WorkspaceId.Value == current.WorkspaceId)
            .OrderByDescending(value => value.Value.CreatedAt)
            .ToArray();
    }

    public async Task<StoredResource<FlowRunArtifactResource>?> GetFlowRunArtifactAsync(
        FlowRunArtifactId id,
        CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsInspect, cancellationToken);
        return await store.GetExactAsync<FlowRunArtifactResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Workspace(current.WorkspaceId), ResourceNamespace.Default,
            ArtifactResourceKinds.FlowRunArtifact, id.ToString()), cancellationToken);
    }

    public async Task<StoredResource<FlowRunArtifactResource>?> GetFlowRunArtifactForContentReadAsync(
        FlowRunArtifactId id,
        bool auditAccess,
        CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsReadContent, cancellationToken);
        var stored = await store.GetExactAsync<FlowRunArtifactResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Workspace(current.WorkspaceId), ResourceNamespace.Default,
            ArtifactResourceKinds.FlowRunArtifact, id.ToString()), cancellationToken);
        if (stored is not null && auditAccess)
            await AuditAsync("flow-run-artifact.content-materialization-requested", current, id.ToString(), cancellationToken);
        return stored;
    }

    public async Task PurgeAsync(StagedArtifactId id, CancellationToken cancellationToken)
    {
        var current = await RequireAsync(AuthorizationPermissions.ArtifactsPurge, cancellationToken);
        var stored = await RequireStagedAsync(current.WorkspaceId, id, cancellationToken);
        if (stored.Value.ArtifactStatus == StagedArtifactStatus.Purged) return;
        var now = timeProvider.GetUtcNow();
        if (stored.Value.Leases.Any(value => value.RevokedAt is null && value.ExpiresAt > now))
            throw Error("staged_artifact_active_lease", "The StagedArtifact cannot be purged while an active lease exists.");
        if (stored.Value.ArtifactStatus is StagedArtifactStatus.Open or StagedArtifactStatus.Persisting)
            throw Error("staged_artifact_purge_ineligible", $"StagedArtifact state '{stored.Value.ArtifactStatus}' is not eligible for purge.");
        var purging = await UpdateAsync(stored, stored.Value with { ArtifactStatus = StagedArtifactStatus.Purging }, cancellationToken);
        var binding = await RequireBindingAsync(purging.Value, cancellationToken);
        try
        {
            await staging.DeleteAsync(binding.Value, purging.Value.Backend,
                Command(current, $"staged-artifact:{id}:purge", purging.Value.Producer.CorrelationId,
                    purging.Value.Producer.FlowRunId, purging.Value.Producer.FlowStepId), cancellationToken);
            _ = await UpdateAsync(purging, purging.Value with
            {
                ArtifactStatus = StagedArtifactStatus.Purged,
                PurgedAt = timeProvider.GetUtcNow(),
                Leases = []
            }, cancellationToken);
            await AuditAsync("staged-artifact.purged", current, id.ToString(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = await UpdateAsync(purging, purging.Value with
            {
                ArtifactStatus = StagedArtifactStatus.Failed,
                FailureCode = "staged_artifact_backend_delete_failed",
                FailureMessage = exception.Message
            }, CancellationToken.None);
            throw;
        }
    }

    public async Task<int> ExpireAndPurgeAsync(int maximum, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        maximum = Math.Min(maximum, 500);
        var now = timeProvider.GetUtcNow();
        var candidates = (await store.ListAllAsync<StagedArtifactResource>(ArtifactResourceKinds.StagedArtifact, cancellationToken))
            .Where(value => value.Value.ExpiresAt <= now
                && value.Value.ArtifactStatus is StagedArtifactStatus.Sealed or StagedArtifactStatus.Persisted or StagedArtifactStatus.Expired
                && !value.Value.Leases.Any(lease => lease.RevokedAt is null && lease.ExpiresAt > now))
            .OrderBy(value => value.Value.ExpiresAt)
            .Take(maximum)
            .ToArray();
        var count = 0;
        foreach (var candidate in candidates)
        {
            var expired = candidate.Value.ArtifactStatus == StagedArtifactStatus.Expired
                ? candidate
                : await UpdateAsync(candidate, candidate.Value with { ArtifactStatus = StagedArtifactStatus.Expired }, cancellationToken);
            var current = requestContext.IsInitialized
                ? requestContext.Current
                : new RequestContext(Guid.Empty, expired.Value.TenantId, expired.Value.WorkspaceId.Value);
            try
            {
                var binding = await RequireBindingAsync(expired.Value, cancellationToken);
                await staging.DeleteAsync(binding.Value, expired.Value.Backend,
                    Command(current, $"staged-artifact:{expired.Value.ArtifactId}:expire",
                        expired.Value.Producer.CorrelationId, expired.Value.Producer.FlowRunId, expired.Value.Producer.FlowStepId), cancellationToken);
                _ = await UpdateAsync(expired, expired.Value with
                {
                    ArtifactStatus = StagedArtifactStatus.Purged,
                    PurgedAt = timeProvider.GetUtcNow(),
                    FailureCode = null,
                    FailureMessage = null,
                    Leases = []
                }, cancellationToken);
                count++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _ = await UpdateAsync(expired, expired.Value with
                {
                    ArtifactStatus = StagedArtifactStatus.Expired,
                    FailureCode = "staged_artifact_backend_delete_failed",
                    FailureMessage = exception.Message
                }, CancellationToken.None);
            }
        }
        return count;
    }

    private async Task<RequestContext> RequireAsync(string permission, CancellationToken cancellationToken)
    {
        var current = Current();
        await authorization.EnsurePermissionAsync(current, permission, cancellationToken);
        return current;
    }

    private RequestContext Current() => requestContext.IsInitialized
        ? requestContext.Current
        : throw new ArtifactValidationException("artifact_execution_scope_required", "Artifact operations require a trusted Tenant, Workspace, and Principal scope.");

    private async Task<StoredResource<ArtifactStagingBindingResource>> ResolveBindingAsync(
        Guid workspaceId,
        ResourceReference? reference,
        CancellationToken cancellationToken)
    {
        var scope = ResourceScopeRef.Workspace(workspaceId);
        var bindings = await ListBindingsSystemAsync(scope, cancellationToken);
        var selected = reference is null
            ? bindings.SingleOrDefault(value => value.Value.Definition.IsDefault)
            : bindings.SingleOrDefault(value => value.Value.Name == reference.Name
                && value.Value.Namespace == (reference.Namespace ?? ResourceNamespace.Default));
        if (selected is null || !selected.Value.Definition.Enabled)
            throw Error("artifact_staging_binding_unavailable", reference is null
                ? "No enabled default ArtifactStagingBinding is configured."
                : $"ArtifactStagingBinding '{reference.Name}' is unavailable.");
        return selected;
    }

    private async Task<StoredResource<ArtifactStagingBindingResource>> RequireBindingAsync(
        StagedArtifactResource artifact,
        CancellationToken cancellationToken)
    {
        var address = ScopedResourceAddress.Create(ResourceScopeRef.Workspace(artifact.WorkspaceId.Value),
            artifact.Backend.BindingNamespace, ArtifactResourceKinds.ArtifactStagingBinding, artifact.Backend.BindingName);
        var binding = await store.GetExactAsync<ArtifactStagingBindingResource>(address, cancellationToken)
            ?? throw Error("artifact_staging_binding_not_found", $"Resolved binding '{artifact.Backend.BindingNamespace}/{artifact.Backend.BindingName}' no longer exists.");
        if (binding.Value.Definition.ToolSetVersion != artifact.Backend.ToolSetVersion
            || binding.Value.Definition.ToolSet.Name != artifact.Backend.ToolSetName
            || (binding.Value.Definition.ToolSet.Namespace ?? binding.Value.Namespace) != artifact.Backend.ToolSetNamespace)
            throw Error("artifact_staging_binding_changed", "The binding no longer resolves to the backend version that created this artifact.");
        return binding;
    }

    private Task<IReadOnlyList<StoredResource<ArtifactStagingBindingResource>>> ListBindingsSystemAsync(
        ResourceScopeRef scope,
        CancellationToken cancellationToken) => ListExactAsync<ArtifactStagingBindingResource>(ArtifactResourceKinds.ArtifactStagingBinding, scope, cancellationToken);

    private async Task<IReadOnlyList<StoredResource<T>>> ListExactAsync<T>(string kind, ResourceScopeRef scope, CancellationToken cancellationToken)
        where T : Resource => (await store.ListAllAsync<T>(kind, cancellationToken)).Where(value => value.Value.ScopeRef == scope).ToArray();

    private Task<StoredResource<StagedArtifactResource>?> GetStagedSystemAsync(Guid workspaceId, StagedArtifactId id, CancellationToken cancellationToken) =>
        store.GetExactAsync<StagedArtifactResource>(ScopedResourceAddress.Create(ResourceScopeRef.Workspace(workspaceId),
            ResourceNamespace.Default, ArtifactResourceKinds.StagedArtifact, id.ToString()), cancellationToken);

    private async Task<StoredResource<StagedArtifactResource>> RequireStagedAsync(Guid workspaceId, StagedArtifactId id, CancellationToken cancellationToken) =>
        await GetStagedSystemAsync(workspaceId, id, cancellationToken)
        ?? throw Error("staged_artifact_not_found", $"StagedArtifact '{id}' was not found in the current Workspace.");

    private Task<StoredResource<StagedArtifactResource>> UpdateAsync(
        StoredResource<StagedArtifactResource> stored,
        StagedArtifactResource value,
        CancellationToken cancellationToken) => store.PutExactAsync(
            value.ScopeRef ?? throw Error("staged_artifact_scope_invalid", "The StagedArtifact has no Workspace scope."),
            value with { Generation = checked(stored.Value.Generation + 1) }, stored.ETag, false, cancellationToken);

    private static bool HasLease(StagedArtifactResource artifact, ArtifactLeaseId id, ArtifactLeaseOperation operation, DateTimeOffset now) =>
        artifact.Leases.Any(value => value.Id == id && value.RevokedAt is null && value.ExpiresAt > now && value.Operations.Contains(operation));

    private static bool SameReceipt(ArtifactStorageReceipt left, ArtifactStorageReceipt right) =>
        string.Equals(left.StorageFlowRunId, right.StorageFlowRunId, StringComparison.Ordinal)
        && string.Equals(left.OpaqueReference, right.OpaqueReference, StringComparison.Ordinal)
        && string.Equals(left.MediaType, right.MediaType, StringComparison.OrdinalIgnoreCase)
        && left.Length == right.Length
        && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase)
        && left.Provenance.Count == right.Provenance.Count
        && left.Provenance.All(item => right.Provenance.TryGetValue(item.Key, out var value)
            && string.Equals(item.Value, value, StringComparison.Ordinal));

    private static void ValidateBinding(string name, ArtifactStagingBindingProperties properties)
    {
        ValidateToken(name, "artifact_staging_binding_name_invalid");
        if (string.IsNullOrWhiteSpace(properties.DisplayName) || properties.DisplayName.Length > 200)
            throw Error("artifact_staging_binding_display_name_invalid", "A binding display name of at most 200 characters is required.");
        if (string.IsNullOrWhiteSpace(properties.ToolSetVersion) || properties.ToolSetVersion.Length > 64)
            throw Error("artifact_staging_toolset_version_invalid", "A published ToolSet version is required.");
        if (properties.MaximumArtifactBytes is < 1024 or > 1024L * 1024 * 1024)
            throw Error("artifact_staging_size_invalid", "Maximum artifact size must be between 1 KiB and 1 GiB.");
        if (properties.DefaultRetention <= TimeSpan.Zero || properties.MaximumRetention < properties.DefaultRetention || properties.MaximumRetention > TimeSpan.FromDays(365))
            throw Error("artifact_staging_retention_invalid", "Binding retention limits are invalid.");
    }

    private static void ValidateFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw Error("staged_artifact_file_name_invalid", "A safe file name of at most 240 characters is required.");
    }

    private static void ValidateMediaType(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || !value.Contains('/', StringComparison.Ordinal))
            throw Error("staged_artifact_media_type_invalid", "A valid media type is required.");
    }

    private static void ValidateToken(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw Error(code, "The value must contain only letters, digits, '.', '-' or '_' and be at most 128 characters.");
    }

    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
    private static ArtifactValidationException Error(string code, string message) => new(code, message);
    private static ArtifactBackendCommandContext Command(RequestContext current, string callId,
        string? correlationId = null, string? runId = null, string? flowStepId = null) =>
        new(current.TenantId, new WorkspaceId(current.WorkspaceId), current.PrincipalId, callId, correlationId, runId, flowStepId);
    private Task AuditAsync(string action, RequestContext current, string target, CancellationToken cancellationToken)
    {
        _ = target;
        return audit.WriteAsync(new(action, WorkspaceId: current.WorkspaceId), cancellationToken);
    }
}
