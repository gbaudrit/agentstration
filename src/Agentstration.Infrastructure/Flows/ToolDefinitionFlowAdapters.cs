using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Flows;

public sealed class ToolDefinitionFlowResolver(FlowService flows) : IToolDefinitionFlowResolver
{
    public async Task<ResolvedToolDefinitionFlowContract> ResolveAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace ownerNamespace,
        ToolDefinitionFlowTarget target,
        CancellationToken cancellationToken)
    {
        if (scopeRef.Kind != ResourceScopeKind.Workspace || scopeRef.TargetId is not { } workspaceId)
            throw new ToolDefinitionValidationException("tool_definition_scope_invalid", "A ToolDefinition must belong to a Workspace scope.");
        var flowNamespace = target.Namespace ?? ownerNamespace;
        var reference = new FlowReference(new FlowId(target.Name, flowNamespace), target.Version, target.UseActiveVersion, flowNamespace);
        var resolved = await flows.ResolveAsync(new WorkspaceId(workspaceId), reference, ownerNamespace, cancellationToken);
        return Contract(resolved);
    }

    internal static ResolvedToolDefinitionFlowContract Contract(FlowVersion version) => new(
        version.FlowId.Value,
        version.FlowId.Namespace,
        version.Version,
        version.Graph?.InputSchema?.Clone(),
        version.Graph?.OutputSchema?.Clone());
}

public sealed class ToolDefinitionFlowActivationGuard(
    IResourceStore store,
    IRequestContextScopeFactory requestScopes) : IFlowVersionActivationGuard
{
    public async Task ValidateActivationAsync(WorkspaceId workspaceId, FlowVersion version, CancellationToken cancellationToken)
    {
        using var requestScope = requestScopes.PushSystem();
        var definitions = await store.ListAllAsync<ToolDefinitionResource>(ResourceKinds.ToolDefinition, cancellationToken);
        var contract = ToolDefinitionFlowResolver.Contract(version);
        foreach (var stored in definitions.Where(value =>
                     value.Value.ScopeRef is { Kind: ResourceScopeKind.Workspace, TargetId: { } targetId }
                     && targetId == workspaceId.Value
                     && value.Value.Definition.Enabled
                     && value.Value.Definition.Flow.UseActiveVersion
                     && string.Equals(value.Value.Definition.Flow.Name, version.FlowId.Value, StringComparison.Ordinal)
                     && (value.Value.Definition.Flow.Namespace ?? value.Value.Namespace) == version.FlowId.Namespace))
        {
            try { ToolDefinitionService.ValidateContract(stored.Value.Definition, contract); }
            catch (ToolDefinitionValidationException exception)
            {
                throw new FlowValidationException("flow_tool_definition_contract_incompatible", $"Flow version '{version.FlowId}:{version.Version}' is incompatible with enabled ToolDefinition '{stored.Value.Address}': {exception.Message}");
            }
        }
    }
}

public sealed class ToolDefinitionFlowDeletionGuard(
    IResourceStore store,
    IRequestContextScopeFactory requestScopes) : IFlowDeletionGuard
{
    public async Task ValidateDeleteAsync(WorkspaceId workspaceId, FlowId flowId, CancellationToken cancellationToken)
    {
        using var requestScope = requestScopes.PushSystem();
        var definitions = await store.ListAllAsync<ToolDefinitionResource>(ResourceKinds.ToolDefinition, cancellationToken);
        var usage = definitions.Select(value => value.Value).FirstOrDefault(value =>
            value.ScopeRef is { Kind: ResourceScopeKind.Workspace, TargetId: { } targetId }
            && targetId == workspaceId.Value
            && string.Equals(value.Definition.Flow.Name, flowId.Value, StringComparison.Ordinal)
            && (value.Definition.Flow.Namespace ?? value.Namespace) == flowId.Namespace);
        if (usage is not null)
            throw new FlowValidationException("flow_in_use_by_tool_definition", $"Flow '{flowId}' is referenced by ToolDefinition '{usage.Address}'.");
    }
}

public sealed class ToolDefinitionExecutor(
    ToolDefinitionService definitions,
    RootFlowSubmissionService submissions,
    FlowRunService runs) : IToolDefinitionExecutor
{
    public async Task<ToolDefinitionInvocationResult> ExecuteAsync(ToolDefinitionInvocation invocation, CancellationToken cancellationToken)
    {
        var stored = await definitions.GetAsync(invocation.ToolName, invocation.Namespace, cancellationToken)
            ?? throw new ToolDefinitionInvocationException("tool_definition_not_found", $"ToolDefinition '{invocation.Namespace}/{invocation.ToolName}' was not found.");
        if (!stored.Value.Definition.Enabled)
            throw new ToolDefinitionInvocationException("tool_definition_disabled", $"ToolDefinition '{stored.Value.Address}' is disabled.");
        if (stored.Value.ScopeRef is not { Kind: ResourceScopeKind.Workspace, TargetId: { } ownerWorkspace }
            || ownerWorkspace != invocation.WorkspaceId.Value)
            throw new ToolDefinitionInvocationException("tool_definition_scope_mismatch", "The ToolDefinition is not owned by the invocation Workspace.");
        if (invocation.Arguments.GetRawText().Length > 65_536)
            throw new ToolDefinitionInvocationException("tool_definition_input_too_large", "ToolDefinition input cannot exceed 65536 JSON characters.");
        try { FlowRunService.ValidateInput(stored.Value.Definition.InputSchema, invocation.Arguments); }
        catch (FlowValidationException exception)
        {
            throw new ToolDefinitionInvocationException("tool_definition_input_invalid", exception.Message, exception);
        }

        var target = stored.Value.Definition.Flow;
        var flowNamespace = target.Namespace ?? stored.Value.Namespace;
        var scope = new FlowRunScope(invocation.TenantId, invocation.WorkspaceId, invocation.PrincipalId);
        var origin = invocation.CallerKind == ToolDefinitionCallerKind.Agent ? FlowInvocationOrigin.Agent : FlowInvocationOrigin.Mcp;
        var callerId = invocation.CallerId ?? invocation.PrincipalId.ToString("D");
        RootFlowSubmission submission;
        try
        {
            submission = await submissions.SubmitAsync(new SubmitRootFlowCommand(
                invocation.WorkspaceId,
                new FlowReference(new FlowId(target.Name, flowNamespace), target.Version, target.UseActiveVersion, flowNamespace),
                invocation.Arguments,
                origin,
                callerId,
                invocation.CallerKind == ToolDefinitionCallerKind.Flow ? FlowRunTrigger.Flow : FlowRunTrigger.Api,
                $"tool:{stored.Value.Uid:N}:{invocation.CallId}",
                invocation.CallId,
                invocation.CorrelationId,
                Type: "tool-definition",
                Instruction: $"Execute ToolDefinition '{stored.Value.Address}'.",
                Title: stored.Value.Definition.DisplayName,
                Metadata: new Dictionary<string, string>
                {
                    ["toolDefinition.uid"] = stored.Value.Uid.ToString("D"),
                    ["toolDefinition.name"] = stored.Value.Name,
                    ["toolDefinition.namespace"] = stored.Value.Namespace.Value,
                    ["toolDefinition.callId"] = invocation.CallId
                }), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ToolDefinitionInvocationException("tool_definition_submission_failed", exception.Message, exception);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(stored.Value.Definition.InvocationTimeoutSeconds));
        StoredFlowRun completed;
        try
        {
            await runs.ExecuteAsync(new FlowRunQueueItem(submission.FlowRun.Run.Id, scope), timeout.Token);
            completed = await AwaitCompletionAsync(submission.FlowRun.Run.Id, scope, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolDefinitionInvocationException("tool_definition_timed_out", $"ToolDefinition '{stored.Value.Address}' exceeded its execution timeout.");
        }

        if (completed.Value.Status != FlowRunStatus.Succeeded)
            throw new ToolDefinitionInvocationException(
                completed.Value.Status == FlowRunStatus.WaitingForInput ? "tool_definition_input_required" : "tool_definition_execution_failed",
                completed.Value.Error?.Message ?? $"ToolDefinition Flow Run ended with status '{completed.Value.Status}'.");
        var output = completed.Value.Output?.Clone();
        if (output is null && stored.Value.Definition.OutputSchema is not null)
            throw new ToolDefinitionInvocationException("tool_definition_output_invalid", "The Flow returned no output for a ToolDefinition with an output schema.");
        if (output is not null)
        {
            try { FlowRunService.ValidateInput(stored.Value.Definition.OutputSchema, output.Value); }
            catch (FlowValidationException exception)
            {
                throw new ToolDefinitionInvocationException("tool_definition_output_invalid", exception.Message, exception);
            }
        }

        return new(output, new ToolDefinitionOperationReceipt(
            submission.WorkItem.Value.Id.Value.ToString("D"),
            completed.Value.Id,
            completed.Value.FlowId.Value,
            completed.Value.FlowId.Namespace,
            completed.Value.FlowVersion,
            completed.Value.CorrelationId ?? string.Empty,
            submission.Recovered));
    }

    private async Task<StoredFlowRun> AwaitCompletionAsync(string runId, FlowRunScope scope, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stored = await runs.GetAsync(runId, scope, cancellationToken)
                ?? throw new ToolDefinitionInvocationException("tool_definition_flow_run_missing", "The ToolDefinition Flow Run could not be reloaded.");
            if (stored.Value.Status.IsTerminal() || stored.Value.Status == FlowRunStatus.WaitingForInput) return stored;
            if (stored.Value.Status == FlowRunStatus.WaitingForChild
                && stored.Value.Steps.SingleOrDefault(step => step.ChildFlowRunId is not null)?.ChildFlowRunId is { } childRunId)
            {
                await runs.ExecuteAsync(new FlowRunQueueItem(childRunId, scope), cancellationToken);
                await runs.ExecuteAsync(new FlowRunQueueItem(runId, scope), cancellationToken);
                continue;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }
}
