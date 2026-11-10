using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Security.Contracts;

namespace Agentstration.Knowledge;

public sealed class KnowledgeProjectionException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public interface IKnowledgeProjectionInputResolver
{
    Task<ResolvedKnowledgeDataSourceBinding> ResolveDependencyAsync(
        ResourceScopeRef ownerScope,
        ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding,
        CancellationToken cancellationToken);

    Task<KnowledgeDataSourceBindingReadiness> GetReadinessAsync(
        ResourceScopeRef executionScope,
        ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding,
        ResolvedKnowledgeDataSourceBinding dependency,
        CancellationToken cancellationToken);

    Task RevalidateAsync(ResourceScopeRef executionScope, ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding, ResolvedKnowledgeDataSourceBinding dependency,
        KnowledgeProjectionInputEvidence evidence, CancellationToken cancellationToken);

    Task<KnowledgeProjectionInputEvidence> ResolveAsync(
        ResourceScopeRef executionScope,
        ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding,
        ResolvedKnowledgeDataSourceBinding dependency,
        string? acquisitionId,
        string requestId,
        CancellationToken cancellationToken);
}

public interface IKnowledgeArtifactReferenceValidator
{
    Task ValidateAsync(
        Guid workspaceId,
        IReadOnlyList<KnowledgeProjectionArtifact> references,
        CancellationToken cancellationToken);
}

public sealed record KnowledgeProjectionFlowRunRequest(
    string RunId,
    ResolvedKnowledgeFlowBinding Flow,
    JsonElement Input,
    string CallerId,
    string CorrelationId,
    Guid TenantId,
    Guid WorkspaceId,
    Guid PrincipalId)
{
    public ResourceScopeRef? LogicalScopeRef { get; init; }
}

public sealed record KnowledgeProjectionFlowRunResult(
    string RunId,
    KnowledgeProjectionState State,
    JsonElement? Output,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? CompletedAt);

public interface IKnowledgeProjectionFlowGateway
{
    Task<KnowledgeProjectionFlowRunResult> ExecuteAsync(
        KnowledgeProjectionFlowRunRequest request,
        CancellationToken cancellationToken);
}

public sealed class KnowledgeProjectionService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    ICurrentRequestContext requestContext,
    IRequestContextScopeFactory requestScopes,
    IAuthorizationService authorization,
    KnowledgeSourceManagementService sources,
    IKnowledgeFlowResolver flows,
    IKnowledgeProjectionInputResolver inputs,
    IKnowledgeProjectionFlowGateway flowRuns,
    IKnowledgeArtifactReferenceValidator artifactReferences,
    IKnowledgeSnapshotArtifactResolver artifacts,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider,
    IKnowledgeOutputPublisher outputPublisher)
{
    private static readonly JsonSerializerOptions FlowContractJsonOptions = CreateFlowContractJsonOptions();
    private readonly ConcurrentDictionary<(Guid WorkspaceId, Guid SourceUid), SemaphoreSlim> gates = new();
    private readonly SemaphoreSlim admission = new(32, 32);

    public const int MaximumInputs = 32;
    public const int MaximumArtifacts = 100;
    public const int MaximumParametersBytes = 64 * 1024;

    public async Task<StoredResource<KnowledgeProjectionResource>> StartAsync(
        KnowledgeSourceId sourceId,
        StartKnowledgeProjectionRequest request,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken);
        ValidateParameters(request.Parameters);
        var executionScope = ResourceScopeRef.Workspace(context.WorkspaceId);
        StoredResource<KnowledgeSourceResource> source;
        try { source = await sources.ResolveForUseAsync(sourceId, KnowledgeOperationalRead.AdministrativeScope(requestContext) ?? executionScope, cancellationToken); }
        catch (KnowledgeSourceNotFoundException)
        { throw Error("knowledge_projection_source_not_found", $"KnowledgeSource '{sourceId}' was not found."); }
        catch (KnowledgeSourceValidationException exception)
        { throw Error(exception.Code, exception.Message, exception); }
        if (!source.Value.Definition.Enabled)
            throw Error("knowledge_projection_source_disabled", $"KnowledgeSource '{source.Value.Address}' is disabled.");
        if (source.Value.Definition.DataSources.Count is < 1 or > MaximumInputs)
            throw Error("knowledge_projection_inputs_invalid", $"A KnowledgeSource requires 1 to {MaximumInputs} Data Source bindings.");
        if (source.Value.Definition.ProjectionFlow is null)
            throw Error("knowledge_projection_flow_required", "A KnowledgeSource projection Flow is required.");
        if (request.ExpectedSourceUid is { } expectedUid && source.Value.Uid != expectedUid
            || request.ExpectedSourceGeneration is { } expectedGeneration && source.Value.Generation != expectedGeneration)
            throw Error("knowledge_projection_configuration_changed", "The accepted Source configuration changed.");

        if (gates.Count >= 1024 && !gates.ContainsKey((context.WorkspaceId, source.Value.Uid)))
            throw Error("knowledge_projection_capacity", "The projection admission capacity has been reached.");
        var gate = gates.GetOrAdd((context.WorkspaceId, source.Value.Uid), _ => new SemaphoreSlim(1, 1));
        if (!await admission.WaitAsync(0, cancellationToken))
            throw Error("knowledge_projection_capacity", "The projection admission capacity has been reached.");
        try { await gate.WaitAsync(cancellationToken); }
        catch { admission.Release(); throw; }
        try
        {
            var scopeRef = executionScope;
            var composition = source.Value.Composition
                ?? throw Error("knowledge_source_composition_missing",
                    "The KnowledgeSource does not retain an exact dependency composition and must be recreated.");
            var projectionFlow = await ResolveFlowAsync(composition.ProjectionFlow,
                KnowledgeFlowContracts.Projection, "projection", cancellationToken);
            // Preserve optional historical retrieval provenance without executing it during preparation.
            var retrievalFlow = composition.RetrievalFlow;
            if (request.IdempotencyKey is { Length: > 200 })
                throw Error("knowledge_projection_idempotency_invalid", "The idempotency key exceeds 200 characters.");
            var requestHash = Hash(JsonSerializer.SerializeToElement(new
            { source.Value.Uid, source.Value.Generation, request.Parameters, request.AcquisitionIds, context.PrincipalId }));
            var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? Guid.NewGuid().ToString("N") : request.IdempotencyKey;
            var projectionId = "projection-" + Hash(JsonSerializer.SerializeToElement(new { source.Value.Uid, IdempotencyKey = idempotencyKey }))[..32];
            var previous = await InWorkspaceAsync(() => store.GetExactAsync<KnowledgeProjectionResource>(ScopedResourceAddress.Create(
                scopeRef, source.Value.Namespace, KnowledgeResourceKinds.KnowledgeProjection, projectionId), cancellationToken));
            if (previous is not null)
            {
                if (previous.Value.RequestHash != requestHash)
                    throw Error("knowledge_projection_idempotency_conflict", "This request identity belongs to a different projection configuration.");
                if (previous.Value.State != KnowledgeProjectionState.Running) return previous;
            }
            var correlationId = previous?.Value.CorrelationId ?? (string.IsNullOrWhiteSpace(request.CorrelationId) ? projectionId : request.CorrelationId.Trim());
            var resolvedInputs = new List<KnowledgeProjectionInputEvidence>(source.Value.Definition.DataSources.Count);
            var inputIssues = new List<KnowledgeProjectionInputIssue>();
            if (previous is not null)
            {
                foreach (var evidence in previous.Value.Inputs)
                {
                    var binding = source.Value.Definition.DataSources.Single(value => value.Name == evidence.BindingName);
                    var dependency = composition.DataSources.Single(value => value.BindingName == evidence.BindingName);
                    await inputs.RevalidateAsync(KnowledgeOperationalRead.AdministrativeScope(requestContext) ?? scopeRef,
                        source.Value.Namespace, binding, dependency, evidence, cancellationToken);
                    resolvedInputs.Add(evidence);
                }
            }
            var resource = new KnowledgeProjectionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeProjection,
                Metadata = new() { Name = projectionId, Namespace = source.Value.Namespace },
                ScopeRef = scopeRef,
                Generation = 1,
                Status = new() { ProvisioningState = ProvisioningState.Accepted },
                RequestHash = requestHash,
                IdempotencyKey = idempotencyKey,
                LogicalScopeRef = KnowledgeOperationalRead.AdministrativeScope(requestContext),
                AcquisitionIds = request.AcquisitionIds,
                Output = source.Value.Definition.Output,
                KnowledgeSourceUid = source.Value.Uid,
                KnowledgeSourceScopeRef = source.Value.ScopeRef,
                KnowledgeSourceName = source.Value.Name,
                KnowledgeSourceNamespace = source.Value.Namespace,
                KnowledgeSourceGeneration = source.Value.Generation,
                ProjectionFlow = projectionFlow,
                RetrievalFlow = retrievalFlow,
                Inputs = resolvedInputs.ToArray(),
                InputIssues = inputIssues.ToArray(),
                ProjectionFlowRunId = $"flowrun-knowledge-{projectionId[11..]}",
                State = KnowledgeProjectionState.Running,
                CorrelationId = correlationId,
                Parameters = request.Parameters.Clone(),
                CreatedBy = context.PrincipalId,
                TenantId = context.TenantId,
                WorkspaceId = context.WorkspaceId,
                CreatedAt = timeProvider.GetUtcNow()
            };
            var stored = previous ?? await InWorkspaceAsync(() => scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.RunsExecute,
                token => store.PutExactAsync(scopeRef, resource, null, true, token), cancellationToken));
            resource = stored.Value;
            StoredResource<KnowledgeSnapshotResource>? published = null;

            try
            {
                foreach (var binding in resolvedInputs.Count == 0 ? source.Value.Definition.DataSources : [])
                {
                    request.AcquisitionIds.TryGetValue(binding.Name, out var acquisitionId);
                    var dependency = composition.DataSources.SingleOrDefault(value =>
                        string.Equals(value.BindingName, binding.Name, StringComparison.Ordinal))
                        ?? throw Error("knowledge_projection_dependency_missing",
                            $"Binding '{binding.Name}' has no exact Data Source dependency pin.");
                    try
                    {
                        resolvedInputs.Add(await inputs.ResolveAsync(KnowledgeOperationalRead.AdministrativeScope(requestContext) ?? scopeRef, source.Value.Namespace, binding,
                            dependency, acquisitionId, projectionId + ":" + binding.Name, cancellationToken));
                    }
                    catch (KnowledgeProjectionException exception) when (!binding.Required)
                    {
                        inputIssues.Add(new(binding.Name, exception.Code, exception.Message));
                    }
                }
                if (resolvedInputs.Count == 0)
                    throw Error("knowledge_projection_no_ready_input", "No configured Data Source input is ready for projection.");

                if (stored.Value.Inputs.Count == 0)
                    stored = await InWorkspaceAsync(() => store.PutExactAsync(scopeRef, stored.Value with
                    { Generation = checked(stored.Value.Generation + 1), Inputs = resolvedInputs.ToArray(), InputIssues = inputIssues.ToArray() }, stored.ETag, false, cancellationToken));
                var transformation = await TransformAsync(source.Value, resolvedInputs, projectionId, correlationId, context, cancellationToken);
                var prepared = transformation.Inputs;
                inputIssues.AddRange(transformation.Issues);
                if (prepared.Count == 0)
                    throw Error("knowledge_projection_no_ready_input", "No configured Data Source input is ready for projection.");
                var projectionInput = JsonSerializer.SerializeToElement(new KnowledgeProjectionFlowInput
                {
                    KnowledgeSourceId = $"{source.Value.Namespace}/{source.Value.Name}",
                    KnowledgeSourceUid = source.Value.Uid,
                    KnowledgeSourceGeneration = source.Value.Generation,
                    Inputs = prepared,
                    Artifacts = prepared.SelectMany(value => value.PreparedArtifacts).ToArray(),
                    Parameters = request.Parameters.Clone(),
                    Caller = new(context.PrincipalId, context.TenantId, context.WorkspaceId),
                    CorrelationId = correlationId,
                    ProjectionId = projectionId
                }, FlowContractJsonOptions);
                var run = await flowRuns.ExecuteAsync(new(resource.ProjectionFlowRunId, projectionFlow, projectionInput,
                    context.PrincipalId.ToString("D"), correlationId, context.TenantId, context.WorkspaceId,
                    context.PrincipalId)
                { LogicalScopeRef = KnowledgeOperationalRead.AdministrativeScope(requestContext) }, cancellationToken);
                if (run.State != KnowledgeProjectionState.Succeeded)
                    throw Error(run.ErrorCode ?? "knowledge_projection_flow_failed",
                        run.ErrorMessage ?? $"Projection Flow ended with state '{run.State}'.");
                var manifest = ParseManifest(run.Output, "projection");
                if (manifest.Artifacts.Any(value => value.Kind is not (KnowledgeArtifactKind.Durable or KnowledgeArtifactKind.Inline)
                    || value.Disposition != KnowledgeArtifactDisposition.Publishable))
                    throw Error("knowledge_projection_output_not_publishable",
                        "Every projected Snapshot output must be a durable publishable Artifact.");
                var snapshot = await PublishSnapshotAsync(source.Value, stored.Value with { Inputs = prepared },
                    manifest, context, cancellationToken);
                published = snapshot;
                await audit.WriteAsync(new(SecurityAuditActions.KnowledgeSnapshotPublished,
                    ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId,
                    WorkspaceId: context.WorkspaceId, ReasonCode: projectionId), cancellationToken);
                await SaveObservedAsync(scopeRef, source.Value, snapshot.Value, cancellationToken);
                var completed = stored.Value with
                {
                    Generation = checked(stored.Value.Generation + 1),
                    Inputs = prepared,
                    InputIssues = inputIssues.ToArray(),
                    State = KnowledgeProjectionState.Succeeded,
                    CompletedAt = run.CompletedAt ?? timeProvider.GetUtcNow(),
                    Manifest = manifest,
                    SnapshotName = snapshot.Value.Name,
                    Status = new() { ProvisioningState = ProvisioningState.Succeeded }
                };
                var result = await InWorkspaceAsync(() => store.PutExactAsync(scopeRef, completed, stored.ETag, false, cancellationToken));
                return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (published is not null || exception is KnowledgeProjectionException { Code: "knowledge_projection_publication_pending" })
                    throw Error("knowledge_projection_publication_pending", "The Snapshot is sealed; activation or completion will resume with the same request identity.", exception);
                var mapped = exception as KnowledgeProjectionException
                    ?? (exception is StorageException storage ? Error(storage.Code, storage.Message, storage)
                        : Error("knowledge_projection_failed", exception.Message, exception));
                var failed = stored.Value with
                {
                    Generation = checked(stored.Value.Generation + 1),
                    State = KnowledgeProjectionState.Failed,
                    CompletedAt = timeProvider.GetUtcNow(),
                    ErrorCode = mapped.Code,
                    ErrorMessage = mapped.Message,
                    Status = new() { ProvisioningState = ProvisioningState.Failed }
                };
                try { _ = await InWorkspaceAsync(() => store.PutExactAsync(scopeRef, failed, stored.ETag, false, cancellationToken)); }
                catch (ResourceConcurrencyException) { }
                throw mapped;
            }
        }
        finally
        {
            gate.Release();
            admission.Release();
        }
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await store.ListAllAsync<KnowledgeProjectionResource>(KnowledgeResourceKinds.KnowledgeProjection, cancellationToken);
        foreach (var item in pending.Where(value => value.Value.State == KnowledgeProjectionState.Running
                     && value.Value.IdempotencyKey is not null).OrderBy(value => value.Value.CreatedAt).Take(32))
        {
            var value = item.Value;
            var caller = new RequestContext(value.CreatedBy, value.TenantId, value.WorkspaceId);
            using var callerScope = value.LogicalScopeRef?.Kind switch
            {
                ResourceScopeKind.Instance => requestScopes.PushInstance(caller),
                ResourceScopeKind.Tenant => requestScopes.PushTenantAdministration(caller),
                _ => requestScopes.Push(caller)
            };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                _ = await StartAsync(new(value.KnowledgeSourceName, value.KnowledgeSourceNamespace, value.KnowledgeSourceScopeRef),
                    new()
                    {
                        IdempotencyKey = value.IdempotencyKey,
                        AcquisitionIds = value.AcquisitionIds,
                        Parameters = value.Parameters,
                        CorrelationId = value.CorrelationId
                    }, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (KnowledgeProjectionException exception) when (exception.Code == "knowledge_projection_publication_pending") { }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var latest = await InWorkspaceAsync(() => store.GetExactAsync<KnowledgeProjectionResource>(new(value.ScopeRef!.Value,
                    value.Namespace, value.Kind, value.Name), cancellationToken));
                if (latest?.Value.State != KnowledgeProjectionState.Running) continue;
                try
                {
                    _ = await InWorkspaceAsync(() => store.PutExactAsync(value.ScopeRef!.Value, latest.Value with
                    {
                        Generation = checked(latest.Value.Generation + 1),
                        State = KnowledgeProjectionState.Failed,
                        CompletedAt = timeProvider.GetUtcNow(),
                        ErrorCode = "knowledge_projection_recovery_refused",
                        ErrorMessage = "The original projection dependencies or authorization are unavailable."
                    }, latest.ETag, false, cancellationToken));
                }
                catch (ResourceConcurrencyException) { }
            }
        }
    }

    public async Task<IReadOnlyList<KnowledgeProjectionResource>> ListAsync(
        KnowledgeSourceId? sourceId,
        CancellationToken cancellationToken)
    {
        var administrative = KnowledgeOperationalRead.AdministrativeContext(requestContext);
        var context = administrative ?? RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        Guid? sourceUid = null;
        if (sourceId is { } requested)
        {
            try
            {
                sourceUid = (await sources.ResolveForUseAsync(requested,
                KnowledgeOperationalRead.AdministrativeScope(requestContext) ?? scopeRef, cancellationToken)).Value.Uid;
            }
            catch (KnowledgeSourceNotFoundException)
            { throw Error("knowledge_projection_source_not_found", $"KnowledgeSource '{requested}' was not found."); }
        }
        using var operational = administrative is null ? null : requestScopes.Push(context);
        var values = await store.ListExactAsync<KnowledgeProjectionResource>(scopeRef,
            KnowledgeResourceKinds.KnowledgeProjection, 0, 1000, cancellationToken);
        return values.Select(value => value.Value)
            .Where(value => sourceUid is null || value.KnowledgeSourceUid == sourceUid)
            .OrderByDescending(value => value.CreatedAt).ToArray();
    }

    public async Task<StoredResource<KnowledgeProjectionResource>> GetAsync(
        string id,
        ResourceNamespace @namespace,
        CancellationToken cancellationToken)
    {
        var administrative = KnowledgeOperationalRead.AdministrativeContext(requestContext);
        var context = administrative ?? RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        using var operational = administrative is null ? null : requestScopes.Push(context);
        return await InWorkspaceAsync(() => store.GetExactAsync<KnowledgeProjectionResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Workspace(context.WorkspaceId), @namespace, KnowledgeResourceKinds.KnowledgeProjection, id),
            cancellationToken)) ?? throw Error("knowledge_projection_not_found", $"Knowledge projection '{id}' was not found.");
    }

    private async Task<(List<KnowledgeProjectionInputEvidence> Inputs, List<KnowledgeProjectionInputIssue> Issues)> TransformAsync(
        KnowledgeSourceResource source,
        IReadOnlyList<KnowledgeProjectionInputEvidence> resolved,
        string projectionId,
        string correlationId,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var result = new List<KnowledgeProjectionInputEvidence>(resolved.Count);
        var issues = new List<KnowledgeProjectionInputIssue>();
        foreach (var evidence in resolved)
        {
            var binding = source.Definition.DataSources.Single(value => value.Name == evidence.BindingName);
            if (binding.TransformationFlow is null)
            {
                result.Add(evidence with { PreparedArtifacts = evidence.AcquiredArtifacts });
                continue;
            }
            try
            {
                var pinned = source.Composition?.TransformationFlows.GetValueOrDefault(binding.Name)
                    ?? throw Error("knowledge_projection_dependency_missing",
                        $"Binding '{binding.Name}' has no exact transformation Flow pin.");
                var flow = await ResolveFlowAsync(pinned,
                    KnowledgeFlowContracts.ArtifactTransformation, "transformation", cancellationToken);
                var runId = "flowrun-transform-" + Hash(JsonSerializer.SerializeToElement(new { projectionId, binding.Name }))[..32];
                var input = JsonSerializer.SerializeToElement(new KnowledgeArtifactTransformationInput
                {
                    BindingName = binding.Name,
                    DataSource = evidence,
                    Artifacts = evidence.AcquiredArtifacts,
                    Configuration = binding.Configuration.Clone(),
                    Caller = new(context.PrincipalId, context.TenantId, context.WorkspaceId),
                    CorrelationId = correlationId,
                    TransformationId = $"{source.Name}:{binding.Name}:{evidence.AcquisitionId}"
                }, FlowContractJsonOptions);
                var run = await flowRuns.ExecuteAsync(new(runId, flow, input, context.PrincipalId.ToString("D"),
                    correlationId, context.TenantId, context.WorkspaceId, context.PrincipalId)
                { LogicalScopeRef = KnowledgeOperationalRead.AdministrativeScope(requestContext) }, cancellationToken);
                if (run.State != KnowledgeProjectionState.Succeeded)
                    throw Error("knowledge_projection_transformation_failed",
                        $"Transformation for binding '{binding.Name}' failed: {run.ErrorMessage ?? run.State.ToString()}.");
                var manifest = ParseManifest(run.Output, $"transformation '{binding.Name}'");
                await InWorkspaceAsync(() => artifactReferences.ValidateAsync(context.WorkspaceId, manifest.Artifacts, cancellationToken));
                result.Add(evidence with
                {
                    TransformationFlow = flow,
                    TransformationFlowRunId = run.RunId,
                    PreparedArtifacts = manifest.Artifacts
                });
            }
            catch (KnowledgeProjectionException exception) when (!binding.Required)
            {
                issues.Add(new(binding.Name, exception.Code, exception.Message));
            }
        }
        return (result, issues);
    }

    private async Task<ResolvedKnowledgeFlowBinding> ResolveFlowAsync(
        ResolvedKnowledgeFlowBinding resolved,
        string requiredContract,
        string role,
        CancellationToken cancellationToken)
    {
        try { await flows.ValidatePinnedAsync(resolved, cancellationToken); }
        catch (KnowledgeSourceValidationException exception)
        { throw Error(exception.Code, exception.Message, exception); }
        if (!string.Equals(resolved.Contract, requiredContract, StringComparison.Ordinal))
            throw Error($"knowledge_projection_{role}_contract_invalid",
                $"Flow '{resolved.Namespace}/{resolved.Name}:{resolved.Version}' must declare flow.contract '{requiredContract}'.");
        RequireContract(resolved, role, requiredContract);
        return resolved;
    }

    private async Task<StoredResource<KnowledgeSnapshotResource>> PublishSnapshotAsync(
        KnowledgeSourceResource source,
        KnowledgeProjectionResource projection,
        KnowledgeProjectionManifest manifest,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var ids = manifest.Artifacts.Where(value => value.Kind != KnowledgeArtifactKind.Inline).Select(value => value.ArtifactId).ToArray();
        var evidence = await InWorkspaceAsync(() => artifacts.ResolveAsync(context.WorkspaceId, ids, cancellationToken));
        var byId = manifest.Artifacts.ToDictionary(value => value.ArtifactId, StringComparer.Ordinal);
        IReadOnlyList<KnowledgeSnapshotArtifact> normalized = evidence.Select(value =>
        {
            var declared = byId[value.ArtifactId];
            if (!string.IsNullOrWhiteSpace(declared.Digest) && !SameDigest(declared.Digest, value.Sha256))
                throw Error("knowledge_projection_artifact_integrity_invalid",
                    $"Artifact '{value.ArtifactId}' does not match its declared digest.");
            return new KnowledgeSnapshotArtifact
            {
                ArtifactId = value.ArtifactId,
                ProducerFlowRunId = value.ProducerFlowRunId,
                ProducerFlowStepId = value.ProducerFlowStepId,
                StorageFlowRunId = value.StorageFlowRunId,
                MediaType = value.MediaType,
                Length = value.Length,
                Sha256 = value.Sha256,
                Provenance = new Dictionary<string, string>(value.Provenance, StringComparer.Ordinal)
            };
        }).ToArray();
        if (projection.Output is not null)
            normalized = await outputPublisher.PublishAsync(source, projection, manifest, cancellationToken);
        else if (manifest.Artifacts.Any(value => value.Kind == KnowledgeArtifactKind.Inline))
            throw Error("knowledge_output_required", "Inline content requires a retained output Storage.");
        var hash = Hash(JsonSerializer.SerializeToElement(new
        {
            projection = new { projection.Uid, projection.Name, projection.ProjectionFlowRunId },
            source = new { source.Uid, source.Generation },
            inputs = projection.Inputs,
            flow = new { projection.ProjectionFlow.Name, projection.ProjectionFlow.Namespace, projection.ProjectionFlow.Version },
            artifacts = normalized.Select(value => new { value.ArtifactId, value.Sha256 }).ToArray()
        }));
        var name = $"snapshot-{hash[..32]}";
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var address = ScopedResourceAddress.Create(scopeRef, source.Namespace, KnowledgeResourceKinds.KnowledgeSnapshot, name);
        var existing = await InWorkspaceAsync(() => store.GetExactAsync<KnowledgeSnapshotResource>(address, cancellationToken));
        if (existing is not null) return existing;
        var snapshot = new KnowledgeSnapshotResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSnapshot,
            Metadata = new() { Name = name, Namespace = source.Namespace },
            ScopeRef = scopeRef,
            Generation = 1,
            Status = new() { ProvisioningState = ProvisioningState.Succeeded },
            KnowledgeSourceUid = source.Uid,
            KnowledgeSourceScopeRef = source.ScopeRef,
            KnowledgeSourceName = source.Name,
            KnowledgeSourceNamespace = source.Namespace,
            KnowledgeSourceGeneration = source.Generation,
            RequestHash = hash,
            PublishedAt = timeProvider.GetUtcNow(),
            PublishedBy = context.PrincipalId,
            ProjectionId = projection.Name,
            ProjectionUid = projection.Uid,
            ProjectionFlow = projection.ProjectionFlow,
            ProjectionFlowRunId = projection.ProjectionFlowRunId,
            ProjectionInputs = projection.Inputs.Select(value => value with
            {
                AcquiredArtifacts = value.AcquiredArtifacts.Select(item => item with { ContentBase64 = null }).ToArray(),
                PreparedArtifacts = value.PreparedArtifacts.Select(item => item with { ContentBase64 = null }).ToArray()
            }).ToArray(),
            RetrievalFlow = projection.RetrievalFlow,
            Artifacts = normalized
        };
        try { return await InWorkspaceAsync(() => store.CreateImmutableAsync(snapshot, cancellationToken)); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Error("knowledge_projection_publication_pending",
                "The sealed output is retained; Snapshot creation will resume with the same request identity.", exception);
        }
    }

    private async Task SaveObservedAsync(
        ResourceScopeRef scopeRef,
        KnowledgeSourceResource source,
        KnowledgeSnapshotResource snapshot,
        CancellationToken cancellationToken)
    {
        var name = $"snapshot-state-{source.Uid:N}";
        var address = ScopedResourceAddress.Create(scopeRef, source.Namespace,
            KnowledgeResourceKinds.KnowledgeSnapshotObservedState, name);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await InWorkspaceAsync(() => store.GetExactAsync<KnowledgeSnapshotObservedResource>(address, cancellationToken));
            if (current?.Value.LastProjectionId is { } priorId && priorId != snapshot.ProjectionId)
            {
                var prior = await InWorkspaceAsync(() => store.GetExactAsync<KnowledgeProjectionResource>(
                    ScopedResourceAddress.Create(scopeRef, source.Namespace, KnowledgeResourceKinds.KnowledgeProjection, priorId), cancellationToken));
                var incoming = await InWorkspaceAsync(() => store.GetExactAsync<KnowledgeProjectionResource>(
                    ScopedResourceAddress.Create(scopeRef, source.Namespace, KnowledgeResourceKinds.KnowledgeProjection, snapshot.ProjectionId), cancellationToken));
                if (prior is not null && incoming is not null && (prior.Value.CreatedAt > incoming.Value.CreatedAt
                    || prior.Value.CreatedAt == incoming.Value.CreatedAt && string.CompareOrdinal(prior.Value.Name, incoming.Value.Name) > 0))
                    return; // A late completion cannot displace a newer accepted projection.
            }
            var observed = new KnowledgeSnapshotObservedResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSnapshotObservedState,
                Metadata = new() { Name = name, Namespace = source.Namespace },
                ScopeRef = scopeRef,
                Uid = current?.Value.Uid ?? Guid.Empty,
                Generation = current is null ? 1 : checked(current.Value.Generation + 1),
                Status = new() { ProvisioningState = ProvisioningState.Succeeded },
                KnowledgeSourceUid = source.Uid,
                ActiveSnapshotName = snapshot.Name,
                ActiveSnapshotUid = snapshot.Uid,
                LastProjectionId = snapshot.ProjectionId,
                LastPublishedAt = snapshot.PublishedAt,
                LastAttemptAt = timeProvider.GetUtcNow()
            };
            try
            {
                _ = await InWorkspaceAsync(() => store.PutExactAsync(scopeRef, observed, current?.ETag, current is null, cancellationToken));
                return;
            }
            catch (ResourceConcurrencyException) when (attempt < 2) { }
        }
        throw Error("knowledge_projection_activation_conflict", "The active Knowledge Snapshot changed concurrently.");
    }

    private static KnowledgeProjectionManifest ParseManifest(JsonElement? output, string role)
    {
        if (output is null || output.Value.ValueKind != JsonValueKind.Object
            || !output.Value.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
            throw Error("knowledge_projection_manifest_invalid", $"The {role} Flow output must contain an artifacts array.");
        var entries = artifacts.EnumerateArray().ToArray();
        if (entries.Length is < 1 or > MaximumArtifacts)
            throw Error("knowledge_projection_manifest_invalid", $"The {role} Flow must emit 1 to {MaximumArtifacts} artifacts.");
        var result = new List<KnowledgeProjectionArtifact>(entries.Length);
        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("artifactId", out var id) || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString()))
                throw Error("knowledge_projection_manifest_invalid", $"Every {role} artifact requires artifactId.");
            var kind = ParseEnum<KnowledgeArtifactKind>(entry, "kind", role);
            var disposition = ParseEnum<KnowledgeArtifactDisposition>(entry, "disposition", role);
            result.Add(new()
            {
                ArtifactId = id.GetString()!,
                Kind = kind,
                Disposition = disposition,
                Name = Optional(entry, "name"),
                MediaType = Optional(entry, "mediaType"),
                Digest = Optional(entry, "digest"),
                ContentBase64 = Optional(entry, "contentBase64")
            });
        }
        if (result.Select(value => value.ArtifactId).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw Error("knowledge_projection_manifest_invalid", "Output content identities must be unique.");
        return new() { Artifacts = result };
    }

    private static T ParseEnum<T>(JsonElement value, string name, string role) where T : struct
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            && Enum.TryParse<T>(property.GetString(), true, out var parsed)
            ? parsed
            : throw Error("knowledge_projection_manifest_invalid", $"Every {role} artifact requires a valid {name}.");
    private static string? Optional(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static JsonSerializerOptions CreateFlowContractJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static void RequireContract(
        ResolvedKnowledgeFlowBinding flow,
        string role,
        string requiredContract)
    {
        RequireObject(flow.InputSchema, role, "input");
        RequireObject(flow.OutputSchema, role, "output");
        var requiredOutputs = requiredContract == KnowledgeFlowContracts.Retrieval
            ? new[] { "items", "citations" }
            : ["artifacts"];
        if (flow.OutputSchema is not { } output)
            throw Error("knowledge_projection_contract_invalid", $"The {role} Flow output schema is required.");
        foreach (var property in requiredOutputs)
            if (!HasRequiredProperty(output, property))
                throw Error("knowledge_projection_contract_invalid",
                    $"The {role} Flow output schema must require '{property}'.");
    }

    private static void RequireObject(JsonElement? schema, string role, string direction)
    {
        if (schema is not { ValueKind: JsonValueKind.Object }
            || !schema.Value.TryGetProperty("type", out var type) || type.GetString() != "object")
            throw Error("knowledge_projection_contract_invalid", $"The {role} Flow {direction} schema must be an object.");
    }

    private static bool HasRequiredProperty(JsonElement schema, string name) =>
        schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty(name, out _)
        && schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
        && required.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == name);

    private static void ValidateParameters(JsonElement parameters)
    {
        if (parameters.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
            throw Error("knowledge_projection_parameters_invalid", "Projection parameters must be a JSON object.");
        if (Encoding.UTF8.GetByteCount(parameters.GetRawText()) > MaximumParametersBytes)
            throw Error("knowledge_projection_parameters_too_large", $"Projection parameters cannot exceed {MaximumParametersBytes} bytes.");
    }

    private Task InWorkspaceAsync(Func<Task> action) => InWorkspaceAsync(async () => { await action(); return true; });

    private async Task<T> InWorkspaceAsync<T>(Func<Task<T>> action)
    {
        if (requestContext.AccessMode == ControlPlaneAccessMode.System) return await action();
        using var workspace = requestScopes.Push(RequireContext());
        return await action();
    }

    private RequestContext RequireContext() => KnowledgeOperationalRead.AdministrativeContext(requestContext) ?? (requestContext.IsInitialized
        ? requestContext.Current
        : throw Error("knowledge_projection_context_required", "A Workspace request context is required."));
    private static bool SameDigest(string left, string right) => string.Equals(
        left.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase),
        right.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    private static string Hash(JsonElement value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));
    private static KnowledgeProjectionException Error(string code, string message, Exception? inner = null) =>
        new(code, message, inner);
}
