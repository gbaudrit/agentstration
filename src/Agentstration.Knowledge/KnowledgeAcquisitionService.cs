using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.Knowledge;

public sealed class KnowledgeAcquisitionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record KnowledgeFlowRunRequest(
    string RunId,
    ResolvedKnowledgeFlowBinding Flow,
    JsonElement Input,
    string CallerId,
    string CorrelationId,
    string? IdempotencyKey,
    Guid TenantId,
    Guid WorkspaceId,
    Guid PrincipalId);

public sealed record KnowledgeFlowRunSnapshot(
    string RunId,
    KnowledgeAcquisitionState State,
    JsonElement? Output,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? CompletedAt);

public interface IKnowledgeAcquisitionFlowGateway
{
    Task<KnowledgeFlowRunSnapshot> StartAsync(KnowledgeFlowRunRequest request, CancellationToken cancellationToken);
    Task<KnowledgeFlowRunSnapshot?> GetAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId, CancellationToken cancellationToken);
    Task<KnowledgeFlowRunSnapshot> CancelAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId, CancellationToken cancellationToken);
}

public interface IKnowledgeArtifactReferenceValidator
{
    Task ValidateAsync(Guid workspaceId, IReadOnlyList<KnowledgeAcquisitionArtifact> artifacts, CancellationToken cancellationToken);
}

public sealed class KnowledgeAcquisitionService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    ISecurityAuditWriter audit,
    IKnowledgeFlowResolver flows,
    IKnowledgeAcquisitionFlowGateway flowRuns,
    IKnowledgeArtifactReferenceValidator artifacts,
    TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<(Guid WorkspaceId, Guid SourceUid), SemaphoreSlim> startGates = new();

    public const int MaximumManifestArtifacts = 100;
    public const int MaximumParametersBytes = 64 * 1024;
    public const int MaximumIdempotencyKeyLength = 200;

    public async Task<StoredResource<KnowledgeAcquisitionResource>> StartAsync(
        KnowledgeSourceId sourceId,
        JsonElement parameters,
        string? correlationId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        ValidateRequest(parameters, idempotencyKey);
        var source = await store.GetExactAsync<KnowledgeSourceResource>(Scoped(
            ResourceScopeRef.Workspace(context.WorkspaceId), KnowledgeResourceKinds.KnowledgeSource,
            sourceId.Namespace, sourceId.Value), cancellationToken)
            ?? throw new KnowledgeSourceNotFoundException(sourceId);
        var gate = startGates.GetOrAdd((context.WorkspaceId, source.Value.Uid), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!source.Value.Definition.Enabled)
                throw Error("knowledge_source_disabled", $"KnowledgeSource '{sourceId}' is disabled.");
            var target = source.Value.Definition.IngestionFlow
                ?? throw Error("knowledge_ingestion_flow_required", $"KnowledgeSource '{sourceId}' has no ingestion Flow.");
            var resolved = await flows.ResolveAsync(source.Value.ScopeRef!.Value, source.Value.Namespace, target, cancellationToken);
            if (!string.Equals(resolved.Contract, KnowledgeFlowContracts.Ingestion, StringComparison.Ordinal))
                throw Error("knowledge_ingestion_contract_required",
                    $"Flow '{resolved.Namespace}/{resolved.Name}:{resolved.Version}' must declare contract '{KnowledgeFlowContracts.Ingestion}'.");
            ValidateContract(resolved);

            var requestHash = Hash(JsonSerializer.SerializeToElement(new
            {
                source = new { source.Value.Uid, source.Value.Generation },
                flow = new { resolved.Name, resolved.Namespace, resolved.Version },
                parameters
            }));
            var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
            var acquisitions = await store.ListExactAsync<KnowledgeAcquisitionResource>(
                scopeRef, KnowledgeResourceKinds.KnowledgeAcquisition, 0, 1000, cancellationToken);
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var existing = acquisitions.SingleOrDefault(item =>
                    item.Value.KnowledgeSourceUid == source.Value.Uid
                    && string.Equals(item.Value.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
                if (existing is not null)
                {
                    if (!string.Equals(existing.Value.RequestHash, requestHash, StringComparison.Ordinal))
                        throw Error("knowledge_acquisition_idempotency_conflict", "The idempotency key is already bound to another acquisition request.");
                    return await SynchronizeAsync(existing, context, cancellationToken);
                }
            }

            foreach (var active in acquisitions.Where(item => item.Value.KnowledgeSourceUid == source.Value.Uid))
            {
                var synchronized = await SynchronizeAsync(active, context, cancellationToken);
                if (!IsTerminal(synchronized.Value.State))
                    throw Error("knowledge_acquisition_already_running", $"Acquisition '{synchronized.Value.Name}' is already active for this KnowledgeSource.");
            }

            var acquisitionId = string.IsNullOrWhiteSpace(idempotencyKey)
                ? $"acquisition-{Guid.NewGuid():N}"
                : $"acquisition-{HashText($"{context.WorkspaceId:D}:{source.Value.Uid:D}:{idempotencyKey}")[..32]}";
            var runId = $"flowrun-knowledge-{acquisitionId[12..]}";
            var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId) ? acquisitionId : correlationId.Trim();
            var input = JsonSerializer.SerializeToElement(new KnowledgeIngestionInput
            {
                KnowledgeSourceId = $"{source.Value.Namespace}/{source.Value.Name}",
                KnowledgeSourceUid = source.Value.Uid,
                KnowledgeSourceGeneration = source.Value.Generation,
                SourceConfiguration = source.Value.Definition.AcquisitionConfiguration.Clone(),
                Parameters = parameters.Clone(),
                Caller = new(context.PrincipalId, context.TenantId, context.WorkspaceId),
                CorrelationId = effectiveCorrelationId,
                AcquisitionId = acquisitionId
            });
            var resource = new KnowledgeAcquisitionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeAcquisition,
                Metadata = new ResourceMetadata { Name = acquisitionId, Namespace = source.Value.Namespace },
                ScopeRef = scopeRef,
                Generation = 1,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Accepted },
                KnowledgeSourceUid = source.Value.Uid,
                KnowledgeSourceName = source.Value.Name,
                KnowledgeSourceNamespace = source.Value.Namespace,
                KnowledgeSourceGeneration = source.Value.Generation,
                IngestionFlow = resolved,
                FlowRunId = runId,
                State = KnowledgeAcquisitionState.Pending,
                CorrelationId = effectiveCorrelationId,
                IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
                RequestHash = requestHash,
                SourceConfiguration = source.Value.Definition.AcquisitionConfiguration.Clone(),
                Parameters = parameters.Clone(),
                CreatedBy = context.PrincipalId,
                TenantId = context.TenantId,
                CreatedAt = timeProvider.GetUtcNow()
            };

            var result = await scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.RunsExecute, async token =>
            {
                StoredResource<KnowledgeAcquisitionResource> stored;
                try { stored = await store.PutExactAsync(scopeRef, resource, null, true, token); }
                catch (ResourceConcurrencyException) when (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    var recovered = (await store.ListExactAsync<KnowledgeAcquisitionResource>(scopeRef,
                        KnowledgeResourceKinds.KnowledgeAcquisition, 0, 1000, token)).SingleOrDefault(item =>
                        item.Value.KnowledgeSourceUid == source.Value.Uid
                        && string.Equals(item.Value.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
                    if (recovered is not null && recovered.Value.RequestHash == requestHash) return recovered;
                    throw;
                }

                try
                {
                    _ = await flowRuns.StartAsync(new(runId, resolved, input, context.PrincipalId.ToString("D"),
                        effectiveCorrelationId, idempotencyKey, context.TenantId, context.WorkspaceId, context.PrincipalId), token);
                    return stored;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var failed = resource with
                    {
                        Generation = 2,
                        State = KnowledgeAcquisitionState.Failed,
                        CompletedAt = timeProvider.GetUtcNow(),
                        ErrorCode = "knowledge_acquisition_start_failed",
                        ErrorMessage = exception.Message,
                        Status = new ResourceStatus { ProvisioningState = ProvisioningState.Failed }
                    };
                    _ = await store.PutExactAsync(scopeRef, failed, stored.ETag, false, token);
                    throw;
                }
            }, cancellationToken);
            await audit.WriteAsync(new(SecurityAuditActions.KnowledgeAcquisitionStarted,
                ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId,
                WorkspaceId: context.WorkspaceId, ReasonCode: result.Value.Name), cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StoredResource<KnowledgeAcquisitionResource>> GetAsync(
        string acquisitionId, ResourceNamespace @namespace, CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        return await GetCoreAsync(acquisitionId, @namespace, context, cancellationToken);
    }

    private async Task<StoredResource<KnowledgeAcquisitionResource>> GetCoreAsync(
        string acquisitionId, ResourceNamespace @namespace, RequestContext context, CancellationToken cancellationToken)
    {
        var stored = await store.GetExactAsync<KnowledgeAcquisitionResource>(Scoped(
            ResourceScopeRef.Workspace(context.WorkspaceId), KnowledgeResourceKinds.KnowledgeAcquisition,
            @namespace, acquisitionId), cancellationToken)
            ?? throw Error("knowledge_acquisition_not_found", $"Knowledge acquisition '{acquisitionId}' was not found.");
        return await SynchronizeAsync(stored, context, cancellationToken);
    }

    public async Task<IReadOnlyList<KnowledgeAcquisitionResource>> ListAsync(
        KnowledgeSourceId? sourceId, CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        var values = await store.ListExactAsync<KnowledgeAcquisitionResource>(ResourceScopeRef.Workspace(context.WorkspaceId),
            KnowledgeResourceKinds.KnowledgeAcquisition, 0, 1000, cancellationToken);
        var filtered = (sourceId is null ? values : values.Where(item =>
            item.Value.KnowledgeSourceName == sourceId.Value.Value
            && item.Value.KnowledgeSourceNamespace == sourceId.Value.Namespace).ToArray()).ToArray();
        var result = new List<KnowledgeAcquisitionResource>(filtered.Length);
        foreach (var item in filtered.OrderByDescending(value => value.Value.CreatedAt))
            result.Add((await SynchronizeAsync(item, context, cancellationToken)).Value);
        return result;
    }

    public async Task<StoredResource<KnowledgeAcquisitionResource>> CancelAsync(
        string acquisitionId, ResourceNamespace @namespace, CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken);
        var stored = await GetCoreAsync(acquisitionId, @namespace, context, cancellationToken);
        if (!IsTerminal(stored.Value.State))
            _ = await flowRuns.CancelAsync(context.WorkspaceId, stored.Value.FlowRunId,
                context.TenantId, context.PrincipalId, cancellationToken);
        var result = await SynchronizeAsync(stored, context, cancellationToken);
        await audit.WriteAsync(new(SecurityAuditActions.KnowledgeAcquisitionCancelled,
            ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId,
            WorkspaceId: context.WorkspaceId, ReasonCode: result.Value.Name), cancellationToken);
        return result;
    }

    public async Task<StoredResource<KnowledgeAcquisitionResource>> RetryAsync(
        string acquisitionId, ResourceNamespace @namespace, string? correlationId, CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken);
        var original = await GetCoreAsync(acquisitionId, @namespace, context, cancellationToken);
        if (!IsTerminal(original.Value.State))
            throw Error("knowledge_acquisition_retry_not_terminal", "Only a terminal acquisition can be retried.");
        var retried = await StartAsync(new(original.Value.KnowledgeSourceName, original.Value.KnowledgeSourceNamespace),
            original.Value.Parameters, correlationId, null, cancellationToken);
        var updated = retried.Value with { RetriedFrom = original.Value.Name, Attempt = checked(original.Value.Attempt + 1), Generation = 2 };
        var result = await store.PutExactAsync(updated.ScopeRef!.Value, updated, retried.ETag, false, cancellationToken);
        await audit.WriteAsync(new(SecurityAuditActions.KnowledgeAcquisitionRetried,
            ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId,
            WorkspaceId: context.WorkspaceId, ReasonCode: original.Value.Name), cancellationToken);
        return result;
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        var pending = (await store.ListAllAsync<KnowledgeAcquisitionResource>(
            KnowledgeResourceKinds.KnowledgeAcquisition, cancellationToken))
            .Where(item => item.Value.State == KnowledgeAcquisitionState.Pending)
            .ToArray();
        foreach (var stored in pending)
        {
            var value = stored.Value;
            if (value.ScopeRef is not { Kind: ResourceScopeKind.Workspace, TargetId: { } workspaceId }) continue;
            if (await flowRuns.GetAsync(workspaceId, value.FlowRunId, value.TenantId, value.CreatedBy, cancellationToken) is not null) continue;
            var input = JsonSerializer.SerializeToElement(new KnowledgeIngestionInput
            {
                KnowledgeSourceId = $"{value.KnowledgeSourceNamespace}/{value.KnowledgeSourceName}",
                KnowledgeSourceUid = value.KnowledgeSourceUid,
                KnowledgeSourceGeneration = value.KnowledgeSourceGeneration,
                SourceConfiguration = value.SourceConfiguration.Clone(),
                Parameters = value.Parameters.Clone(),
                Caller = new(value.CreatedBy, value.TenantId, workspaceId),
                CorrelationId = value.CorrelationId,
                AcquisitionId = value.Name
            });
            try
            {
                _ = await flowRuns.StartAsync(new(value.FlowRunId, value.IngestionFlow, input,
                    value.CreatedBy.ToString("D"), value.CorrelationId, value.IdempotencyKey,
                    value.TenantId, workspaceId, value.CreatedBy), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failed = value with
                {
                    Generation = checked(value.Generation + 1),
                    State = KnowledgeAcquisitionState.Failed,
                    CompletedAt = timeProvider.GetUtcNow(),
                    ErrorCode = "knowledge_acquisition_recovery_failed",
                    ErrorMessage = exception.Message,
                    Status = new ResourceStatus { ProvisioningState = ProvisioningState.Failed }
                };
                try { _ = await store.PutExactAsync(value.ScopeRef.Value, failed, stored.ETag, false, cancellationToken); }
                catch (ResourceConcurrencyException) { }
            }
        }
    }

    private async Task<StoredResource<KnowledgeAcquisitionResource>> SynchronizeAsync(
        StoredResource<KnowledgeAcquisitionResource> stored,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var run = await flowRuns.GetAsync(context.WorkspaceId, stored.Value.FlowRunId,
            context.TenantId, context.PrincipalId, cancellationToken);
        if (run is null || run.State == stored.Value.State && (!IsTerminal(run.State) || stored.Value.CompletedAt is not null))
            return stored;
        KnowledgeAcquisitionManifest? manifest = stored.Value.Manifest;
        string? errorCode = run.ErrorCode;
        string? errorMessage = run.ErrorMessage;
        var state = run.State;
        if (state == KnowledgeAcquisitionState.Succeeded)
        {
            try
            {
                manifest = ParseManifest(run.Output);
                await artifacts.ValidateAsync(context.WorkspaceId, manifest.Artifacts, cancellationToken);
            }
            catch (KnowledgeAcquisitionException exception)
            {
                state = KnowledgeAcquisitionState.Failed;
                errorCode = exception.Code;
                errorMessage = exception.Message;
            }
        }
        var updated = stored.Value with
        {
            Generation = checked(stored.Value.Generation + 1),
            State = state,
            CompletedAt = IsTerminal(state) ? run.CompletedAt ?? timeProvider.GetUtcNow() : null,
            Manifest = manifest,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            Status = new ResourceStatus
            {
                ProvisioningState = state switch
                {
                    KnowledgeAcquisitionState.Succeeded => ProvisioningState.Succeeded,
                    KnowledgeAcquisitionState.Failed or KnowledgeAcquisitionState.Cancelled or KnowledgeAcquisitionState.TimedOut => ProvisioningState.Failed,
                    _ => ProvisioningState.Accepted
                }
            }
        };
        try { return await store.PutExactAsync(updated.ScopeRef!.Value, updated, stored.ETag, false, cancellationToken); }
        catch (ResourceConcurrencyException)
        {
            return await store.GetExactAsync<KnowledgeAcquisitionResource>(Scoped(updated.ScopeRef!.Value, updated.Kind,
                updated.Namespace, updated.Name), cancellationToken)
                ?? throw new KnowledgeAcquisitionException("knowledge_acquisition_not_found",
                    $"Knowledge acquisition '{updated.Name}' was not found after a concurrent update.");
        }
    }

    private static KnowledgeAcquisitionManifest ParseManifest(JsonElement? output)
    {
        if (output is null || output.Value.ValueKind != JsonValueKind.Object
            || !output.Value.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
            throw Error("knowledge_ingestion_manifest_invalid", "The ingestion Flow output must contain an artifacts array.");
        var values = artifacts.EnumerateArray().ToArray();
        if (values.Length > MaximumManifestArtifacts)
            throw Error("knowledge_ingestion_manifest_too_large", $"The ingestion manifest cannot contain more than {MaximumManifestArtifacts} artifacts.");
        var parsed = new List<KnowledgeAcquisitionArtifact>(values.Length);
        foreach (var value in values)
        {
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("artifactId", out var id) || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString())
                || !value.TryGetProperty("disposition", out var disposition) || disposition.ValueKind != JsonValueKind.String
                || !Enum.TryParse<KnowledgeArtifactDisposition>(disposition.GetString(), true, out var parsedDisposition))
                throw Error("knowledge_ingestion_manifest_invalid", "Each manifest item requires artifactId and a valid disposition.");
            parsed.Add(new KnowledgeAcquisitionArtifact
            {
                ArtifactId = id.GetString()!,
                Kind = value.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                    && Enum.TryParse<KnowledgeArtifactKind>(kind.GetString(), true, out var parsedKind)
                        ? parsedKind
                        : throw Error("knowledge_ingestion_manifest_invalid", "Each manifest item requires a valid artifact kind."),
                Disposition = parsedDisposition,
                Name = OptionalString(value, "name"),
                MediaType = OptionalString(value, "mediaType"),
                Digest = OptionalString(value, "digest")
            });
        }
        return new() { Artifacts = parsed };
    }

    private static string? OptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static void ValidateRequest(JsonElement parameters, string? idempotencyKey)
    {
        if (parameters.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
            throw Error("knowledge_acquisition_parameters_invalid", "Acquisition parameters must be a JSON object.");
        if (Encoding.UTF8.GetByteCount(parameters.GetRawText()) > MaximumParametersBytes)
            throw Error("knowledge_acquisition_parameters_too_large", $"Acquisition parameters cannot exceed {MaximumParametersBytes} bytes.");
        if (idempotencyKey?.Length > MaximumIdempotencyKeyLength)
            throw Error("knowledge_acquisition_idempotency_key_too_long", $"Idempotency keys cannot exceed {MaximumIdempotencyKeyLength} characters.");
    }

    private static void ValidateContract(ResolvedKnowledgeFlowBinding flow)
    {
        RequireProperties(flow.InputSchema,
            ["knowledgeSourceId", "knowledgeSourceUid", "knowledgeSourceGeneration", "sourceConfiguration",
                "parameters", "caller", "correlationId", "acquisitionId"], "input");
        RequireProperties(flow.OutputSchema, ["artifacts"], "output");
    }

    private static void RequireProperties(JsonElement? schema, IReadOnlyList<string> names, string direction)
    {
        if (schema is not { ValueKind: JsonValueKind.Object }
            || !schema.Value.TryGetProperty("type", out var type) || type.GetString() != "object"
            || !schema.Value.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object
            || !schema.Value.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
            throw Error("knowledge_ingestion_contract_invalid",
                $"The {KnowledgeFlowContracts.Ingestion} {direction} schema must be an object with properties and required arrays.");
        var requiredNames = required.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var name in names)
            if (!properties.TryGetProperty(name, out _) || !requiredNames.Contains(name))
                throw Error("knowledge_ingestion_contract_invalid",
                    $"The {KnowledgeFlowContracts.Ingestion} {direction} schema must require property '{name}'.");
    }

    private RequestContext RequireContext() => requestContext.IsInitialized
        ? requestContext.Current
        : throw Error("knowledge_acquisition_context_required", "A Workspace request context is required.");
    private static bool IsTerminal(KnowledgeAcquisitionState state) => state is KnowledgeAcquisitionState.Succeeded
        or KnowledgeAcquisitionState.Failed or KnowledgeAcquisitionState.Cancelled or KnowledgeAcquisitionState.TimedOut;
    private static string Hash(JsonElement value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));
    private static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ScopedResourceAddress Scoped(ResourceScopeRef scope, string kind, ResourceNamespace ns, string name) =>
        ScopedResourceAddress.Create(scope, ns, kind, name);
    private static KnowledgeAcquisitionException Error(string code, string message) => new(code, message);
}
