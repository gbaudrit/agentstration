using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Flows;

public sealed class ToolDefinitionAcquisitionExecutor(
    DataSourceAcquisitionService acquisitions,
    ArtifactManagementService artifacts,
    ICurrentRequestContext requestContext,
    IRequestContextScopeFactory requestScopes,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal async Task<StoredResource<ToolDefinitionResource>?> FindInOperationalWorkspaceAsync(
        ToolDefinitionService definitions, ToolDefinitionInvocation invocation, CancellationToken cancellationToken)
    {
        if (requestContext.AccessMode is not (ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration)
            || MatchingContext(invocation) is not { } context)
            return null;

        using var workspace = requestScopes.Push(context);
        var stored = await definitions.GetAsync(invocation.ToolName, invocation.Namespace, cancellationToken);
        return stored?.Value.Definition.Acquisition is not null ? stored : null;
    }

    internal bool OwnsAdministrativeInvocation(ToolDefinitionResource definition, ToolDefinitionInvocation invocation) =>
        definition.Definition.Acquisition is not null && MatchingContext(invocation) is { } context
        && definition.ScopeRef == (requestContext.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(context.TenantId),
            _ => (ResourceScopeRef?)null
        }) && definition.ScopeRef is not null;

    public async Task<ToolDefinitionInvocationResult> ExecuteAsync(
        ToolDefinitionResource definition,
        ToolDefinitionAcquisitionTarget target,
        ToolDefinitionInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (MatchingContext(invocation) is not { } context)
            throw new ToolDefinitionInvocationException("tool_definition_scope_mismatch",
                "The acquisition Tool requires the invocation's trusted Workspace context.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(definition.Definition.InvocationTimeoutSeconds));
        var token = timeout.Token;
        var callHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invocation.CallId)));
        var idempotencyKey = $"acquisition-tool:{definition.Uid:N}:{context.PrincipalId:N}:{callHash}";
        try
        {
            var started = await acquisitions.StartFromSuccessfulAcquisitionAsync(target.TemplateNamespace,
                target.TemplateId, invocation.CorrelationId, idempotencyKey, token);
            var completed = started.Value;
            while (completed.State is DataSourceAcquisitionState.Pending or DataSourceAcquisitionState.Running
                or DataSourceAcquisitionState.WaitingForChild)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, token);
                completed = (await acquisitions.GetAsync(completed.Namespace, completed.Name, token)).Value;
            }

            if (completed.State != DataSourceAcquisitionState.Succeeded)
                throw new ToolDefinitionInvocationException(
                    completed.State == DataSourceAcquisitionState.WaitingForInput
                        ? "tool_definition_input_required" : "tool_definition_acquisition_failed",
                    completed.ErrorMessage ?? $"Acquisition '{completed.Name}' ended with status '{completed.State}'.");

            var candidates = completed.Manifest?.Artifacts.Where(value =>
                ToolDefinitionAcquisitionFactory.MatchesArtifact(target, value))
                .Take(2).ToArray() ?? [];
            if (candidates.Length != 1)
                throw new ToolDefinitionInvocationException("tool_definition_acquisition_artifact_unresolved",
                    "The acquisition did not produce exactly one publishable artifact matching the Tool output.");

            var artifact = candidates[0];
            using (requestScopes.Push(context))
                await EnsureReadableAsync(artifact, token);
            var output = JsonSerializer.SerializeToElement(new
            {
                artifact = new { artifact.ArtifactId, kind = target.ArtifactKind, artifact.Name, artifact.MediaType },
                acquisitionId = completed.Name
            }, OutputJson);
            var flow = completed.Composition!.Flow;
            return new(output, new ToolDefinitionOperationReceipt(
                null, completed.FlowRunId, flow.Name, flow.Namespace,
                flow.Version, completed.CorrelationId, false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolDefinitionInvocationException("tool_definition_timed_out",
                $"ToolDefinition '{definition.Address}' exceeded its execution timeout.");
        }
        catch (DataSourceAcquisitionException exception)
        {
            throw new ToolDefinitionInvocationException(exception.Code, exception.Message, exception);
        }
        catch (DataSourceValidationException exception)
        {
            throw new ToolDefinitionInvocationException(exception.Code, exception.Message, exception);
        }
        catch (ResourceScopeAccessDeniedException exception)
        {
            throw new ToolDefinitionInvocationException("tool_definition_scope_mismatch", exception.Message, exception);
        }
    }

    private RequestContext? MatchingContext(ToolDefinitionInvocation invocation)
    {
        if (requestContext.AccessMode is not (ControlPlaneAccessMode.Workspace or ControlPlaneAccessMode.Instance
            or ControlPlaneAccessMode.TenantAdministration))
            return null;
        var context = requestContext.Current;
        return context.TenantId != Guid.Empty && context.TenantId == invocation.TenantId
            && context.WorkspaceId != Guid.Empty && context.WorkspaceId == invocation.WorkspaceId.Value
            && context.PrincipalId != Guid.Empty && context.PrincipalId == invocation.PrincipalId
            ? context : null;
    }

    private async Task EnsureReadableAsync(DataSourceAcquisitionArtifact artifact, CancellationToken cancellationToken)
    {
        var available = artifact.Kind switch
        {
            DataSourceArtifactKind.Staged =>
                await artifacts.GetStagedAsync(StagedArtifactId.Parse(artifact.ArtifactId), null,
                    ArtifactLeaseOperation.Read, cancellationToken) is { Value: var staged }
                && staged.ExpiresAt > timeProvider.GetUtcNow()
                && staged.ArtifactStatus is StagedArtifactStatus.Sealed or StagedArtifactStatus.Persisted,
            DataSourceArtifactKind.Durable =>
                await artifacts.GetFlowRunArtifactForContentReadAsync(FlowRunArtifactId.Parse(artifact.ArtifactId),
                    false, cancellationToken) is not null,
            _ => false
        };
        if (!available)
            throw new ToolDefinitionInvocationException("tool_definition_acquisition_artifact_unavailable",
                "The artifact returned by the acquisition is unavailable.");
    }
}
