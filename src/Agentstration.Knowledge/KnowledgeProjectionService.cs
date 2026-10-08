using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.Knowledge;

public sealed class KnowledgeProjectionException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public interface IKnowledgeProjectionInputResolver
{
    Task<KnowledgeDataSourceBindingReadiness> GetReadinessAsync(
        ResourceScopeRef executionScope,
        ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding,
        CancellationToken cancellationToken);

    Task<KnowledgeProjectionInputEvidence> ResolveAsync(
        ResourceScopeRef executionScope,
        ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding,
        string? acquisitionId,
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
    Guid PrincipalId);

public sealed record KnowledgeProjectionFlowRunResult(
    string RunId,
    KnowledgeAcquisitionState State,
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
    IAuthorizationService authorization,
    KnowledgeSourceManagementService sources,
    IKnowledgeFlowResolver flows,
    IKnowledgeProjectionInputResolver inputs,
    IKnowledgeProjectionFlowGateway flowRuns,
    IKnowledgeArtifactReferenceValidator artifactReferences,
    IKnowledgeSnapshotArtifactResolver artifacts,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions FlowContractJsonOptions = CreateFlowContractJsonOptions();
    private readonly ConcurrentDictionary<(Guid WorkspaceId, Guid SourceUid), SemaphoreSlim> gates = new();

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
        var source = await sources.GetAsync(sourceId, cancellationToken)
            ?? throw Error("knowledge_projection_source_not_found", $"KnowledgeSource '{sourceId}' was not found.");
        if (!source.Value.Definition.Enabled)
            throw Error("knowledge_projection_source_disabled", $"KnowledgeSource '{source.Value.Address}' is disabled.");
        if (source.Value.Definition.DataSources.Count is < 1 or > MaximumInputs)
            throw Error("knowledge_projection_inputs_invalid", $"A KnowledgeSource requires 1 to {MaximumInputs} Data Source bindings.");
        if (source.Value.Definition.ProjectionFlow is null)
            throw Error("knowledge_projection_flow_required", "A KnowledgeSource projection Flow is required.");
        if (source.Value.Definition.RetrievalFlow is null)
            throw Error("knowledge_retrieval_flow_required", "A KnowledgeSource retrieval Flow is required.");

        var gate = gates.GetOrAdd((context.WorkspaceId, source.Value.Uid), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
            var projectionFlow = await ResolveFlowAsync(source.Value, source.Value.Definition.ProjectionFlow,
                KnowledgeFlowContracts.Projection, "projection", cancellationToken);
            var retrievalFlow = await ResolveFlowAsync(source.Value, source.Value.Definition.RetrievalFlow,
                KnowledgeFlowContracts.Retrieval, "retrieval", cancellationToken);
            var projectionId = $"projection-{Guid.NewGuid():N}";
            var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? projectionId : request.CorrelationId.Trim();
            var resolvedInputs = new List<KnowledgeProjectionInputEvidence>(source.Value.Definition.DataSources.Count);
            var inputIssues = new List<KnowledgeProjectionInputIssue>();
            foreach (var binding in source.Value.Definition.DataSources)
            {
                request.AcquisitionIds.TryGetValue(binding.Name, out var acquisitionId);
                try
                {
                    resolvedInputs.Add(await inputs.ResolveAsync(scopeRef, source.Value.Namespace, binding,
                        acquisitionId, cancellationToken));
                }
                catch (KnowledgeProjectionException exception) when (!binding.Required)
                {
                    inputIssues.Add(new(binding.Name, exception.Code, exception.Message));
                }
            }
            if (resolvedInputs.Count == 0)
                throw Error("knowledge_projection_no_ready_input", "No configured Data Source input is ready for projection.");

            var resource = new KnowledgeProjectionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeProjection,
                Metadata = new() { Name = projectionId, Namespace = source.Value.Namespace },
                ScopeRef = scopeRef,
                Generation = 1,
                Status = new() { ProvisioningState = ProvisioningState.Accepted },
                KnowledgeSourceUid = source.Value.Uid,
                KnowledgeSourceName = source.Value.Name,
                KnowledgeSourceNamespace = source.Value.Namespace,
                KnowledgeSourceGeneration = source.Value.Generation,
                ProjectionFlow = projectionFlow,
                RetrievalFlow = retrievalFlow,
                Inputs = resolvedInputs,
                InputIssues = inputIssues,
                ProjectionFlowRunId = $"flowrun-knowledge-{projectionId[11..]}",
                State = KnowledgeAcquisitionState.Running,
                CorrelationId = correlationId,
                Parameters = request.Parameters.Clone(),
                CreatedBy = context.PrincipalId,
                TenantId = context.TenantId,
                WorkspaceId = context.WorkspaceId,
                CreatedAt = timeProvider.GetUtcNow()
            };
            var stored = await scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.RunsExecute,
                token => store.PutExactAsync(scopeRef, resource, null, true, token), cancellationToken);

            try
            {
                var transformation = await TransformAsync(source.Value, resolvedInputs, correlationId, context, cancellationToken);
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
                    context.PrincipalId), cancellationToken);
                if (run.State != KnowledgeAcquisitionState.Succeeded)
                    throw Error(run.ErrorCode ?? "knowledge_projection_flow_failed",
                        run.ErrorMessage ?? $"Projection Flow ended with state '{run.State}'.");
                var manifest = ParseManifest(run.Output, "projection");
                if (manifest.Artifacts.Any(value => value.Kind != KnowledgeArtifactKind.Durable
                    || value.Disposition != KnowledgeArtifactDisposition.Publishable))
                    throw Error("knowledge_projection_output_not_publishable",
                        "Every projected Snapshot output must be a durable publishable Artifact.");
                var snapshot = await PublishSnapshotAsync(source.Value, stored.Value with { Inputs = prepared },
                    manifest, context, cancellationToken);
                var completed = stored.Value with
                {
                    Generation = 2,
                    Inputs = prepared,
                    InputIssues = inputIssues,
                    State = KnowledgeAcquisitionState.Succeeded,
                    CompletedAt = run.CompletedAt ?? timeProvider.GetUtcNow(),
                    Manifest = manifest,
                    SnapshotName = snapshot.Value.Name,
                    Status = new() { ProvisioningState = ProvisioningState.Succeeded }
                };
                var result = await store.PutExactAsync(scopeRef, completed, stored.ETag, false, cancellationToken);
                await audit.WriteAsync(new(SecurityAuditActions.KnowledgeSnapshotPublished,
                    ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId,
                    WorkspaceId: context.WorkspaceId, ReasonCode: projectionId), cancellationToken);
                return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var mapped = exception as KnowledgeProjectionException
                    ?? Error("knowledge_projection_failed", exception.Message, exception);
                var failed = stored.Value with
                {
                    Generation = 2,
                    State = KnowledgeAcquisitionState.Failed,
                    CompletedAt = timeProvider.GetUtcNow(),
                    ErrorCode = mapped.Code,
                    ErrorMessage = mapped.Message,
                    Status = new() { ProvisioningState = ProvisioningState.Failed }
                };
                try { _ = await store.PutExactAsync(scopeRef, failed, stored.ETag, false, cancellationToken); }
                catch (ResourceConcurrencyException) { }
                throw mapped;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<KnowledgeProjectionResource>> ListAsync(
        KnowledgeSourceId? sourceId,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        var values = await store.ListExactAsync<KnowledgeProjectionResource>(ResourceScopeRef.Workspace(context.WorkspaceId),
            KnowledgeResourceKinds.KnowledgeProjection, 0, 1000, cancellationToken);
        return values.Select(value => value.Value)
            .Where(value => sourceId is null || value.KnowledgeSourceName == sourceId.Value.Value
                && value.KnowledgeSourceNamespace == sourceId.Value.Namespace)
            .OrderByDescending(value => value.CreatedAt).ToArray();
    }

    public async Task<StoredResource<KnowledgeProjectionResource>> GetAsync(
        string id,
        ResourceNamespace @namespace,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        return await store.GetExactAsync<KnowledgeProjectionResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Workspace(context.WorkspaceId), @namespace, KnowledgeResourceKinds.KnowledgeProjection, id),
            cancellationToken) ?? throw Error("knowledge_projection_not_found", $"Knowledge projection '{id}' was not found.");
    }

    private async Task<(List<KnowledgeProjectionInputEvidence> Inputs, List<KnowledgeProjectionInputIssue> Issues)> TransformAsync(
        KnowledgeSourceResource source,
        IReadOnlyList<KnowledgeProjectionInputEvidence> resolved,
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
                var flow = await ResolveFlowAsync(source, binding.TransformationFlow,
                    KnowledgeFlowContracts.ArtifactTransformation, "transformation", cancellationToken);
                var runId = $"flowrun-transform-{Guid.NewGuid():N}";
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
                    correlationId, context.TenantId, context.WorkspaceId, context.PrincipalId), cancellationToken);
                if (run.State != KnowledgeAcquisitionState.Succeeded)
                    throw Error("knowledge_projection_transformation_failed",
                        $"Transformation for binding '{binding.Name}' failed: {run.ErrorMessage ?? run.State.ToString()}.");
                var manifest = ParseManifest(run.Output, $"transformation '{binding.Name}'");
                try
                {
                    await artifactReferences.ValidateAsync(context.WorkspaceId, manifest.Artifacts, cancellationToken);
                }
                catch (KnowledgeAcquisitionException exception)
                {
                    throw Error(exception.Code, exception.Message, exception);
                }
                result.Add(evidence with
                {
                    TransformationFlow = flow,
                    TransformationFlowRunId = run.RunId,
                    PreparedArtifacts = manifest.Artifacts.Select(ToProjectionArtifact).ToArray()
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
        KnowledgeSourceResource source,
        KnowledgeFlowTarget target,
        string requiredContract,
        string role,
        CancellationToken cancellationToken)
    {
        var resolved = await flows.ResolveAsync(source.ScopeRef!.Value, source.Namespace, target, cancellationToken);
        if (!string.Equals(resolved.Contract, requiredContract, StringComparison.Ordinal))
            throw Error($"knowledge_projection_{role}_contract_invalid",
                $"Flow '{resolved.Namespace}/{resolved.Name}:{resolved.Version}' must declare flow.contract '{requiredContract}'.");
        RequireContract(resolved, role, requiredContract);
        return resolved;
    }

    private async Task<StoredResource<KnowledgeSnapshotResource>> PublishSnapshotAsync(
        KnowledgeSourceResource source,
        KnowledgeProjectionResource projection,
        KnowledgeAcquisitionManifest manifest,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var ids = manifest.Artifacts.Select(value => value.ArtifactId).ToArray();
        var evidence = await artifacts.ResolveAsync(context.WorkspaceId, ids, cancellationToken);
        var byId = manifest.Artifacts.ToDictionary(value => value.ArtifactId, StringComparer.Ordinal);
        var normalized = evidence.Select(value =>
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
        var hash = Hash(JsonSerializer.SerializeToElement(new
        {
            source = new { source.Uid, source.Generation },
            inputs = projection.Inputs,
            flow = new { projection.ProjectionFlow.Name, projection.ProjectionFlow.Namespace, projection.ProjectionFlow.Version },
            artifacts = normalized.Select(value => new { value.ArtifactId, value.Sha256 }).ToArray()
        }));
        var name = $"snapshot-{hash[..32]}";
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var address = ScopedResourceAddress.Create(scopeRef, source.Namespace, KnowledgeResourceKinds.KnowledgeSnapshot, name);
        var existing = await store.GetExactAsync<KnowledgeSnapshotResource>(address, cancellationToken);
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
            KnowledgeSourceName = source.Name,
            KnowledgeSourceNamespace = source.Namespace,
            KnowledgeSourceGeneration = source.Generation,
            AcquisitionId = projection.Name,
            AcquisitionUid = projection.Uid,
            AcquiredAt = projection.Inputs.Max(value => value.AcquiredAt),
            IngestionFlow = projection.ProjectionFlow,
            IngestionFlowRunId = projection.ProjectionFlowRunId,
            PublicationId = projection.Name,
            RequestHash = hash,
            PublishedAt = timeProvider.GetUtcNow(),
            PublishedBy = context.PrincipalId,
            ProjectionId = projection.Name,
            ProjectionUid = projection.Uid,
            ProjectionFlow = projection.ProjectionFlow,
            ProjectionFlowRunId = projection.ProjectionFlowRunId,
            ProjectionInputs = projection.Inputs,
            RetrievalFlow = projection.RetrievalFlow,
            Artifacts = normalized
        };
        var stored = await store.CreateImmutableAsync(snapshot, cancellationToken);
        await SaveObservedAsync(scopeRef, source, stored.Value, cancellationToken);
        return stored;
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
            var current = await store.GetExactAsync<KnowledgeSnapshotObservedResource>(address, cancellationToken);
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
                LastPublicationId = projectionPublication(snapshot),
                LastPublishedAt = snapshot.PublishedAt,
                LastAttemptAt = timeProvider.GetUtcNow()
            };
            try
            {
                _ = await store.PutExactAsync(scopeRef, observed, current?.ETag, current is null, cancellationToken);
                return;
            }
            catch (ResourceConcurrencyException) when (attempt < 2) { }
        }
        throw Error("knowledge_projection_activation_conflict", "The active Knowledge Snapshot changed concurrently.");

        static string projectionPublication(KnowledgeSnapshotResource value) => value.ProjectionId ?? value.PublicationId;
    }

    private static KnowledgeAcquisitionManifest ParseManifest(JsonElement? output, string role)
    {
        if (output is null || output.Value.ValueKind != JsonValueKind.Object
            || !output.Value.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
            throw Error("knowledge_projection_manifest_invalid", $"The {role} Flow output must contain an artifacts array.");
        var entries = artifacts.EnumerateArray().ToArray();
        if (entries.Length is < 1 or > MaximumArtifacts)
            throw Error("knowledge_projection_manifest_invalid", $"The {role} Flow must emit 1 to {MaximumArtifacts} artifacts.");
        var result = new List<KnowledgeAcquisitionArtifact>(entries.Length);
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
                ArtifactId = id.GetString()!, Kind = kind, Disposition = disposition,
                Name = Optional(entry, "name"), MediaType = Optional(entry, "mediaType"), Digest = Optional(entry, "digest")
            });
        }
        return new() { Artifacts = result };
    }

    private static T ParseEnum<T>(JsonElement value, string name, string role) where T : struct
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            && Enum.TryParse<T>(property.GetString(), true, out var parsed)
            ? parsed
            : throw Error("knowledge_projection_manifest_invalid", $"Every {role} artifact requires a valid {name}.");
    private static string? Optional(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static KnowledgeProjectionArtifact ToProjectionArtifact(KnowledgeAcquisitionArtifact value) => new()
    {
        ArtifactId = value.ArtifactId, Kind = value.Kind, Disposition = value.Disposition,
        Name = value.Name, MediaType = value.MediaType, Digest = value.Digest
    };

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

    private RequestContext RequireContext() => requestContext.IsInitialized
        ? requestContext.Current
        : throw Error("knowledge_projection_context_required", "A Workspace request context is required.");
    private static bool SameDigest(string left, string right) => string.Equals(
        left.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase),
        right.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    private static string Hash(JsonElement value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));
    private static KnowledgeProjectionException Error(string code, string message, Exception? inner = null) =>
        new(code, message, inner);
}
