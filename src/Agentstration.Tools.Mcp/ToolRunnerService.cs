using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools.Contracts;

namespace Agentstration.Tools.Mcp;

public sealed class ToolRunException(string code, int statusCode, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class ToolRunnerService(
    IResourceStore store,
    IToolExecutionPipeline executionPipeline,
    ICurrentRequestContext? current = null,
    IRequestContextScopeFactory? requestScopes = null)
{
    public Task<RunToolResponse> RunAsync(
        ResourceNamespace toolNamespace,
        string toolName,
        RunToolRequest request,
        RequestContext requestContext,
        CancellationToken cancellationToken) =>
        RunAsync(toolNamespace, toolName, request, requestContext, null, cancellationToken);

    public async Task<RunToolResponse> RunAsync(
        ResourceNamespace toolNamespace,
        string toolName,
        RunToolRequest request,
        RequestContext requestContext,
        ResourceScopeRef? scopeRef,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var workspaceScope = ResourceScopeRef.Workspace(requestContext.WorkspaceId);
        var administrativeScope = current?.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(requestContext.TenantId),
            _ => (ResourceScopeRef?)null
        };
        if (scopeRef is { } target && target != workspaceScope && target != administrativeScope)
            throw new ToolRunException("tool_scope_mismatch", 403, "The Tool does not belong to the current scope.");
        var (tool, provider) = await ResolveAsync(toolNamespace, toolName, requestContext, scopeRef, cancellationToken);

        var arguments = request.Arguments;
        if (request.Mode == ToolRunMode.Simulate)
        {
            if (!ToolDryRunContract.IsSupported(tool.Definition.Schema?.Input))
                throw new ToolRunException("tool_simulation_unavailable", 422, "This Tool does not declare a boolean 'dryRun' input parameter.");
            arguments = ToolDryRunContract.Enable(arguments);
        }
        ToolInputSchemaValidator.Validate(tool.Definition.Schema?.Input, arguments);
        var callId = Guid.NewGuid().ToString("N");
        var context = new ToolExecutionContext
        {
            OwnerKind = ToolExecutionOwnerKind.Console,
            ToolCallId = callId,
            InvocationId = $"{callId}:attempt:1",
            ToolId = tool.Name,
            ToolNamespace = tool.Namespace,
            ToolScopeRef = tool.ScopeRef,
            ToolUid = tool.Uid,
            ToolGeneration = tool.Generation,
            ToolName = tool.Definition.ExternalId ?? tool.Name,
            ToolProviderId = provider.Name,
            ToolProviderNamespace = provider.Namespace,
            ToolProviderScopeRef = provider.ScopeRef,
            ToolProviderUid = provider.Uid,
            ToolProviderGeneration = provider.Generation,
            ExternalToolId = tool.Definition.ExternalId,
            TenantId = requestContext.TenantId,
            WorkspaceId = new WorkspaceId(requestContext.WorkspaceId),
            PrincipalId = requestContext.PrincipalId,
            CorrelationId = callId,
            Arguments = arguments.Clone()
        };

        var checks = new List<ToolRunCheck>
        {
            new("tool_resolved", "passed", $"Tool '{tool.Address}' resolves to provider '{provider.Address}'."),
            new("input_valid", "passed", "Arguments match the Tool input contract."),
            new("approval", tool.Definition.RequiresApproval ? "required" : "notRequired",
                tool.Definition.RequiresApproval ? "Real execution requires approval." : "No approval is required.")
        };

        if (request.Mode == ToolRunMode.Simulate)
        {
            try
            {
                var simulation = await executionPipeline.SimulateAsync(context, cancellationToken);
                checks.AddRange(simulation.GovernanceEvaluations.Select(evaluation => new ToolRunCheck(
                    evaluation.Hook.Id,
                    evaluation.Decision == ToolExecutionHookEvaluationKind.Allowed ? "passed" : "denied",
                    evaluation.Decision == ToolExecutionHookEvaluationKind.Allowed
                        ? $"Governance check '{evaluation.Hook.Id}' allows execution."
                        : $"Governance check '{evaluation.Hook.Id}' denies execution.")));
                checks.Add(new("dry_run", "passed", "The Tool completed with dryRun enabled and created no execution resource."));
                return Response(ToolRunMode.Simulate, "simulated", true, checks, simulation.Output);
            }
            catch (ToolExecutionDeniedException exception)
            {
                checks.Add(new(exception.Code, "denied", exception.Message));
                checks.Add(new("no_execution", "passed", "The Tool was not invoked and no execution resource was created."));
                return Response(ToolRunMode.Simulate, "denied", false, checks);
            }
        }

        if (tool.Definition.RequiresApproval)
            throw new ToolRunException("tool_approval_required", 409, "This Tool requires approval and cannot be executed directly from the Console.");
        try
        {
            var result = await executionPipeline.ExecuteDetailedAsync(context, cancellationToken);
            checks.Add(new("execution", "passed", "The Tool execution completed."));
            return Response(
                ToolRunMode.Execute,
                "completed",
                true,
                checks,
                result.Output,
                Receipt(result.Receipt));
        }
        catch (ToolExecutionDeniedException exception)
        {
            throw new ToolRunException(exception.Code, 403, exception.Message, exception);
        }
        catch (ToolResolutionException exception)
        {
            throw new ToolRunException(exception.Code, 409, exception.Message, exception);
        }
        catch (ToolDefinitionInvocationException exception)
        {
            var status = exception.Code switch
            {
                "tool_definition_not_found" => 404,
                "tool_definition_timed_out" => 504,
                "tool_definition_scope_mismatch" or "data_source_access_denied" => 403,
                "data_source_acquisition_already_running" or "data_source_acquisition_idempotency_conflict" => 409,
                _ => 422
            };
            throw new ToolRunException(exception.Code, status, exception.Message, exception);
        }

        RunToolResponse Response(
            ToolRunMode mode,
            string status,
            bool providerInvoked,
            IReadOnlyList<ToolRunCheck> responseChecks,
            System.Text.Json.JsonElement? output = null,
            ToolRunReceipt? receipt = null) =>
            new(mode, status, tool.Name, tool.Namespace.Value, provider.Name, providerInvoked,
                tool.Definition.RequiresApproval, responseChecks, output, receipt);
    }

    private async Task<(ToolResource Tool, ToolProviderResource Provider)> ResolveAsync(
        ResourceNamespace toolNamespace, string toolName, RequestContext context, ResourceScopeRef? scopeRef,
        CancellationToken cancellationToken)
    {
        var workspaceScope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var ownerScope = scopeRef ?? workspaceScope;
        using var operationalScope = scopeRef == workspaceScope
            && current is { AccessMode: ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration }
            && current.Current == context && context.PrincipalId != Guid.Empty && context.TenantId != Guid.Empty
            ? requestScopes?.Push(context)
            : null;
        var tool = (await store.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(
            ownerScope, toolNamespace, ToolResourceKinds.Tool, toolName), cancellationToken))?.Value
            ?? throw new ToolRunException("tool_not_found", 404, $"Tool '{toolNamespace}/{toolName}' was not found.");
        EnsureOwnerScope(tool, ownerScope);
        if (ownerScope != workspaceScope)
        {
            var definition = await store.GetExactAsync<ToolDefinitionVersionResource>(ScopedResourceAddress.Create(
                ownerScope, toolNamespace, ToolResourceKinds.ToolDefinitionVersion, tool.Definition.ExternalId ?? toolName), cancellationToken);
            if (definition is null
                || AgentstrationToolProvider.ToolResourceName(definition.Value.Name) != tool.Name)
                throw new ToolRunException("tool_scope_mismatch", 403, "The Tool does not support administrative execution.");
        }
        if (!tool.Definition.Enabled)
            throw new ToolRunException("tool_disabled", 409, $"Tool '{tool.Address}' is disabled.");
        if (tool.Definition.Discovery?.Available != true)
            throw new ToolRunException("tool_unavailable", 409, $"Tool '{tool.Address}' is unavailable.");
        var providerReference = tool.Definition.Provider
            ?? throw new ToolRunException("tool_mapping_invalid", 422, $"Tool '{tool.Address}' has no provider mapping.");
        var providerNamespace = providerReference.Namespace ?? tool.Namespace;
        var providerScope = providerReference.ScopeRef ?? ownerScope;
        var provider = (await store.GetExactAsync<ToolProviderResource>(ScopedResourceAddress.Create(
            providerScope, providerNamespace, ToolResourceKinds.ToolProvider, providerReference.Name), cancellationToken))?.Value
            ?? throw new ToolRunException("tool_provider_not_found", 404, $"ToolProvider '{providerNamespace}/{providerReference.Name}' was not found.");
        EnsureOwnerScope(provider, ownerScope);
        if (!provider.Definition.Enabled)
            throw new ToolRunException("tool_provider_disabled", 409, $"ToolProvider '{provider.Address}' is disabled.");
        return (tool, provider);
    }

    private static ToolRunReceipt? Receipt(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null) return null;
        values.TryGetValue("workItemId", out var workItemId);
        values.TryGetValue("flowRunId", out var flowRunId);
        values.TryGetValue("correlationId", out var correlationId);
        ToolDefinitionRelayReceipt? relay = null;
        if (values.TryGetValue("relay.publicationUid", out var publicationUid)
            && values.TryGetValue("relay.version", out var version)
            && values.TryGetValue("relay.sourceTool", out var sourceTool)
            && values.TryGetValue("relay.sourceScope", out var sourceScope)
            && values.TryGetValue("relay.sourceUid", out var sourceUid)
            && values.TryGetValue("relay.sourceGeneration", out var sourceGeneration))
        {
            values.TryGetValue("relay.sourceVersion", out var sourceVersion);
            relay = new(Guid.Parse(publicationUid), version, sourceTool, ResourceScopeRef.Parse(sourceScope),
                Guid.Parse(sourceUid), long.Parse(sourceGeneration, System.Globalization.CultureInfo.InvariantCulture), sourceVersion);
        }
        return workItemId is null && flowRunId is null && correlationId is null
            && relay is null
            ? null
            : new ToolRunReceipt(workItemId, flowRunId, correlationId, relay);
    }

    private static void EnsureOwnerScope(Resource resource, ResourceScopeRef ownerScope)
    {
        if (resource.ScopeRef != ownerScope)
            throw new ToolRunException("tool_scope_mismatch", 403, $"Resource '{resource.Address}' does not belong to the current Tool scope.");
    }
}
