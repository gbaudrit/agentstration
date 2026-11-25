using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;

namespace Agentstration.Infrastructure.ConnectedServices;

internal sealed class ConnectedServiceAcquisitionExecutor(DataSourceDatasetAcquisitionService acquisitions,
    ConnectedServiceRawOperationExecutor operationExecutor, ICurrentRequestContext current,
    Agentstration.Schemas.ISchemaFormatRegistry formats)
{
    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.CallerKind != ToolDefinitionCallerKind.Flow || string.IsNullOrEmpty(invocation.RunId)
            || current.AccessMode is not (ControlPlaneAccessMode.Workspace or ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration))
            throw new DataSourceAcquisitionException("connected_service_acquisition_execution_denied", "An accepted governed acquisition is required.");
        var context = current.Current;
        if (context.PrincipalId != invocation.PrincipalId || context.TenantId != invocation.TenantId || context.WorkspaceId != invocation.WorkspaceId.Value)
            throw new DataSourceAcquisitionException("connected_service_acquisition_execution_denied", "The execution context does not match the invocation.");
        var consumer = current.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(context.TenantId),
            _ => ResourceScopeRef.Workspace(context.WorkspaceId)
        };
        var acquisition = await acquisitions.GetExecutionAsync(invocation.Arguments.GetProperty("acquisitionId").GetString()!, invocation.RunId, cancellationToken);
        try
        {
            var result = await operationExecutor.ExecuteAsync(acquisition.Dataset!.Definition.Acquisition, consumer, acquisition.CorrelationId, cancellationToken);
            var adapter = acquisition.Dataset.OperationType.Acquisition!;
            DataSourceDatasetCompositionService.ValidateValue(acquisition.Dataset.Operation.Definition.Definition.GetProperty(adapter.OutputSchemaProperty), result);
            DataSourceDatasetCompositionService.ValidateOperationOutput(acquisition.Dataset, result, invocation.RunId);
            var content = await OperationContentValidation.ValidateAsync(acquisition.Dataset.Operation.Definition.AdoptedContentContract, result, formats, cancellationToken);
            if (!content.IsValid) throw new DataSourceAcquisitionException(content.Issues[0].Code, "The native operation content does not satisfy its adopted contract.");
            return result;
        }
        catch (AepProtocolException) { throw new DataSourceAcquisitionException("connected_service_provider_acquisition_failed", "Connected Service acquisition could not be completed."); }
        catch (HttpRequestException) { throw new DataSourceAcquisitionException("connected_service_provider_unavailable", "Connected Service acquisition could not be completed."); }
    }
}

internal sealed class ConnectedServiceAcquisitionMcpTool(ConnectedServiceAcquisitionExecutor execution) : IInternalMcpToolHandler
{
    public const string Name = "connected-service.operations.acquire";
    public InternalMcpToolDefinition Definition { get; } = new(Name, Name, null,
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { acquisitionId = new { type = "string", minLength = 1, maxLength = 128 } },
            required = new[] { "acquisitionId" },
            additionalProperties = false
        }), JsonSerializer.SerializeToElement(new { }), ExposeThroughMcp: false);
    public Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken) =>
        execution.ExecuteAsync(invocation, cancellationToken);
}
