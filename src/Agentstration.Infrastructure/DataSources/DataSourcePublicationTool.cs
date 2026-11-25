using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.DataSources;

internal sealed class DataSourcePublicationTool(string phase, DataSourceDatasetAcquisitionService acquisitions,
    IFlowRepository runs, ICurrentRequestContext current, TimeProvider clock, Agentstration.Flows.Application.IFlowRunEventSink events) : IInternalMcpToolHandler
{
    public InternalMcpToolDefinition Definition { get; } = CreateDefinition(phase);

    internal static InternalMcpToolDefinition CreateDefinition(string phase) => new("data-source.publication." + phase,
        "data-source.publication." + phase, null, JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { acquisitionId = new { type = "string", minLength = 1, maxLength = 128 } },
            required = new[] { "acquisitionId" },
            additionalProperties = false
        }), JsonSerializer.SerializeToElement(new { type = "object" }), ExposeThroughMcp: false);

    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken token)
    {
        if (invocation.CallerKind != ToolDefinitionCallerKind.Flow || invocation.RunId is null
            || invocation.FlowStepId != "publication-" + phase || current.Current.PrincipalId != invocation.PrincipalId
            || current.Current.WorkspaceId != invocation.WorkspaceId.Value || current.Current.TenantId != invocation.TenantId)
            throw new DataSourceAcquisitionException("data_source_publication_execution_denied", "Publication requires its accepted Flow step.");
        var scope = ResourceScopeRef.Workspace(current.Current.WorkspaceId);
        var id = invocation.Arguments.GetProperty("acquisitionId").GetString()!;
        var prior = await runs.GetRunExactAsync(scope, invocation.RunId, token);
        var previousAttempts = prior?.Value.Steps.Single(value => value.StepName == invocation.FlowStepId).OperationProgress?.Attempt ?? 0;
        for (var attempt = previousAttempts + 1; ; attempt++)
        {
            await SetProgressAsync(scope, invocation.RunId, invocation.FlowStepId, "Active", attempt, null, token);
            try
            {
                await acquisitions.ExecutePublicationStepAsync(id, invocation.RunId, phase, token);
                return JsonSerializer.SerializeToElement(new { acquisitionId = id, phase });
            }
            catch (StorageException error) when (attempt < 120 && (error.StatusCode is 503 or 504 || error.Code == "storage_publication_unavailable"))
            {
                await SetProgressAsync(scope, invocation.RunId, invocation.FlowStepId, "WaitingForProvider", attempt, error.Code, token);
                await Task.Delay(TimeSpan.FromSeconds(1), clock, token);
            }
            catch (Agentstration.ResourceManagement.ResourceConcurrencyException) when (attempt < 120)
            {
                // Re-read the committed receipt. A competing recovery can never change publication identity.
            }
            catch (StorageException error)
            {
                throw new ToolDefinitionInvocationException(error.Code, "Storage publication failed.");
            }
            catch (DataSourceAcquisitionException error)
            {
                throw new ToolDefinitionInvocationException(error.Code, "The accepted publication is unavailable or no longer authorized.");
            }
        }
    }

    private async Task SetProgressAsync(ResourceScopeRef scope, string runId, string stepName, string state,
        int attempt, string? code, CancellationToken token)
    {
        for (var retry = 0; ; retry++)
        {
            var stored = await runs.GetRunExactAsync(scope, runId, token) ?? throw new FlowRunNotFoundException(runId);
            if (stored.Value.Status.IsTerminal()) throw new OperationCanceledException(token);
            var progress = new FlowOperationProgress(state, attempt, clock.GetUtcNow(), code);
            try
            {
                await runs.UpdateRunAsync(stored.Value with
                {
                    Steps = stored.Value.Steps.Select(value => value.StepName == stepName
                    ? value with { OperationProgress = progress } : value).ToArray()
                }, stored.ETag, token);
                var progressEvent = await runs.AppendRunEventAsync(new(scope, runId, 0, FlowRunEventType.StepProgressChanged,
                    stepName, JsonSerializer.SerializeToElement(progress), progress.UpdatedAt), token);
                await events.PublishAsync(progressEvent, token);
                return;
            }
            catch (FlowConcurrencyException) when (retry < 20) { }
        }
    }
}
