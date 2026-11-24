using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Application.Work;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Agentstration.Work;
using ModelContextProtocol;

namespace Agentstration.Infrastructure.Work;

public sealed class McpWorkTaskProviderGateway(IWorkProjectBindingResolver bindings, IResourceStore resources,
    IResourceAccessPolicyService access, IToolExecutionPipeline pipeline, ICurrentRequestContext current,
    IAuthorizationService authorization, TimeProvider clock) : IWorkTaskProviderGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    public async Task<Result<WorkTaskProviderRoute>> ResolveAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectContextBinding binding, ExternalTaskOperationKind operation, CancellationToken token)
    {
        if (current.Current.WorkspaceId != workspaceId.Value || current.Current.PrincipalId != principalId
            || !await authorization.HasPermissionAsync(current.Current, AuthorizationPermissions.RunsExecute, token)) return Failure("task_provider_denied");
        if (binding.Purpose != WorkProjectBindingPurpose.TaskOperations || binding.Target.Address.Kind != ToolResourceKinds.ToolProvider
            || !Enum.IsDefined(operation)) return Failure("task_provider_binding_invalid");
        var allowed = await bindings.ResolveAsync(workspaceId, principalId, binding, token);
        if (!allowed.IsSuccess) return Failure(allowed.ErrorCode!);
        var provider = await resources.GetExactAsync<ToolProviderResource>(binding.Target.Address, token);
        if (provider is null || !provider.Value.Definition.Enabled || provider.Value.Definition.Mcp?.Internal == true) return Failure("task_provider_unavailable");
        var consumer = ResourceScopeRef.Workspace(workspaceId.Value);
        var tools = await resources.ListVisibleAsync<ToolResource>(consumer, ToolResourceKinds.Tool, 0, 1_000, token);
        if (tools.Count == 1_000) return Failure("task_provider_discovery_limit");
        var candidates = new List<(ToolResource Tool, WorkTaskProviderCapabilities Capabilities)>();
        foreach (var stored in tools)
        {
            var tool = stored.Value;
            if (tool.Definition.Provider is not { } reference || reference.Name != provider.Value.Name
                || (reference.ScopeRef ?? tool.ScopeRef) != provider.Value.ScopeRef
                || (reference.Namespace ?? tool.Namespace) != provider.Value.Namespace
                || !tool.Definition.Enabled || tool.Definition.Discovery?.Available != true
                || !tool.Definition.Metadata.TryGetValue(WorkTaskProviderProtocol.MetadataKey, out var metadata)) continue;
            WorkTaskProviderCapabilities? capabilities;
            try { capabilities = metadata.Deserialize<WorkTaskProviderCapabilities>(Json); }
            catch (JsonException) { return Failure("task_provider_capabilities_invalid"); }
            if (capabilities is null || capabilities.Version != WorkTaskProviderProtocol.Version
                || capabilities.Operations is not { Count: >= 1 and <= 3 } || capabilities.Operations.Any(value => !Enum.IsDefined(value))
                || capabilities.Operations.Distinct().Count() != capabilities.Operations.Count
                || !capabilities.IdempotentOperations || !capabilities.OperationRecovery) return Failure("task_provider_capabilities_invalid");
            if (capabilities.Operations.Contains(operation)) candidates.Add((tool, capabilities));
        }
        if (candidates.Count != 1) return Failure(candidates.Count == 0 ? "task_provider_capability_missing" : "task_provider_capability_ambiguous");
        var selected = candidates[0];
        if (!selected.Tool.Definition.Enabled || selected.Tool.Definition.Discovery?.Available != true) return Failure("task_provider_tool_unavailable");
        if (selected.Tool.Definition.RequiresApproval) return Failure("task_provider_approval_required");
        if (selected.Tool.ScopeRef is not { } owner) return Failure("task_provider_tool_unavailable");
        var identity = new ResourceAccessIdentity(selected.Tool.Uid, owner, selected.Tool.Namespace, selected.Tool.Kind, selected.Tool.Generation);
        if (!(await access.EvaluateAsync(identity, consumer, ResourceAccessAction.Discover, token)).Allowed) return Failure("task_provider_tool_unavailable");
        if (!(await access.EvaluateAsync(identity, consumer, ResourceAccessAction.Use, token)).Allowed) return Failure("task_provider_denied");
        return Result<WorkTaskProviderRoute>.Success(new(binding, new(ScopedResourceAddress.Create(owner, selected.Tool.Namespace,
            selected.Tool.Kind, selected.Tool.Name), selected.Tool.Uid, selected.Tool.Generation), selected.Capabilities));
    }

    public async Task<ExternalTaskProviderResponse> InvokeAsync(WorkTaskOperation operation, bool recover, CancellationToken token)
    {
        ExternalTaskProviderResponse Unknown(string code) => new(operation.OperationKey, operation.IntentDigest, ExternalTaskProviderStatus.Uncertain, ErrorCode: code);
        if (current.Current.TenantId != operation.TenantId || current.Current.WorkspaceId != operation.WorkspaceId.Value
            || current.Current.PrincipalId != operation.OwnerPrincipalId) return Unknown("task_provider_denied");
        var selected = await ResolveAsync(operation.WorkspaceId, operation.OwnerPrincipalId, operation.Route.Binding, operation.Input.Operation, token);
        if (!selected.IsSuccess) return Unknown(selected.ErrorCode!);
        if (selected.Value!.Tool != operation.Route.Tool) return Unknown("task_provider_route_changed");
        var tool = (await resources.GetExactAsync<ToolResource>(operation.Route.Tool.Address, token))?.Value;
        if (tool is null || tool.Uid != operation.Route.Tool.Uid || tool.Generation != operation.Route.Tool.Generation) return Unknown("task_provider_route_changed");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var output = await pipeline.ExecuteAsync(new ToolExecutionContext
            {
                OwnerKind = ToolExecutionOwnerKind.WorkOperation,
                ToolCallId = operation.OperationKey,
                InvocationId = $"work-operation:{operation.Id}:{(recover ? "recover" : "execute")}",
                ToolId = tool.Name, ToolName = tool.Definition.ExternalId ?? tool.Name,
                ToolNamespace = tool.Namespace, ToolScopeRef = tool.ScopeRef, ToolUid = tool.Uid, ToolGeneration = tool.Generation,
                ToolProviderId = operation.Route.Binding.Target.Address.Name, ToolProviderNamespace = operation.Route.Binding.Target.Address.Namespace,
                ToolProviderScopeRef = operation.Route.Binding.Target.Address.ScopeRef, ToolProviderUid = operation.Route.Binding.Target.Uid,
                ToolProviderGeneration = operation.Route.Binding.Target.Generation, ExternalToolId = tool.Definition.ExternalId,
                TenantId = operation.TenantId, WorkspaceId = operation.WorkspaceId, PrincipalId = operation.OwnerPrincipalId,
                CorrelationId = $"work-operation:{operation.Id}", PersistArguments = false,
                Arguments = JsonSerializer.SerializeToElement(new ExternalTaskProviderRequest(recover ? "recover" : "execute",
                    operation.OperationKey, operation.IntentDigest, operation.Input), Json)
            }, linked.Token);
            if (output is null || Encoding.UTF8.GetByteCount(output.Value.GetRawText()) > 512 * 1024
                || output.Value.ValueKind != JsonValueKind.Object || !output.Value.TryGetProperty("structuredContent", out var content)
                || content.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(content.GetRawText()) > 256 * 1024)
                return Unknown("task_provider_response_invalid");
            return content.Deserialize<ExternalTaskProviderResponse>(Json) ?? Unknown("task_provider_response_invalid");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Unknown("task_provider_timeout"); }
        catch (ToolExecutionDeniedException) { return new(operation.OperationKey, operation.IntentDigest, ExternalTaskProviderStatus.Rejected, ErrorCode: "task_provider_governance_denied"); }
        catch (ToolInputValidationException) { return new(operation.OperationKey, operation.IntentDigest, ExternalTaskProviderStatus.Rejected, ErrorCode: "task_provider_contract_invalid"); }
        catch (JsonException) { return Unknown("task_provider_response_invalid"); }
        catch (HttpRequestException) { return Unknown("task_provider_unavailable"); }
        catch (McpException) { return Unknown("task_provider_outcome_uncertain"); }
        catch (ToolResolutionException) { return Unknown("task_provider_outcome_uncertain"); }
    }

    private static Result<WorkTaskProviderRoute> Failure(string code) => Result<WorkTaskProviderRoute>.Failure(code);
}
