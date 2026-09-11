using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.AgentFramework;
using Agentstration.Tools.Mcp;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Flows;

public sealed class LocalFlowRunQueue : IFlowRunQueue
{
    private readonly Channel<FlowRunQueueItem> channel = Channel.CreateBounded<FlowRunQueueItem>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(FlowRunQueueItem item, CancellationToken cancellationToken) => channel.Writer.WriteAsync(item, cancellationToken);

    public async IAsyncEnumerable<FlowRunQueueItem> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken)) yield return item;
    }
}

public sealed class WorkspaceFlowRunExecutionScope(
    IIdentityStore identities,
    Agentstration.Management.Abstractions.IAuthorizationService authorization,
    IRequestContextScopeFactory scopeFactory) : IFlowRunExecutionScope
{
    public async ValueTask ValidateAsync(FlowRunScope scope, CancellationToken cancellationToken)
    {
        var principal = await identities.GetPrincipalAsync(scope.PrincipalId, cancellationToken);
        var workspace = await identities.GetWorkspaceAsync(scope.TenantId, scope.WorkspaceId.Value, cancellationToken);
        if (principal?.Status != PrincipalStatus.Active || workspace?.Status != WorkspaceStatus.Active)
            throw Denied();

        var requestContext = new RequestContext(scope.PrincipalId, scope.TenantId, scope.WorkspaceId.Value);
        try
        {
            await authorization.EnsurePermissionAsync(requestContext, AuthorizationPermissions.RunsExecute, cancellationToken);
        }
        catch (AuthorizationDeniedException)
        {
            throw Denied();
        }
    }

    public IDisposable Enter(FlowRunScope scope) =>
        scopeFactory.Push(new RequestContext(scope.PrincipalId, scope.TenantId, scope.WorkspaceId.Value));

    private static FlowValidationException Denied() =>
        new("flow_run_authorization_denied", "The Principal is no longer authorized to execute this Flow Run in its Workspace.");
}

public sealed class CurrentWorkExecutionScopeAccessor(ICurrentRequestContext requestContext) : IWorkExecutionScopeAccessor
{
    public FlowRunScope? Current => requestContext.IsInitialized
        ? new FlowRunScope(requestContext.Current.TenantId, new WorkspaceId(requestContext.Current.WorkspaceId), requestContext.Current.PrincipalId)
        : null;
}

public sealed class LocalFlowRunCancellationRegistry : IFlowRunCancellationRegistry
{
    private readonly ConcurrentDictionary<FlowRunKey, CancellationTokenSource> sources = new();

    public CancellationToken Register(FlowRunKey key, CancellationToken stoppingToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!sources.TryAdd(key, source))
        {
            source.Dispose();
            return sources[key].Token;
        }
        return source.Token;
    }

    public bool Cancel(FlowRunKey key) => sources.TryGetValue(key, out var source) && TryCancel(source);

    public void Complete(FlowRunKey key)
    {
        if (sources.TryRemove(key, out var source)) source.Dispose();
    }

    private static bool TryCancel(CancellationTokenSource source)
    {
        try { source.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }
}

public sealed class ManagedFlowAgentExecutor(
    AgentExecutionCoordinator execution,
    IControlPlaneStore store,
    IAgentResourceQueries agentQueries,
    AgentManagementService agents) : IFlowAgentExecutor
{
    public async Task<FlowAgentExecutionResult> ExecuteAsync(FlowTargetReference target, JsonElement input, string correlationId, CancellationToken cancellationToken)
    {
        if (target.Kind != FlowTargetKind.Agent)
            throw new FlowValidationException("flow_target_kind_unsupported", "Flow Runs currently execute explicit Agent targets.");

        var prompt = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("prompt", out var promptProperty) && promptProperty.ValueKind == JsonValueKind.String
            ? promptProperty.GetString()!
            : input.GetRawText();
        var targetNamespace = target.Namespace ?? Agentstration.Resources.ResourceNamespace.Default;
        var agent = await agents.GetAgentAsync(targetNamespace, ResourceName(target.Id), cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Agent, ResourceName(target.Id), targetNamespace));
        var prepared = await agents.PrepareLocalRuntimeAsync(targetNamespace, agent.Value.Metadata.Name, agent.Value.Generation, cancellationToken);
        if (prepared.Value.OperationalState != OperationalState.Ready)
            throw new InvalidOperationException(prepared.Value.LastError ?? $"Agent '{target.Id}' could not be prepared for local execution.");
        var selected = await execution.SelectAgentAsync(prompt, ResourceName(target.Id), targetNamespace, cancellationToken);
        var deployment = (await agentQueries.ListDeploymentsAsync(cancellationToken))
            .SingleOrDefault(value => value.Value.AgentNamespace == targetNamespace && value.Value.Uid.ToString("N") == selected.DeploymentId)
            ?? throw new InvalidOperationException("The selected agent deployment no longer exists.");
        var revision = await store.GetAsync<AgentRevision>(new ResourceKey(ResourceKinds.AgentRevision, deployment.Value.RevisionName, targetNamespace), cancellationToken)
            ?? throw new InvalidOperationException("The selected agent revision no longer exists.");
        var result = await execution.ExecuteSelectedAsync(selected, prompt, cancellationToken);
        return new FlowAgentExecutionResult(
            JsonSerializer.SerializeToElement(result.Output),
            revision.Value.AgentName,
            revision.Value.AgentVersion,
            deployment.Value.ModelProfileName ?? revision.Value.Definition.ModelProfileName,
            result.ProviderType,
            result.Usage is null ? null : new FlowStepRunUsage(result.Usage.InputTokens, result.Usage.OutputTokens),
            revision.Value.Definition.EffectiveToolNames.ToArray(),
            [$"Runtime deployment {deployment.Value.Uid} executed for correlation {correlationId}.", $"Model: {result.ModelName ?? "unspecified"}."]);
    }

    private static string ResourceName(string id) => id;
}

public sealed class ManagedFlowOrchestrationEngine(
    AgentFrameworkFlowOrchestrationEngine inner,
    AgentManagementService agents) : IFlowOrchestrationEngine
{
    public async IAsyncEnumerable<FlowExecutionEvent> ExecuteAsync(
        FlowOrchestrationExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var references = request.Definition.Participants.AsEnumerable();
        if (request.Definition.Pattern is MagenticOrchestrationPattern magentic)
            references = references.Append(magentic.Manager);

        foreach (var reference in references.DistinctBy(target => (target.Namespace, target.Id)))
        {
            var targetNamespace = reference.Namespace ?? Agentstration.Resources.ResourceNamespace.Default;
            var agent = await agents.GetAgentAsync(targetNamespace, reference.Id, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Agent, reference.Id, targetNamespace));
            var prepared = await agents.PrepareLocalRuntimeAsync(targetNamespace, agent.Value.Metadata.Name, agent.Value.Generation, cancellationToken);
            if (prepared.Value.OperationalState != OperationalState.Ready)
                throw new InvalidOperationException(prepared.Value.LastError ?? $"Agent '{targetNamespace}/{reference.Id}' could not be prepared for local execution.");
        }

        await foreach (var executionEvent in inner.ExecuteAsync(request, cancellationToken))
            yield return executionEvent;
    }
}

public sealed class ManagementFlowResourceReferenceResolver(IControlPlaneStore store, IFlowRepository flows) : IFlowResourceReferenceResolver
{
    public async Task<bool> ExistsAsync(string resourceId, CancellationToken cancellationToken) =>
        await store.GetAsync<AgentResource>(new ResourceKey(ResourceKinds.Agent, resourceId), cancellationToken) is not null;

    public async Task<ResolvedFlowCall?> ResolveFlowAsync(
        WorkspaceId workspaceId,
        ResourceNamespace ownerNamespace,
        FlowCallReference reference,
        CancellationToken cancellationToken)
    {
        var id = reference.Resolve(ownerNamespace);
        var version = reference.Version;
        if (reference.VersionStrategy == FlowCallVersionStrategy.Active)
        {
            var current = await flows.GetAsync(workspaceId, id, cancellationToken);
            if (current is null || !current.Value.Enabled || current.Value.ActiveVersion is null) return null;
            version = current.Value.ActiveVersion;
        }
        else if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var published = await flows.GetVersionAsync(workspaceId, id, version, cancellationToken);
        return published is null
            ? null
            : new ResolvedFlowCall(id, published.Value.Version, published.Value.Graph?.InputSchema, published.Value.Graph?.OutputSchema);
    }

    public async Task<ResolvedFlowTool?> ResolveToolAsync(
        WorkspaceId workspaceId,
        ResourceNamespace ownerNamespace,
        FlowToolReference reference,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        var toolNamespace = reference.ResolveNamespace(ownerNamespace);
        var stored = await store.GetAsync<ToolResource>(
            new ResourceKey(ResourceKinds.Tool, reference.ResourceId, toolNamespace),
            cancellationToken);
        if (stored is null || stored.Value.Definition.Schema is null) return null;
        var tool = stored.Value;
        return new ResolvedFlowTool(
            tool.Metadata.Name,
            tool.Metadata.Namespace,
            tool.Definition.Schema.Input.Clone(),
            tool.Definition.Schema.Output?.Clone(),
            tool.Definition.Enabled,
            tool.Definition.Discovery?.Available == true,
            tool.Definition.RequiresApproval);
    }

    public async Task<bool> CreatesFlowCycleAsync(
        WorkspaceId workspaceId,
        FlowId ownerFlowId,
        ResolvedFlowCall target,
        CancellationToken cancellationToken)
    {
        var pending = new Queue<(FlowId Id, string Version)>();
        var visited = new HashSet<(FlowId Id, string Version)>();
        pending.Enqueue((target.FlowId, target.Version));
        while (pending.TryDequeue(out var current))
        {
            if (current.Id == ownerFlowId) return true;
            if (!visited.Add(current)) continue;
            var published = await flows.GetVersionAsync(workspaceId, current.Id, current.Version, cancellationToken);
            if (published?.Value.Graph is null) continue;
            foreach (var call in published.Value.Graph.Steps.OfType<FlowCallStepDefinition>())
            {
                var resolved = await ResolveFlowAsync(workspaceId, current.Id.Namespace, call.Flow, cancellationToken);
                if (resolved is not null) pending.Enqueue((resolved.FlowId, resolved.Version));
            }
        }
        return false;
    }
}

public sealed class ManagedFlowToolExecutor(
    IControlPlaneStore store,
    IToolExecutionPipeline pipeline) : IFlowToolExecutor
{
    public async Task<JsonElement?> ExecuteAsync(
        FlowToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var toolNamespace = request.Tool.ResolveNamespace(request.OwnerFlowId.Namespace);
        var stored = await store.GetAsync<ToolResource>(
            new ResourceKey(ResourceKinds.Tool, request.Tool.ResourceId, toolNamespace),
            cancellationToken);
        if (stored is null)
            throw Error("tool_not_found", $"Tool resource '{toolNamespace}/{request.Tool.ResourceId}' was not found.");

        var tool = stored.Value;
        if (!tool.Definition.Enabled)
            throw Error("tool_disabled", $"Tool resource '{tool.Address}' is disabled.");
        if (tool.Definition.Discovery?.Available != true)
            throw Error("tool_unavailable", $"Tool resource '{tool.Address}' is no longer available from its provider.");
        if (tool.Definition.RequiresApproval)
            throw Error("tool_approval_required", $"Tool resource '{tool.Address}' requires approval and cannot be invoked without an approval response.");
        var providerId = tool.Definition.Provider?.Name
            ?? throw Error("tool_mapping_invalid", $"Tool resource '{tool.Address}' has no ToolProvider mapping.");
        var externalId = tool.Definition.ExternalId
            ?? throw Error("tool_mapping_invalid", $"Tool resource '{tool.Address}' has no external Tool identity.");
        var schema = tool.Definition.Schema?.Input
            ?? throw Error("tool_schema_missing", $"Tool resource '{tool.Address}' has no input schema.");
        ValidateArguments(request.Arguments, schema);

        var logicalCallId = $"flow:{request.RunId}:step:{request.StepName}";
        var invocationId = $"{logicalCallId}:attempt:{request.Attempt}";
        try
        {
            var result = await pipeline.ExecuteAsync(new ToolExecutionContext
            {
                OwnerKind = ToolExecutionOwnerKind.FlowRun,
                ToolCallId = logicalCallId,
                InvocationId = invocationId,
                ToolId = tool.Metadata.Name,
                ToolNamespace = tool.Metadata.Namespace,
                ToolName = externalId,
                ToolProviderId = providerId,
                ToolProviderNamespace = tool.Definition.Provider!.Namespace ?? tool.Metadata.Namespace,
                ExternalToolId = externalId,
                TenantId = request.Scope.TenantId,
                WorkspaceId = request.Scope.WorkspaceId,
                PrincipalId = request.Scope.PrincipalId,
                RunId = request.RunId,
                FlowStepId = request.StepName,
                CorrelationId = request.CorrelationId,
                Arguments = request.Arguments.Clone()
            }, cancellationToken);
            return result?.Clone();
        }
        catch (ToolExecutionDeniedException exception)
        {
            throw Error(exception.Code, exception.Message, exception);
        }
        catch (ToolResolutionException exception)
        {
            throw Error(exception.Code, exception.Message, exception);
        }
    }

    private static void ValidateArguments(JsonElement arguments, JsonElement schema)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw Error("tool_arguments_object_required", "Tool arguments must be a JSON object.");
        if (schema.ValueKind != JsonValueKind.Object)
            throw Error("tool_input_schema_invalid", "The Tool input schema must be a JSON object schema.");

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            foreach (var item in required.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String))
                if (!arguments.TryGetProperty(item.GetString()!, out _))
                    throw Error("tool_argument_required", $"Tool argument '{item.GetString()}' is required.");

        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return;
        var schemas = properties.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        foreach (var argument in arguments.EnumerateObject())
        {
            if (!schemas.TryGetValue(argument.Name, out var propertySchema))
                throw Error("tool_argument_unknown", $"Tool argument '{argument.Name}' is not declared by the Tool schema.");
            if (!propertySchema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) continue;
            var valid = type.GetString() switch
            {
                "string" => argument.Value.ValueKind == JsonValueKind.String,
                "number" => argument.Value.ValueKind == JsonValueKind.Number,
                "integer" => argument.Value.ValueKind == JsonValueKind.Number && argument.Value.TryGetInt64(out _),
                "boolean" => argument.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "object" => argument.Value.ValueKind == JsonValueKind.Object,
                "array" => argument.Value.ValueKind == JsonValueKind.Array,
                "null" => argument.Value.ValueKind == JsonValueKind.Null,
                _ => true
            };
            if (!valid)
                throw Error("tool_argument_type_invalid", $"Tool argument '{argument.Name}' does not match schema type '{type.GetString()}'.");
        }
    }

    private static FlowValidationException Error(string code, string message, Exception? exception = null) =>
        new(code, exception is null ? message : $"{message} ({exception.GetType().Name})");
}
