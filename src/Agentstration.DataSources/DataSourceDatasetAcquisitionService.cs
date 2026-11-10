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
    TimeProvider clock)
{
    private readonly DataSourceExecutionContext execution = new(current, scopes);

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
        var acquisition = records.SingleOrDefault(item => item.Value.Name == id && item.Value.FlowRunId == runId)?.Value;
        if (acquisition?.Dataset is null || Terminal(acquisition.State) || context.PrincipalId != acquisition.CreatedBy
            || acquisition.LogicalScopeRef != execution.ConsumerScope || context.TenantId != acquisition.TenantId)
            throw Invalid("data_source_invocation_invalid", "The invocation does not identify an accepted acquisition in this context.");
        await ValidateExecutionAsync(acquisition, cancellationToken);
        return acquisition;
    }

    public async Task<StoredResource<DataSourceAcquisitionResource>> StartAsync(DataSourceResource source,
        JsonElement callerParameters, string? correlationId, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var context = execution.RequireContext("data_source_context_required");
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken);
        if (callerParameters.ValueKind != JsonValueKind.Object || callerParameters.EnumerateObject().Any())
            throw Invalid("data_source_parameters_fixed", "Dataset acquisitions use saved parameters; callers cannot override them.");
        var dataset = await compositions.ResolveAsync(source, execution.ConsumerScope, cancellationToken);
        var hash = Digest(JsonSerializer.SerializeToUtf8Bytes(new { source.Uid, source.Generation, dataset }, JsonSerializerOptions.Web));
        var records = await execution.InWorkspaceAsync(() => store.ListExactAsync<DataSourceAcquisitionResource>(
            ResourceScopeRef.Workspace(context.WorkspaceId), DataSourceResourceKinds.DataSourceAcquisition, 0, 1000, cancellationToken));
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var prior = records.SingleOrDefault(item => item.Value.DataSourceUid == source.Uid && item.Value.IdempotencyKey == idempotencyKey);
            if (prior is not null)
            {
                if (prior.Value.RequestHash != hash) throw Invalid("data_source_acquisition_idempotency_conflict", "The idempotency key identifies another configuration.");
                return await SynchronizeAsync(prior, cancellationToken);
            }
        }
        foreach (var prior in records.Where(item => item.Value.DataSourceUid == source.Uid))
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
        { LogicalScopeRef = acquisition.LogicalScopeRef }, cancellationToken);
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
        }, execution.ConsumerScope, cancellationToken);
    }

    public async Task<StoredResource<DataSourceAcquisitionResource>> SynchronizeAsync(
        StoredResource<DataSourceAcquisitionResource> stored, CancellationToken cancellationToken)
    {
        if (Terminal(stored.Value.State)) return stored;
        var acquisition = stored.Value;
        if (execution.RequireContext("data_source_context_required").PrincipalId != acquisition.CreatedBy) return stored;
        var run = await flows.GetAsync(acquisition.WorkspaceId, acquisition.FlowRunId,
            acquisition.TenantId, acquisition.CreatedBy, cancellationToken);
        if (run is null) return stored;
        var state = run.State;
        var code = run.ErrorCode;
        StorageObject? result = null;
        if (state == DataSourceAcquisitionState.Succeeded)
        {
            try
            {
                await ValidateExecutionAsync(acquisition, cancellationToken);
                if (run.Output is not { } output) throw Invalid("data_source_output_missing", "The operation returned no content.");
                DataSourceDatasetCompositionService.ValidateValue(acquisition.Dataset!.OutputSchema, output);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(output);
                var target = DataSourceDatasetCompositionService.Target(acquisition, execution.ConsumerScope);
                if (bytes.Length > target.Policy.MaximumWriteBytes)
                    throw Invalid("data_source_output_too_large", "The operation output exceeds the dataset limit.");
                var key = $"{acquisition.Dataset.Partition}{acquisition.WorkspaceId:N}/{acquisition.Name}.json";
                // Each acquisition writes a unique immutable key. Check a recovered write before retrying it.
                var page = await content.ListAsync(target, key, 1, cancellationToken: cancellationToken);
                result = page.Objects.SingleOrDefault(item => item.Key == key);
                var digest = Digest(bytes);
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
            catch (Exception exception) when (exception is DataSourceValidationException or DataSourceAcquisitionException
                or Agentstration.Storage.StorageException or Agentstration.ConnectedServices.ConnectedServiceOperationDiscoveryException
                or Agentstration.ConnectedServices.Contracts.ConnectedServiceExportInspectionException
                or Agentstration.ConnectedServices.ConnectedServiceOperationTypeValidationException
                or Agentstration.ConnectedServices.ConnectedServiceOperationTypeRevisionException)
            {
                state = DataSourceAcquisitionState.Failed;
                result = null;
                code = exception switch
                {
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
            CompletedAt = Terminal(state) ? clock.GetUtcNow() : null,
            ErrorCode = code,
            ErrorMessage = code is null ? null : "The operation or dataset publication could not complete.",
            Status = new()
            {
                ProvisioningState = state == DataSourceAcquisitionState.Succeeded ? ProvisioningState.Succeeded
                : Terminal(state) ? ProvisioningState.Failed : ProvisioningState.Accepted
            }
        };
        try { return await execution.InWorkspaceAsync(() => store.PutExactAsync(updated.ScopeRef!.Value, updated, stored.ETag, false, cancellationToken)); }
        catch (ResourceConcurrencyException)
        {
            return await execution.InWorkspaceAsync(() => store.GetExactAsync<DataSourceAcquisitionResource>(
                new(updated.ScopeRef!.Value, updated.Namespace, updated.Kind, updated.Name), cancellationToken)) ?? stored;
        }
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
        if (acquisition.Content is not { } item || acquisition.State != DataSourceAcquisitionState.Succeeded)
            throw Invalid("data_source_content_unavailable", "The acquisition has no published content.");
        var target = DataSourceDatasetCompositionService.Target(acquisition, execution.ConsumerScope);
        // References are short-lived and subject-bound. Renew from the recorded key after authorization.
        var page = await content.ListAsync(target, item.Key, 1, cancellationToken: cancellationToken);
        var currentItem = page.Objects.SingleOrDefault(value => value.Key == item.Key);
        if (currentItem is null || currentItem.Length != item.Length
            || (item.Version is not null && currentItem.Version != item.Version)
            || (item.Sha256 is not null && currentItem.Sha256 is not null && currentItem.Sha256 != item.Sha256))
            throw ContentVersionUnavailable();
        if (item.Version is not null && currentItem.Sha256 == item.Sha256)
            return await content.ReadAsync(target, currentItem.Reference, offset, maximumBytes, cancellationToken);

        // A streaming write can attest its bytes even when the provider returns no version or listing checksum.
        if (item.Sha256 is null || item.Length is < 0 or > DataSourceDatasetCompositionService.MaximumValueBytes)
            throw ContentVersionUnavailable();
        if (offset < 0 || offset > item.Length || maximumBytes <= 0)
            throw new Agentstration.Storage.StorageException("storage_content_limit_exceeded", "Invalid read range.");
        var verified = await content.ReadAsync(target, currentItem.Reference, 0,
            Math.Max(maximumBytes, (int)item.Length), cancellationToken);
        var bytes = Convert.FromBase64String(verified.ContentBase64);
        if (!verified.EndOfObject || verified.Offset != 0 || verified.Item.Key != item.Key
            || verified.Item.Version != currentItem.Version || verified.Item.Length != item.Length
            || bytes.Length != item.Length || Digest(bytes) != item.Sha256)
            throw ContentVersionUnavailable();

        var length = (int)Math.Min(maximumBytes, item.Length - offset);
        return new(verified.Item with { Sha256 = item.Sha256 }, offset,
            Convert.ToBase64String(bytes.AsSpan((int)offset, length)), offset + length == item.Length);
    }

    private static DataSourceAcquisitionException ContentVersionUnavailable() =>
        Invalid("data_source_content_version_unavailable", "The exact historical content version is unavailable.");

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
