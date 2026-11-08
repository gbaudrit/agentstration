using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;

namespace Agentstration.Infrastructure.Flows;

public sealed class ToolRelayExecutor(
    ToolDefinitionService definitions,
    IToolExecutionPipeline pipeline,
    IParameterResolver parameters,
    ISecretResolver secrets,
    ICurrentRequestContext current)
{
    public async Task<ToolDefinitionInvocationResult> ExecuteAsync(ToolDefinitionResource definition,
        RelayToolDefinitionProperties relay, ToolDefinitionInvocation invocation, CancellationToken cancellationToken)
    {
        if (current.AccessMode is not (ControlPlaneAccessMode.Workspace or ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration)
            || current.Current.TenantId == Guid.Empty || current.Current.WorkspaceId == Guid.Empty || current.Current.PrincipalId == Guid.Empty
            || current.Current.TenantId != invocation.TenantId
            || current.Current.WorkspaceId != invocation.WorkspaceId.Value || current.Current.PrincipalId != invocation.PrincipalId)
            throw Error("tool_definition_scope_mismatch", "A relay requires the current trusted invocation scope.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(relay.InvocationTimeoutSeconds));
        var token = timeout.Token;
        try
        {
            ToolRelayArguments.ValidateCaller(invocation.Arguments, relay.Bindings);
            var publication = await definitions.GetPublicationAsync(definition.Name, definition.Namespace,
                definition.ScopeRef!.Value, token)
                ?? throw Error("tool_definition_not_found", "The relay publication was not found.");
            var pinned = publication.Value.Source ?? throw Error("tool_definition_target_invalid", "The relay has no pinned source.");
            var source = await definitions.ResolveRelaySourceAsync(relay.Target, definition.ScopeRef.Value, token);
            if (source.Provider.Uid != pinned.Provider.Uid || source.Provider.Generation != pinned.Provider.Generation
                || source.Tool.Definition.ExternalId != pinned.Tool.Definition.ExternalId)
                throw Error("tool_definition_target_stale", "The source provider no longer matches its published revision.");
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var binding in relay.Bindings)
            {
                switch (binding)
                {
                    case LiteralToolArgumentBinding literal:
                        values[binding.Path] = literal.Value.Clone();
                        break;
                    case ParameterToolArgumentBinding parameter:
                        await VerifyIdentityAsync(ScopedResourceAddress.Create(parameter.Parameter.ScopeRef,
                            parameter.Parameter.Address.Namespace, parameter.Parameter.Address.Kind, parameter.Parameter.Address.Name), binding.Path);
                        var resolved = await parameters.ResolveAsync(parameter.Parameter,
                            new(definition.ScopeRef.Value, definition.Address), token)
                            ?? throw Error("tool_definition_binding_unavailable", "A bound Parameter is unavailable.");
                        values[binding.Path] = resolved.Value.Clone();
                        break;
                    case SecretToolArgumentBinding secret:
                        if (secret.Secret.ScopeRef is not { } secretScope)
                            throw Error("tool_definition_binding_invalid", "A Secret binding requires an exact scope.");
                        await VerifyIdentityAsync(ScopedResourceAddress.Create(secretScope, secret.Secret.Address.Namespace,
                            secret.Secret.Address.Kind, secret.Secret.Address.Name), binding.Path);
                        using (var resolvedSecret = await secrets.ResolveAsync(secret.Secret,
                            new(definition.ScopeRef.Value, definition.Address), token)
                            ?? throw Error("tool_definition_binding_unavailable", "A bound Secret is unavailable."))
                            values[binding.Path] = JsonSerializer.SerializeToElement(new UTF8Encoding(false, true).GetString(resolvedSecret.Value.AccessValue().Span));
                        break;
                }
            }
            var arguments = ToolRelayArguments.Reconstruct(invocation.Arguments, relay.Bindings, values, pinned.Tool.Definition.Schema!.Input);
            var execution = invocation.ExecutionContext;
            var result = await pipeline.ExecuteDetailedAsync(new ToolExecutionContext
            {
                OwnerKind = invocation.CallerKind == ToolDefinitionCallerKind.Console ? ToolExecutionOwnerKind.Console
                    : invocation.CallerKind == ToolDefinitionCallerKind.Mcp ? ToolExecutionOwnerKind.Mcp
                    : invocation.CallerKind == ToolDefinitionCallerKind.Flow ? ToolExecutionOwnerKind.FlowRun : ToolExecutionOwnerKind.RuntimeRun,
                ToolCallId = $"{invocation.CallId}:relay:{publication.Value.Uid:N}",
                InvocationId = $"{execution?.InvocationId ?? invocation.CallId}:relay:{publication.Value.Uid:N}",
                ToolId = pinned.Tool.Name,
                ToolNamespace = pinned.Tool.Namespace,
                ToolScopeRef = pinned.Tool.ScopeRef,
                ToolUid = pinned.Tool.Uid,
                ToolGeneration = pinned.Tool.Generation,
                ToolName = pinned.Tool.Definition.ExternalId ?? pinned.Tool.Name,
                ExternalToolId = pinned.Tool.Definition.ExternalId,
                ToolProviderId = pinned.Provider.Name,
                ToolProviderNamespace = pinned.Provider.Namespace,
                ToolProviderScopeRef = pinned.Provider.ScopeRef,
                ToolProviderUid = pinned.Provider.Uid,
                ToolProviderGeneration = pinned.Provider.Generation,
                TenantId = invocation.TenantId,
                WorkspaceId = invocation.WorkspaceId,
                PrincipalId = invocation.PrincipalId,
                RunId = execution?.RuntimeRunId ?? execution?.FlowRunId,
                FlowStepId = execution?.FlowStepId,
                AgentId = execution?.AgentId,
                AgentRevisionId = execution?.AgentRevisionId,
                AgentVersion = execution?.AgentVersion,
                AgentGeneration = execution?.AgentGeneration,
                CorrelationId = invocation.CorrelationId,
                PersistArguments = false,
                HasSensitiveArguments = execution?.HasSensitiveArguments == true || relay.Bindings.Any(binding => binding is SecretToolArgumentBinding),
                Arguments = arguments
            }, token);
            string? workItemId = null, flowRunId = null;
            result.Receipt?.TryGetValue("workItemId", out workItemId);
            result.Receipt?.TryGetValue("flowRunId", out flowRunId);
            return new(result.Output, new(workItemId, flowRunId, null, definition.Namespace, null,
                invocation.CorrelationId ?? invocation.CallId, false, Relay: new(publication.Value.Uid, relay.Version,
                    pinned.Tool.Name, pinned.Tool.ScopeRef!.Value, pinned.Tool.Uid, pinned.Tool.Generation, pinned.Tool.Definition.Version)));

            async Task VerifyIdentityAsync(ScopedResourceAddress address, string path)
            {
                var expected = publication.Value.BindingIdentities.SingleOrDefault(value => value.Path == path);
                if (expected is null || await definitions.BindingIdentityAsync(address, token) != expected.Uid)
                    throw Error("tool_definition_binding_stale", "A bound resource was removed or replaced.");
            }
        }
        catch (ToolDefinitionValidationException exception) { throw Error(exception.Code, exception.Message); }
        catch (ParameterAccessDeniedException) { throw Error("tool_definition_binding_unavailable", "A bound Parameter is unavailable or unauthorized."); }
        catch (SecretResolutionException) { throw Error("tool_definition_binding_unavailable", "A bound Secret is unavailable or unauthorized."); }
        catch (ResourceScopeAccessDeniedException) { throw Error("tool_definition_scope_mismatch", "The source is outside the authorized execution scope."); }
        catch (ResourceReferenceOutsideScopeException) { throw Error("tool_definition_scope_mismatch", "The source is outside the authorized execution scope."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw Error("tool_definition_timed_out", "The relay exceeded its invocation timeout."); }
        catch (Exception exception) when (exception is not OperationCanceledException
            && (invocation.ExecutionContext?.HasSensitiveArguments == true || relay.Bindings.Any(binding => binding is SecretToolArgumentBinding)))
        {
            var code = exception switch
            {
                ToolDefinitionInvocationException invocationError => invocationError.Code,
                ToolResolutionException resolutionError => resolutionError.Code,
                ToolExecutionDeniedException denied => denied.Code,
                _ => "tool_definition_execution_failed"
            };
            throw Error(code, "The Tool invocation with protected inputs could not be completed.");
        }
    }

    private static ToolDefinitionInvocationException Error(string code, string message) => new(code, message);
}
