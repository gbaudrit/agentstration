using Agentstration.Artifacts.Contracts;
using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.DataSources;

public sealed class DataSourceDatasetAcquisitionService(
    IResourceStore store,
    ICurrentRequestContext current,
    IRequestContextScopeFactory scopes,
    IAuthorizationService authorization,
    DataSourceManagementService sources,
    DataSourceDatasetCompositionService compositions,
    IDataSourceAcquisitionFlowGateway flows,
    IStorageContentGateway content,
    TimeProvider clock,
    IStoredArtifactRegistrar artifacts, IDataSourceHistoryMaintenance? history = null,
    DataSourceDocumentStorage? documentStorage = null)
{
    private readonly DataSourceExecutionContext execution = new(current, scopes);
    private readonly SemaphoreSlim admission = new(1, 1);
    public const int MaximumActiveDirectAcquisitions = 32;

    public async Task ValidateInvocationAsync(string runId, JsonElement input, CancellationToken cancellationToken)
    {
        var context = execution.RequireContext("data_source_context_required");
        if (!input.TryGetProperty("acquisitionId", out var id) || id.ValueKind != JsonValueKind.String
            || !input.TryGetProperty("operation", out var operation) || !input.TryGetProperty("parameters", out var parameters))
            throw Invalid("data_source_invocation_invalid", "An operation Flow requires an accepted acquisition identity.");
        var acquisition = await GetExecutionAsync(id.GetString()!, runId, cancellationToken);
        if (acquisition?.Dataset is null || Terminal(acquisition.State)
            || context.PrincipalId != acquisition.CreatedBy || !JsonElement.DeepEquals(parameters, acquisition.Parameters)
            || !JsonElement.DeepEquals(operation, JsonSerializer.SerializeToElement(acquisition.Dataset.Definition.Acquisition.Operation, JsonSerializerOptions.Web))
            || !JsonElement.DeepEquals(input, InvocationInput(acquisition)))
            throw Invalid("data_source_invocation_invalid", "The Flow input does not match the accepted acquisition.");
    }

    public async Task<DataSourceAcquisitionResource> GetExecutionAsync(string id, string runId, CancellationToken cancellationToken)
    {
        var context = execution.RequireContext("data_source_context_required");
        var records = await execution.InWorkspaceAsync(() => store.ListExactAsync<DataSourceAcquisitionResource>(
            ResourceScopeRef.Workspace(context.WorkspaceId), DataSourceResourceKinds.DataSourceAcquisition, 0, 1000, cancellationToken));
        var acquisition = records.SingleOrDefault(item => item.Value.Name == id)?.Value;
        if (acquisition is not null && acquisition.FlowRunId != runId)
        {
            var child = await flows.GetAsync(acquisition.WorkspaceId, runId, acquisition.TenantId, acquisition.CreatedBy, cancellationToken);
            var native = acquisition.Dataset?.OperationFlow;
            if (child?.RootFlowRunId != acquisition.FlowRunId || native is null
                || child.FlowName != native.Name || child.FlowNamespace != native.Namespace
                || child.DefinitionScope != native.ScopeRef || child.DefinitionHash != native.DefinitionHash)
                throw Invalid("data_source_invocation_invalid", "The operation does not belong to this accepted acquisition.");
        }
        if (acquisition?.Dataset is null || Terminal(acquisition.State) || context.PrincipalId != acquisition.CreatedBy
            || acquisition.LogicalScopeRef != execution.ConsumerScope || context.TenantId != acquisition.TenantId)
            throw Invalid("data_source_invocation_invalid", "The invocation does not identify an accepted acquisition in this context.");
        await ValidateExecutionAsync(acquisition, cancellationToken);
        return acquisition;
    }

    public async Task<StoredResource<DataSourceAcquisitionResource>> StartAsync(DataSourceResource source,
        JsonElement callerParameters, string? correlationId, string? idempotencyKey, CancellationToken cancellationToken)
    {
        await admission.WaitAsync(cancellationToken);
        try { return await StartCoreAsync(source, callerParameters, correlationId, idempotencyKey, cancellationToken); }
        finally { admission.Release(); }
    }

    private async Task<StoredResource<DataSourceAcquisitionResource>> StartCoreAsync(DataSourceResource source,
        JsonElement callerParameters, string? correlationId, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var context = execution.RequireContext("data_source_context_required");
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken);
        if (callerParameters.ValueKind != JsonValueKind.Object || callerParameters.EnumerateObject().Any())
            throw Invalid("data_source_parameters_fixed", "Dataset acquisitions use saved parameters; callers cannot override them.");
        var records = await execution.InWorkspaceAsync(() => store.ListExactAsync<DataSourceAcquisitionResource>(
            ResourceScopeRef.Workspace(context.WorkspaceId), DataSourceResourceKinds.DataSourceAcquisition, 0, 10000, cancellationToken));
        if (records.Count >= 10000) throw Invalid("data_source_acquisition_capacity", "The acquisition inventory exceeds the bounded admission scan.");
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var prior = records.SingleOrDefault(item => item.Value.DataSourceUid == source.Uid && item.Value.IdempotencyKey == idempotencyKey);
            if (prior is not null)
            {
                if (prior.Value.DataSourceGeneration != source.Generation || prior.Value.Dataset is null
                    || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(prior.Value.Dataset.Definition), JsonSerializer.SerializeToElement(source.Definition.Dataset))) throw Invalid("data_source_acquisition_idempotency_conflict", "The idempotency key identifies another configuration.");
                return await SynchronizeAsync(prior, cancellationToken);
            }
        }
        var dataset = await compositions.ResolveAsync(source, execution.ConsumerScope, cancellationToken);
        if (dataset.Definition.Storage is null && records.Count(item => item.Value.Dataset is { Definition.Storage: null }
            && !Terminal(item.Value.State)) >= MaximumActiveDirectAcquisitions)
            throw Invalid("data_source_direct_capacity", "The Workspace direct acquisition capacity has been reached.");
        var hash = Digest(JsonSerializer.SerializeToUtf8Bytes(new { source.Uid, source.Generation, dataset }, JsonSerializerOptions.Web));
        foreach (var prior in records.Where(item => dataset.Definition.Storage is not null && item.Value.DataSourceUid == source.Uid))
            if (!Terminal((await SynchronizeAsync(prior, cancellationToken)).Value.State))
                throw Invalid("data_source_acquisition_already_running", "An acquisition is already active for this dataset.");
        var id = string.IsNullOrWhiteSpace(idempotencyKey) ? $"acquisition-{Guid.NewGuid():N}"
            : $"acquisition-{Digest(System.Text.Encoding.UTF8.GetBytes($"{context.WorkspaceId}:{source.Uid}:{idempotencyKey}"))[..32]}";
        var resource = new DataSourceAcquisitionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSourceAcquisition,
            Metadata = new() { Name = id, Namespace = source.Namespace },
            ScopeRef = ResourceScopeRef.Workspace(context.WorkspaceId),
            Generation = 1,
            DataSourceScopeRef = source.ScopeRef!.Value,
            DataSourceUid = source.Uid,
            DataSourceName = source.Name,
            DataSourceNamespace = source.Namespace,
            DataSourceGeneration = source.Generation,
            Dataset = dataset,
            FlowRunId = $"flowrun-dataset-{id[12..]}",
            State = DataSourceAcquisitionState.Pending,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? id : correlationId,
            IdempotencyKey = idempotencyKey,
            RequestHash = hash,
            Parameters = dataset.Definition.Acquisition.Parameters.Clone(),
            CreatedBy = context.PrincipalId,
            TenantId = context.TenantId,
            WorkspaceId = context.WorkspaceId,
            LogicalScopeRef = execution.ConsumerScope,
            CreatedAt = clock.GetUtcNow()
        };
        var stored = await execution.InWorkspaceAsync(() => store.PutExactAsync(resource.ScopeRef.Value, resource, null, true, cancellationToken));
        await EnsureStartedAsync(stored.Value, cancellationToken);
        return stored;
    }

    public async Task EnsureStartedAsync(DataSourceAcquisitionResource acquisition, CancellationToken cancellationToken)
    {
        await ValidateExecutionAsync(acquisition, cancellationToken);
        if (await flows.GetAsync(acquisition.WorkspaceId, acquisition.FlowRunId, acquisition.TenantId, acquisition.CreatedBy, cancellationToken) is not null) return;
        var input = InvocationInput(acquisition);
        _ = await flows.StartAsync(new(acquisition.FlowRunId, acquisition.Dataset!.Flow, input,
            acquisition.CreatedBy.ToString("D"), acquisition.CorrelationId, acquisition.Name,
            acquisition.TenantId, acquisition.WorkspaceId, acquisition.CreatedBy)
        {
            LogicalScopeRef = acquisition.LogicalScopeRef,
            TransientPayloads = acquisition.Dataset!.Definition.Storage is null,
            Compositions = acquisition.Dataset.Composition?.Receipt?.Bindings
            ?? new Dictionary<string, IReadOnlyList<Agentstration.Flows.FlowCompositionCall>>()
        }, cancellationToken);
    }

    public async Task ValidateExecutionAsync(DataSourceAcquisitionResource acquisition, CancellationToken cancellationToken)
    {
        var context = execution.RequireContext("data_source_context_required");
        if (context.WorkspaceId != acquisition.WorkspaceId || context.TenantId != acquisition.TenantId)
            throw Invalid("data_source_acquisition_not_found", "The acquisition is outside the execution Workspace.");
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken);
        var source = await sources.ResolveForUseAsync(acquisition.DataSourceNamespace, acquisition.DataSourceName,
            acquisition.DataSourceScopeRef, execution.ConsumerScope, cancellationToken);
        if (source.Value.Uid != acquisition.DataSourceUid || !source.Value.Definition.Enabled)
            throw Invalid("data_source_unavailable", "The original dataset is disabled or replaced.");
        // Revalidate the accepted dependency pins, rather than substituting an edited dataset definition.
        _ = await compositions.ResolveAsync(source.Value with
        {
            Definition = source.Value.Definition with
            { Dataset = acquisition.Dataset!.Definition }
        }, execution.ConsumerScope, cancellationToken, acquisition.Dataset);
    }

    public async Task<StoredResource<DataSourceAcquisitionResource>> SynchronizeAsync(
        StoredResource<DataSourceAcquisitionResource> stored, CancellationToken cancellationToken)
    {
        if (Terminal(stored.Value.State))
        {
            if (stored.Value.State == DataSourceAcquisitionState.Succeeded && stored.Value.Manifest is null
                && execution.RequireContext("data_source_context_required").PrincipalId == stored.Value.CreatedBy)
            {
                try { return await ReconcileArtifactAsync(stored, cancellationToken); }
                // Catalogue repair must not make a successful history unreadable or prevent
                // an explicit new acquisition when older Storage content is unavailable.
                catch (Exception exception) when (exception is AuthorizationDeniedException
                    or DataSourceAcquisitionException or DataSourceValidationException
                    or ArtifactValidationException or Agentstration.Storage.StorageException
                    or ResourceConcurrencyException)
                { return stored; }
            }
            return stored;
        }
        var acquisition = stored.Value;
        if (execution.RequireContext("data_source_context_required").PrincipalId != acquisition.CreatedBy) return stored;
        var run = await flows.GetAsync(acquisition.WorkspaceId, acquisition.FlowRunId,
            acquisition.TenantId, acquisition.CreatedBy, cancellationToken);
        if (run is null) return stored;
        var state = run.TransientPayloadsAvailable ? run.State : DataSourceAcquisitionState.Failed;
        var code = run.TransientPayloadsAvailable ? run.ErrorCode : "data_source_direct_content_unavailable";
        StorageObject? result = null;
        DataSourceAcquisitionManifest? manifest = acquisition.Manifest;
        DirectDataSourceResult? directResult = acquisition.DirectResult;
        var publication = acquisition.Publication;
        if (state == DataSourceAcquisitionState.Succeeded)
        {
            try
            {
                await ValidateExecutionAsync(acquisition, cancellationToken);
                if (run.Output is not { } output) throw Invalid("data_source_output_missing", "The operation returned no content.");
                DataSourceDatasetCompositionService.ValidateValue(acquisition.Dataset!.OutputSchema, output);
                var bytes = acquisition.Dataset.Definition.DocumentCollectionUid is null
                    ? JsonSerializer.SerializeToUtf8Bytes(output) : DataSourceDocumentStorage.Encode(output);
                if (acquisition.Dataset.Definition.Storage is null)
                {
                    if (bytes.Length > DataSourceDatasetCompositionService.MaximumValueBytes)
                        throw Invalid("data_source_output_too_large", "The direct output exceeds the bounded handoff limit.");
                    directResult = new(bytes.Length, Digest(bytes), clock.GetUtcNow().AddMinutes(5), acquisition.IdempotencyKey ?? acquisition.Name);
                }
                else
                {
                    var target = DataSourceDatasetCompositionService.Target(acquisition, execution.ConsumerScope);
                    if (bytes.Length > target.Policy.MaximumWriteBytes)
                        throw Invalid("data_source_output_too_large", "The operation output exceeds the dataset limit.");
                    var digest = Digest(bytes);
                    if (target.DocumentCollectionUid is not null)
                    {
                        if (documentStorage is null) throw Invalid("data_source_document_storage_unavailable", "Structured acquisition storage is unavailable.");
                        if (acquisition.WriteNotAfter is null)
                        {
                            var writing = acquisition with { Generation = checked(acquisition.Generation + 1), WriteNotAfter = clock.GetUtcNow().AddMinutes(4) };
                            try
                            {
                                stored = await execution.InWorkspaceAsync(() => store.PutExactAsync(writing.ScopeRef!.Value, writing, stored.ETag, false, cancellationToken));
                                acquisition = stored.Value;
                            }
                            catch (ResourceConcurrencyException)
                            {
                                var latest = await execution.InWorkspaceAsync(() => store.GetExactAsync<DataSourceAcquisitionResource>(
                                    new(acquisition.ScopeRef!.Value, acquisition.Namespace, acquisition.Kind, acquisition.Name), cancellationToken));
                                return await SynchronizeAsync(latest ?? stored, cancellationToken);
                            }
                        }
                        publication = await documentStorage.PrepareAsync(acquisition, target, output, bytes, cancellationToken);
                        result = new(publication.Key, publication.Version, publication.Length, publication.Sha256, string.Empty);
                    }
                    else
                    {
                        var key = $"{acquisition.Dataset.Partition}{acquisition.WorkspaceId:N}/{acquisition.Name}.json";
                        // Each acquisition writes a unique immutable key. Check a recovered write before retrying it.
                        var page = await content.ListAsync(target, key, 1, cancellationToken: cancellationToken);
                        result = page.Objects.SingleOrDefault(item => item.Key == key);
                        if (result is not null && result.Sha256 != digest)
                            throw Invalid("data_source_content_conflict", "The acquired content identity conflicts with an existing write.");
                        if (result is null)
                        {
                            if (acquisition.WriteNotAfter is null)
                            {
                                var writing = acquisition with { Generation = checked(acquisition.Generation + 1), WriteNotAfter = clock.GetUtcNow().AddMinutes(4) };
                                try
                                {
                                    stored = await execution.InWorkspaceAsync(() => store.PutExactAsync(writing.ScopeRef!.Value, writing, stored.ETag, false, cancellationToken));
                                    acquisition = stored.Value;
                                }
                                catch (ResourceConcurrencyException)
                                {
                                    var latest = await execution.InWorkspaceAsync(() => store.GetExactAsync<DataSourceAcquisitionResource>(
                                        new(acquisition.ScopeRef!.Value, acquisition.Namespace, acquisition.Kind, acquisition.Name), cancellationToken));
                                    return await SynchronizeAsync(latest ?? stored, cancellationToken);
                                }
                            }
                            if (acquisition.WriteNotAfter <= clock.GetUtcNow())
                                throw Invalid("data_source_write_outcome_unknown", "The publication deadline expired without a confirmed content receipt.");
                            result = await content.WriteAsync(target, new(acquisition.Name, acquisition.WriteNotAfter!.Value,
                                key, Convert.ToBase64String(bytes)), cancellationToken);
                        }
                    }
                    var materialization = new ArtifactStorageMaterialization(new(acquisition.WorkspaceId), target,
                        result.Key, result.Version, bytes.Length, digest, publication);
                    var artifact = await execution.InWorkspaceAsync(() => artifacts.RegisterStoredAsync(new(
                        "dataset:" + acquisition.Uid.ToString("N"), acquisition.FlowRunId,
                        new(acquisition.DataSourceScopeRef, acquisition.DataSourceNamespace, DataSourceResourceKinds.DataSource, acquisition.DataSourceName),
                        acquisition.Name, "application/json", materialization, ProducerUid: acquisition.DataSourceUid,
                        ProducerGeneration: acquisition.DataSourceGeneration, DataContract: Agentstration.Schemas.DataContracts.Declared(acquisition.Dataset!.OutputSchema,
                            $"datasource:{acquisition.DataSourceName}/acquisition:{acquisition.Name}/output", "application/json")), cancellationToken));
                    manifest = new()
                    {
                        Artifacts = [new() { ArtifactId = artifact.ArtifactId.ToString(),
                    Kind = DataSourceArtifactKind.Durable, Disposition = DataSourceArtifactDisposition.Publishable,
                    Name = acquisition.Name, MediaType = artifact.Receipt.MediaType, Digest = artifact.Receipt.Sha256 }]
                    };
                }
            }
            catch (Exception exception) when (exception is ArtifactValidationException or DataSourceValidationException or DataSourceAcquisitionException
                or Agentstration.Storage.StorageException or Agentstration.ConnectedServices.ConnectedServiceOperationDiscoveryException
                or Agentstration.ConnectedServices.Contracts.ConnectedServiceExportInspectionException
                or Agentstration.ConnectedServices.ConnectedServiceOperationTypeValidationException
                or Agentstration.ConnectedServices.ConnectedServiceOperationTypeRevisionException)
            {
                state = DataSourceAcquisitionState.Failed;
                result = null;
                code = exception switch
                {
                    ArtifactValidationException invalid => invalid.Code,
                    DataSourceValidationException invalid => invalid.Code,
                    DataSourceAcquisitionException invalid => invalid.Code,
                    Agentstration.Storage.StorageException unavailable => unavailable.Code,
                    Agentstration.ConnectedServices.ConnectedServiceOperationDiscoveryException unavailable => unavailable.Code,
                    _ => "data_source_publication_refused"
                };
            }
        }
        if (state == acquisition.State && !Terminal(state)) return stored;
        var updated = acquisition with
        {
            Generation = checked(acquisition.Generation + 1),
            State = state,
            Content = result,
            Publication = state == DataSourceAcquisitionState.Succeeded ? publication : null,
            DirectResult = state == DataSourceAcquisitionState.Succeeded ? directResult : null,
            Manifest = state == DataSourceAcquisitionState.Succeeded ? manifest : null,
            CompletedAt = Terminal(state) ? clock.GetUtcNow() : null,
            ErrorCode = code,
            ErrorMessage = code is null ? null : "The operation or dataset publication could not complete.",
            Status = new()
            {
                ProvisioningState = state == DataSourceAcquisitionState.Succeeded ? ProvisioningState.Succeeded
                : Terminal(state) ? ProvisioningState.Failed : ProvisioningState.Accepted
            }
        };
        try
        {
            var resultStored = await execution.InWorkspaceAsync(() => store.PutExactAsync(updated.ScopeRef!.Value, updated, stored.ETag, false, cancellationToken));
            if (state == DataSourceAcquisitionState.Succeeded && history is not null)
                await history.MaintainAsync(resultStored.Value, cancellationToken);
            return resultStored;
        }
        catch (ResourceConcurrencyException)
        {
            return await execution.InWorkspaceAsync(() => store.GetExactAsync<DataSourceAcquisitionResource>(
                new(updated.ScopeRef!.Value, updated.Namespace, updated.Kind, updated.Name), cancellationToken)) ?? stored;
        }
    }

    public async Task AcquireProjectionLeaseAsync(DataSourceAcquisitionResource acquisition, string requestId, CancellationToken cancellationToken)
    {
        await ValidateExecutionAsync(acquisition, cancellationToken);
        await ChangeProjectionLeaseAsync(acquisition, requestId, false, cancellationToken);
    }
    public Task ReleaseProjectionLeaseAsync(DataSourceAcquisitionResource acquisition, string requestId, CancellationToken cancellationToken) =>
        ChangeProjectionLeaseAsync(acquisition, requestId, true, cancellationToken);
    private async Task ChangeProjectionLeaseAsync(DataSourceAcquisitionResource acquisition, string requestId, bool release, CancellationToken ct)
    {
        if (requestId.Length is < 1 or > 256) throw Invalid("data_source_lease_invalid", "A bounded projection identity is required.");
        var context = execution.RequireContext("data_source_context_required");
        if (context.WorkspaceId != acquisition.WorkspaceId || context.TenantId != acquisition.TenantId)
            throw Invalid("data_source_acquisition_not_found", "The acquisition is outside this Workspace.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stored = await execution.InWorkspaceAsync(() => store.GetExactAsync<DataSourceAcquisitionResource>(
                new(acquisition.ScopeRef!.Value, acquisition.Namespace, acquisition.Kind, acquisition.Name), ct))
                ?? throw Invalid("data_source_acquisition_not_found", "The acquisition is unavailable.");
            if (stored.Value.Uid != acquisition.Uid || !release && (stored.Value.ContentRetiredAt is not null || stored.Value.ContentRetiringAt is not null))
                throw Invalid("data_source_content_unavailable", "The acquired content is retiring or unavailable.");
            var leases = stored.Value.ProjectionLeases.Where(pair => pair.Value > clock.GetUtcNow()).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (release) leases.Remove(requestId);
            else
            {
                if (!leases.ContainsKey(requestId) && leases.Count >= 32) throw Invalid("data_source_lease_capacity", "The projection input lease capacity has been reached.");
                leases[requestId] = clock.GetUtcNow().AddMinutes(15);
            }
            try
            {
                _ = await execution.InWorkspaceAsync(() => store.PutExactAsync(stored.Value.ScopeRef!.Value, stored.Value with
                { Generation = checked(stored.Value.Generation + 1), ProjectionLeases = leases }, stored.ETag, false, ct));
                return;
            }
            catch (ResourceConcurrencyException) when (attempt < 2) { }
        }
    }

    public async Task<JsonElement> ReadDirectAsync(DataSourceAcquisitionResource acquisition, string requestId, CancellationToken cancellationToken)
    {
        var context = execution.RequireContext("data_source_context_required");
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        if (acquisition.WorkspaceId != context.WorkspaceId || acquisition.TenantId != context.TenantId || acquisition.CreatedBy != context.PrincipalId
            || acquisition.LogicalScopeRef != execution.ConsumerScope || acquisition.State != DataSourceAcquisitionState.Succeeded
            || acquisition.Dataset?.Definition.Storage is not null || acquisition.Dataset?.Definition.Lifecycle?.AllowDirect != true
            || acquisition.DirectResult is not { } receipt || receipt.RequestId != requestId || receipt.ExpiresAt <= clock.GetUtcNow())
            throw Invalid("data_source_direct_content_unavailable", "The direct result is unavailable outside its accepted projection handoff.");
        await ValidateExecutionAsync(acquisition, cancellationToken);
        var run = await flows.GetAsync(acquisition.WorkspaceId, acquisition.FlowRunId, acquisition.TenantId, acquisition.CreatedBy, cancellationToken);
        if (run?.Output is not { } output || run.State != DataSourceAcquisitionState.Succeeded)
            throw Invalid("data_source_direct_content_unavailable", "The ephemeral execution payload is no longer available.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(output);
        if (bytes.Length != receipt.Length || !SameDigest(Digest(bytes), receipt.Sha256))
            throw Invalid("data_source_direct_content_unavailable", "The direct content no longer matches its accepted receipt.");
        return output.Clone();
    }

    public async Task<StorageObjectContent> ReadAsync(DataSourceAcquisitionResource acquisition, long offset,
        int maximumBytes, CancellationToken cancellationToken)
    {
        var context = execution.RequireContext("data_source_context_required");
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        var source = await sources.ResolveForUseAsync(acquisition.DataSourceNamespace, acquisition.DataSourceName,
            acquisition.DataSourceScopeRef, execution.ConsumerScope, cancellationToken);
        if (source.Value.Uid != acquisition.DataSourceUid || acquisition.WorkspaceId != context.WorkspaceId
            || acquisition.TenantId != context.TenantId || !source.Value.Definition.Enabled)
            throw Invalid("data_source_content_unavailable", "The dataset content is unavailable.");
        if (acquisition.ContentRetiredAt is not null || acquisition.Content is not { } item || acquisition.State != DataSourceAcquisitionState.Succeeded)
            throw Invalid("data_source_content_unavailable", "The acquisition has no published content.");
        var target = DataSourceDatasetCompositionService.Target(acquisition, execution.ConsumerScope);
        if (acquisition.Publication is { } publication)
        {
            if (documentStorage is null || publication.Target.Storage != target.Storage
                || publication.Target.DocumentCollectionUid != target.DocumentCollectionUid
                || publication.Key != item.Key || publication.Version != item.Version
                || publication.Length != item.Length || publication.Sha256 != item.Sha256)
                throw ContentVersionUnavailable();
            return await documentStorage.ReadAsync(publication with { Target = target }, item, offset, maximumBytes, cancellationToken);
        }
        // References are short-lived and subject-bound. Renew from the recorded key after authorization.
        var page = await content.ListAsync(target, item.Key, 1, cancellationToken: cancellationToken);
        var currentItem = page.Objects.SingleOrDefault(value => value.Key == item.Key);
        if (currentItem is null || currentItem.Length != item.Length
            || (item.Version is not null && currentItem.Version != item.Version)
            || (item.Sha256 is not null && currentItem.Sha256 is not null && !SameDigest(currentItem.Sha256, item.Sha256)))
            throw ContentVersionUnavailable();
        if (item.Version is not null && SameDigest(currentItem.Sha256, item.Sha256))
            return await content.ReadAsync(target, currentItem.Reference, offset, maximumBytes, cancellationToken);

        // A streaming write can attest its bytes even when the provider returns no version or listing checksum.
        if (item.Sha256 is null || item.Length is < 0 or > DataSourceDatasetCompositionService.MaximumValueBytes)
            throw ContentVersionUnavailable();
        if (offset < 0 || offset > item.Length || maximumBytes <= 0)
            throw new Agentstration.Storage.StorageException("storage_content_limit_exceeded", "Invalid read range.");
        // A bounded read may return only part of an object. Verify the whole historical
        // value in bounded chunks before exposing any requested range, as Artifact reads do.
        using var verified = new MemoryStream();
        var chunkSize = Math.Min(DataSourceDatasetCompositionService.MaximumValueBytes, target.Policy.MaximumReadBytes);
        do
        {
            var count = (int)Math.Min(chunkSize, item.Length - verified.Length);
            StorageObjectContent chunk;
            try
            {
                chunk = await content.ReadAsync(target, currentItem.Reference, verified.Length,
                    Math.Max(1, count), cancellationToken);
            }
            catch (Agentstration.Storage.StorageException exception) when (exception.Code == "storage_content_limit_exceeded" && chunkSize > 1)
            {
                chunkSize = Math.Max(1, chunkSize / 2);
                continue;
            }
            var bytes = Convert.FromBase64String(chunk.ContentBase64);
            if (chunk.Offset != verified.Length || chunk.Item.Key != item.Key
                || chunk.Item.Version != currentItem.Version || chunk.Item.Length != item.Length
                || bytes.Length > count || bytes.Length == 0 && verified.Length < item.Length
                || chunk.EndOfObject != (verified.Length + bytes.Length == item.Length))
                throw ContentVersionUnavailable();
            await verified.WriteAsync(bytes, cancellationToken);
        } while (verified.Length < item.Length);
        var all = verified.ToArray();
        if (!SameDigest(Digest(all), item.Sha256)) throw ContentVersionUnavailable();
        var length = (int)Math.Min(maximumBytes, item.Length - offset);
        return new(currentItem with { Sha256 = item.Sha256 }, offset,
            Convert.ToBase64String(all.AsSpan((int)offset, length)), offset + length == item.Length);
    }

    public async Task<StoredResource<DataSourceAcquisitionResource>> ReconcileArtifactAsync(
        StoredResource<DataSourceAcquisitionResource> stored, CancellationToken cancellationToken)
    {
        var acquisition = stored.Value;
        if (acquisition.State != DataSourceAcquisitionState.Succeeded || acquisition.Manifest is not null
            || acquisition.Dataset is null || acquisition.Content is not { Sha256: { } digest } item) return stored;
        if (execution.RequireContext("data_source_context_required").PrincipalId != acquisition.CreatedBy)
            throw Invalid("data_source_access_denied", "Artifact reconciliation requires the original acquisition caller.");
        _ = await ReadAsync(acquisition, 0, 1, cancellationToken);
        var materialization = new ArtifactStorageMaterialization(new(acquisition.WorkspaceId),
            DataSourceDatasetCompositionService.Target(acquisition, execution.ConsumerScope), item.Key, item.Version, item.Length, digest, acquisition.Publication);
        var artifact = await execution.InWorkspaceAsync(() => artifacts.RegisterStoredAsync(new(
            "dataset:" + acquisition.Uid.ToString("N"), acquisition.FlowRunId,
            new(acquisition.DataSourceScopeRef, acquisition.DataSourceNamespace, DataSourceResourceKinds.DataSource, acquisition.DataSourceName),
            acquisition.Name, "application/json", materialization, ProducerUid: acquisition.DataSourceUid,
            ProducerGeneration: acquisition.DataSourceGeneration, DataContract: Agentstration.Schemas.DataContracts.Declared(acquisition.Dataset!.OutputSchema,
                            $"datasource:{acquisition.DataSourceName}/acquisition:{acquisition.Name}/output", "application/json")), cancellationToken));
        var updated = acquisition with
        {
            Generation = checked(acquisition.Generation + 1),
            Manifest = new()
            {
                Artifacts = [new() { ArtifactId = artifact.ArtifactId.ToString(), Kind = DataSourceArtifactKind.Durable,
                Disposition = DataSourceArtifactDisposition.Publishable, Name = acquisition.Name,
                MediaType = artifact.Receipt.MediaType, Digest = artifact.Receipt.Sha256 }]
            }
        };
        return await execution.InWorkspaceAsync(() => store.PutExactAsync(updated.ScopeRef!.Value, updated, stored.ETag, false, cancellationToken));
    }

    private static DataSourceAcquisitionException ContentVersionUnavailable() =>
        Invalid("data_source_content_version_unavailable", "The exact historical content version is unavailable.");

    private static bool SameDigest(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Digest(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static JsonElement InvocationInput(DataSourceAcquisitionResource acquisition) => JsonSerializer.SerializeToElement(new
    {
        acquisitionId = acquisition.Name,
        operation = acquisition.Dataset!.Definition.Acquisition.Operation,
        parameters = acquisition.Parameters,
        caller = new DataSourceAcquisitionCaller(acquisition.CreatedBy, acquisition.TenantId, acquisition.WorkspaceId)
    }, JsonSerializerOptions.Web);
    private static bool Terminal(DataSourceAcquisitionState state) => state is DataSourceAcquisitionState.Succeeded
        or DataSourceAcquisitionState.Failed or DataSourceAcquisitionState.Cancelled or DataSourceAcquisitionState.TimedOut;
    private static DataSourceAcquisitionException Invalid(string code, string message) => new(code, message);
}
