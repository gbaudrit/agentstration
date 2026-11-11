using System.Text.Json;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Artifacts;

/// <summary>Ordinary governed Tools for authored Tool/Flow processing paths; an invocation cannot choose its content scope.</summary>
public sealed class ArtifactProcessingContentTool(RuntimeExecutionToolMaterial operation, ArtifactProcessingService processing,
    IFlowRepository runs, ICurrentRequestContext current) : IInternalMcpToolHandler
{
    public InternalMcpToolDefinition Definition { get; } = new("artifact.processing." + operation.ExternalId,
        operation.Name, operation.Description, operation.InputSchema,
        InitialCategory: new("artifact-processing", "Artifact processing", "Content operations restricted to the current Artifact processing execution."),
        ExposeThroughMcp: false);

    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (current.AccessMode is not (ControlPlaneAccessMode.Workspace or ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration)
            || current.Current.PrincipalId == Guid.Empty || current.Current.TenantId == Guid.Empty || current.Current.WorkspaceId == Guid.Empty
            || current.Current.PrincipalId != invocation.PrincipalId
            || current.Current.TenantId != invocation.TenantId || current.Current.WorkspaceId != invocation.WorkspaceId.Value
            || string.IsNullOrWhiteSpace(invocation.RunId) || invocation.CallerKind is not (ToolDefinitionCallerKind.Flow or ToolDefinitionCallerKind.Agent))
            throw new ToolDefinitionInvocationException("artifact_processing_scope_invalid", "Content operations require the trusted Artifact processing execution.");
        var run = (await runs.GetRunExactAsync(ResourceScopeRef.Workspace(invocation.WorkspaceId.Value), invocation.RunId, cancellationToken))?.Value
            ?? throw new ToolDefinitionInvocationException("artifact_processing_scope_invalid", "The processing execution is unavailable.");
        if (run.Scope.PrincipalId != invocation.PrincipalId || run.Scope.TenantId != invocation.TenantId)
            throw new ToolDefinitionInvocationException("artifact_processing_scope_invalid", "The processing execution coordinates differ.");
        try { return await processing.ExecuteToolAsync(run, operation.ExternalId!, invocation.Arguments, cancellationToken); }
        catch (RuntimeGovernedOperationException exception) { throw new ToolDefinitionInvocationException(exception.Code, exception.Message); }
    }
}
