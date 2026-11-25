using System.Text.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Storage.Contracts;

namespace Agentstration.DataSources;

public sealed partial class DataSourceDatasetAcquisitionService
{
    /// <summary>Read durable execution evidence without running or repairing any operation.</summary>
    public Task<StoredResource<DataSourceAcquisitionResource>> ObserveAsync(
        StoredResource<DataSourceAcquisitionResource> stored, CancellationToken token) => ObserveCoreAsync(stored, false, token);

    private async Task<StoredResource<DataSourceAcquisitionResource>> ObserveCoreAsync(
        StoredResource<DataSourceAcquisitionResource> stored, bool includeTerminalOutcome, CancellationToken token)
    {
        var value = stored.Value;
        if (value.Dataset?.DurablePublication != true || Terminal(value.State)) return stored;
        var run = await flows.ObserveAsync(value.WorkspaceId, value.FlowRunId, value.TenantId, value.CreatedBy, token);
        if (run is null) return stored;
        // Only execution/recovery records the terminal acquisition outcome; readers cannot race that commit.
        var state = Terminal(run.State) && !includeTerminalOutcome ? value.State : run.State;
        if (state == DataSourceAcquisitionState.Succeeded && (value.Manifest is null || value.PublicationSealedAt is null))
            state = DataSourceAcquisitionState.Failed;
        return stored with
        {
            Value = value with
            {
                State = state,
                Progress = run.Progress,
                ContractFailure = run.ContractFailure ?? value.ContractFailure,
                CompletedAt = Terminal(state) ? run.CompletedAt ?? value.CompletedAt : null,
                ErrorCode = run.ErrorCode ?? (state == DataSourceAcquisitionState.Failed ? value.ErrorCode ?? "data_source_publication_unconfirmed" : null),
                ErrorMessage = run.ErrorMessage ?? value.ErrorMessage
            }
        };
    }

    /// <summary>Called exclusively by the accepted root's governed publication Tool steps.</summary>
    public async Task ExecutePublicationStepAsync(string id, string runId, string phase, CancellationToken token)
    {
        var acquisition = await GetExecutionAsync(id, runId, token);
        if (acquisition.FlowRunId != runId || acquisition.Dataset?.DurablePublication != true)
            throw Invalid("data_source_publication_execution_denied", "Publication requires its accepted durable root.");
        var address = new ScopedResourceAddress(acquisition.ScopeRef!.Value, acquisition.Namespace, acquisition.Kind, acquisition.Name);
        var stored = await execution.InWorkspaceAsync(() => store.GetExactAsync<DataSourceAcquisitionResource>(address, token))
            ?? throw Invalid("data_source_acquisition_not_found", "The accepted acquisition is unavailable.");
        acquisition = stored.Value;
        await ValidateExecutionAsync(acquisition, token);
        var dataset = acquisition.Dataset!;
        var target = DataSourceDatasetCompositionService.Target(acquisition, execution.ConsumerScope);
        if (recordPublications is null) throw Invalid("data_source_record_storage_unavailable", "Record publication is unavailable.");
        switch (phase)
        {
            case "prepare":
                var run = await flows.GetAsync(acquisition.WorkspaceId, runId, acquisition.TenantId, acquisition.CreatedBy, token);
                if (run?.ProcessingOutput is not { } output)
                    throw Invalid("data_source_output_missing", "The accepted transformations have not completed.");
                var projection = JsonRecordProjection.Parse(output);
                var value = projection?.Value ?? output;
                if (dataset.OutputSchemaSnapshot is not null)
                    DataContracts.Validate(DataSourceDatasetCompositionService.Contract(dataset, acquisition.Name), value, "acquisition-output", runId);
                else DataSourceDatasetCompositionService.ValidateOutput(dataset.OutputSchema, value, compositions.MaximumValueBytes);
                projection ??= JsonRecordProjection.Normalize(value,
                    DataSourceDatasetCompositionService.Contract(dataset, $"datasource:{acquisition.DataSourceName}/acquisition:{id}/output"));
                var records = projection.Value.EnumerateArray().ToArray();
                var bytes = StorageRecordSets.Encode(records);
                if (bytes.Length > compositions.MaximumValueBytes || bytes.Length > target.Policy.MaximumWriteBytes)
                    throw Invalid("data_source_output_too_large", "The normalized result exceeds the publication limit.");
                if (acquisition.Publication is { } prepared)
                {
                    if (prepared.Length != bytes.Length || !SameDigest(prepared.Sha256, Digest(bytes)))
                        throw Invalid("data_source_publication_conflict", "Prepared membership differs from the accepted output.");
                    return;
                }
                if (acquisition.WriteNotAfter is null)
                {
                    acquisition = acquisition with { Generation = checked(acquisition.Generation + 1), WriteNotAfter = clock.GetUtcNow().AddMinutes(4), State = DataSourceAcquisitionState.Running };
                    stored = await execution.InWorkspaceAsync(() => store.PutExactAsync(address.ScopeRef, acquisition, stored.ETag, false, token));
                }
                if (acquisition.WriteNotAfter <= clock.GetUtcNow())
                    throw Invalid("data_source_write_outcome_unknown", "The publication deadline expired without confirmation.");
                var publication = await recordPublications.PrepareRecordsAsync(target, id, records, token, acquisition.WriteNotAfter);
                acquisition = acquisition with { Generation = checked(acquisition.Generation + 1), Publication = publication, RecordContract = projection.RecordContract };
                break;
            case "seal":
                if (acquisition.Publication is null) throw Invalid("data_source_publication_not_prepared", "Publication must be prepared first.");
                if (acquisition.PublicationSealedAt is not null) return;
                await recordPublications.SealAsync(acquisition.Publication, token);
                acquisition = acquisition with { Generation = checked(acquisition.Generation + 1), PublicationSealedAt = clock.GetUtcNow() };
                break;
            case "register":
                if (acquisition.PublicationSealedAt is null || acquisition.Publication is not { } sealedPublication || acquisition.RecordContract is null)
                    throw Invalid("data_source_publication_not_sealed", "Artifact registration requires confirmed sealing.");
                if (acquisition.Manifest is not null) return;
                var content = new StorageObject(sealedPublication.Key, sealedPublication.Version, sealedPublication.Length, sealedPublication.Sha256, string.Empty);
                var materialization = new ArtifactStorageMaterialization(new(acquisition.WorkspaceId), sealedPublication.Target,
                    content.Key, content.Version, content.Length, content.Sha256!, sealedPublication)
                { RecordContract = acquisition.RecordContract };
                var artifact = await execution.InWorkspaceAsync(() => artifacts.RegisterStoredAsync(new(
                    "dataset:" + acquisition.Uid.ToString("N"), runId,
                    new(acquisition.DataSourceScopeRef, acquisition.DataSourceNamespace, DataSourceResourceKinds.DataSource, acquisition.DataSourceName),
                    id, "application/json", materialization, ProducerFlowStepId: "publication-register", ProducerUid: acquisition.DataSourceUid,
                    ProducerGeneration: acquisition.DataSourceGeneration, DataContract: JsonRecordProjection.Collection(acquisition.RecordContract)), token));
                acquisition = acquisition with
                {
                    Generation = checked(acquisition.Generation + 1),
                    Content = content,
                    Manifest = new()
                    {
                        Artifacts = [new() { ArtifactId = artifact.ArtifactId.ToString(), Kind = DataSourceArtifactKind.Durable,
                        Disposition = DataSourceArtifactDisposition.Publishable, Name = id, MediaType = artifact.Receipt.MediaType, Digest = artifact.Receipt.Sha256 }]
                    }
                };
                break;
            default: throw Invalid("data_source_publication_phase_invalid", "Unknown publication phase.");
        }
        _ = await execution.InWorkspaceAsync(() => store.PutExactAsync(address.ScopeRef, acquisition, stored.ETag, false, token));
    }
}
