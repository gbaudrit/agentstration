using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Storage.Contracts;

namespace Agentstration.DataSources;

/// <summary>Projects an authoritative terminal Flow outcome without invoking providers or repairing content.</summary>
public sealed class DataSourceFlowOutcomeService(IResourceStore store, IRequestContextScopeFactory scopes,
    IDataSourceHistoryMaintenance? history = null, DataSourceContentOptions? contentOptions = null)
{
    public async Task ApplyAsync(Guid workspaceId, string runId, DataSourceAcquisitionState state,
        DateTimeOffset completedAt, string? code, JsonElement? output, DataSourceAcquisitionProgress progress, CancellationToken token)
    {
        using var system = scopes.PushSystem();
        var owner = ResourceScopeRef.Workspace(workspaceId);
        var records = await store.ListExactAsync<DataSourceAcquisitionResource>(owner, DataSourceResourceKinds.DataSourceAcquisition, 0, 10000, token);
        foreach (var original in records.Where(item => item.Value.FlowRunId == runId && (item.Value.Dataset?.DurablePublication == true || item.Value.Dataset is { Definition.Storage: null })))
        {
            var stored = original;
            for (var retry = 0; ; retry++)
            {
                var value = stored.Value;
                var finalState = state;
                var errorCode = code;
                if (state == DataSourceAcquisitionState.Succeeded && value.Dataset is { Definition.Storage: null } direct)
                {
                    try
                    {
                        if (output is not { } directOutput) throw new DataSourceAcquisitionException("data_source_output_missing", "No direct output is available.");
                        var projection = direct.Composition is null ? null : JsonRecordProjection.Parse(directOutput);
                        DataSourceDatasetCompositionService.ValidateOutput(direct.OutputSchema, projection?.Value ?? directOutput, (contentOptions ?? new()).MaximumValueBytes);
                        projection ??= JsonRecordProjection.Normalize(directOutput, DataSourceDatasetCompositionService.Contract(direct, value.Name));
                        var bytes = StorageRecordSets.Encode(projection.Value.EnumerateArray().ToArray());
                        value = value with { DirectResult = new(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), completedAt.AddMinutes(5), value.IdempotencyKey ?? value.Name), RecordContract = projection.RecordContract };
                    }
                    catch (Exception exception) when (exception is ArgumentException or DataContractException or DataSourceValidationException or DataSourceAcquisitionException)
                    {
                        finalState = DataSourceAcquisitionState.Failed;
                        errorCode = exception switch
                        {
                            DataSourceValidationException invalid => invalid.Code,
                            DataSourceAcquisitionException invalid => invalid.Code,
                            DataContractException invalid => invalid.Code,
                            _ => "data_source_record_projection_invalid"
                        };
                    }
                }
                if (value.Dataset?.DurablePublication == true && state == DataSourceAcquisitionState.Succeeded && (value.Manifest is null || value.PublicationSealedAt is null))
                {
                    finalState = DataSourceAcquisitionState.Failed;
                    errorCode = "data_source_publication_unconfirmed";
                }
                try
                {
                    var completed = await store.PutExactAsync(owner, value with
                    {
                        Generation = checked(value.Generation + 1),
                        State = finalState,
                        Progress = progress,
                        CompletedAt = completedAt,
                        ErrorCode = errorCode,
                        Status = new() { ProvisioningState = finalState == DataSourceAcquisitionState.Succeeded ? ProvisioningState.Succeeded : ProvisioningState.Failed }
                    }, stored.ETag, false, token);
                    if (finalState == DataSourceAcquisitionState.Succeeded && history is not null)
                    {
                        var caller = new RequestContext(value.CreatedBy, value.TenantId, workspaceId);
                        using var callerScope = value.LogicalScopeRef?.Kind switch
                        {
                            ResourceScopeKind.Instance => scopes.PushInstance(caller),
                            ResourceScopeKind.Tenant => scopes.PushTenantAdministration(caller),
                            _ => scopes.Push(caller)
                        };
                        await history.MaintainAsync(completed.Value, token);
                    }
                    break;
                }
                catch (ResourceConcurrencyException) when (retry < 3)
                {
                    stored = await store.GetExactAsync<DataSourceAcquisitionResource>(new(owner, value.Namespace, value.Kind, value.Name), token) ?? throw new DataSourceAcquisitionException("data_source_acquisition_not_found", "The accepted acquisition is unavailable.");
                }
            }
        }
    }
}
