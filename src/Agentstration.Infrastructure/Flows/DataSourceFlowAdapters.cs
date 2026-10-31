using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Flows;

public sealed class DataSourceAcquisitionCompositionResolver(
    FlowService flows,
    IResourceStore store,
    IResourceReferenceResolver references,
    IResourceAccessPolicyService accessPolicies,
    ICurrentRequestContext requestContext,
    IRequestContextScopeFactory requestScopes) : IDataSourceAcquisitionCompositionResolver
{
    public async Task<ResolvedDataSourceFlowBinding> ResolveFlowAsync(
        ResourceScopeRef ownerScope,
        ResourceNamespace ownerNamespace,
        DataSourceProfileProperties definition,
        CancellationToken cancellationToken) =>
        await ResolveFlowCoreAsync(ownerScope, ownerNamespace, definition.AcquisitionFlow,
            "acquisition", cancellationToken);

    private async Task<ResolvedDataSourceFlowBinding> ResolveFlowCoreAsync(
        ResourceScopeRef ownerScope,
        ResourceNamespace ownerNamespace,
        DataSourceFlowTarget target,
        string purpose,
        CancellationToken cancellationToken)
    {
        var flowNamespace = target.Namespace ?? ownerNamespace;
        FlowVersion resolvedFlow;
        try
        {
            resolvedFlow = await flows.ResolveAsync(ownerScope,
                new FlowReference(new FlowId(target.Name, flowNamespace), target.Version,
                    target.UseActiveVersion, flowNamespace, target.ScopeRef), ownerNamespace, cancellationToken);
        }
        catch (Exception exception) when (exception is FlowNotFoundException or FlowValidationException or ArgumentException)
        {
            throw Error($"data_source_{purpose}_flow_unavailable",
                $"{purpose} Flow binding '{flowNamespace}/{target.Name}' is unavailable: {exception.Message}");
        }
        if (resolvedFlow.ScopeRef != ownerScope)
            throw Error("data_source_dependency_access_denied",
                $"Ancestor Flow '{resolvedFlow.ScopeRef}/{resolvedFlow.FlowId}' cannot be used until the Flow family exposes exact descendant-access authorization.");

        return new(
            resolvedFlow.FlowId.Value,
            resolvedFlow.FlowId.Namespace,
            resolvedFlow.Version,
            false,
            resolvedFlow.Graph?.InputSchema?.Clone(),
            resolvedFlow.Graph?.OutputSchema?.Clone(),
            resolvedFlow.Metadata.GetValueOrDefault(FlowMetadataKeys.Contract),
            resolvedFlow.DefinitionHash ?? (resolvedFlow.Graph is null
                ? FlowDefinitionHash.Compute(resolvedFlow.Definition)
                : FlowDefinitionHash.Compute(resolvedFlow.Graph)))
        { ScopeRef = resolvedFlow.ScopeRef };
    }

    public async Task<ResolvedDataSourceProfileDependencies> ResolveDependenciesAsync(
        ResourceScopeRef ownerScope,
        ResourceNamespace ownerNamespace,
        DataSourceProfileProperties definition,
        CancellationToken cancellationToken)
    {
        var acquisitionFlow = await ResolveFlowAsync(ownerScope, ownerNamespace, definition, cancellationToken);
        var acquisitionTools = await ResolveToolsAsync(ownerScope, ownerNamespace, definition.ToolBindings,
            "acquisition", cancellationToken);
        var discoveryFlow = definition.DiscoveryFlow is null
            ? null
            : await ResolveFlowCoreAsync(ownerScope, ownerNamespace, definition.DiscoveryFlow,
                "discovery", cancellationToken);
        var discoveryTools = await ResolveToolsAsync(ownerScope, ownerNamespace, definition.DiscoveryToolBindings,
            "discovery", cancellationToken);
        return new(acquisitionFlow, acquisitionTools, discoveryFlow, discoveryTools);
    }

    public async Task<ResolvedDataSourceDiscoveryComposition> ResolveDiscoveryAsync(
        ResourceScopeRef ownerScope,
        ResourceNamespace ownerNamespace,
        ResolvedDataSourceProfile profile,
        CancellationToken cancellationToken)
    {
        _ = ownerNamespace;
        if (profile.DiscoveryFlow is null)
            throw Error("data_source_discovery_not_supported", $"Profile '{profile.Name}' does not define discovery.");
        await ValidatePinnedAsync(ownerScope, profile, cancellationToken);
        return new()
        {
            Profile = profile,
            Flow = profile.DiscoveryFlow,
            Tools = profile.DiscoveryToolBindings,
            Limits = Clone(profile.Definition.Limits),
            Policies = Clone(profile.Definition.Policies)
        };
    }

    private async Task<IReadOnlyList<ResolvedDataSourceToolBinding>> ResolveToolsAsync(
        ResourceScopeRef ownerScope,
        ResourceNamespace ownerNamespace,
        IReadOnlyList<DataSourceProfileToolBinding> bindings,
        string purpose,
        CancellationToken cancellationToken)
    {
        var tools = new List<ResolvedDataSourceToolBinding>(bindings.Count);
        foreach (var binding in bindings)
        {
            var storedTool = await references.ResolveAsync<ToolResource>(binding.Tool, ownerNamespace,
                ToolResourceKinds.Tool, ownerScope, cancellationToken)
                ?? throw Error($"data_source_{purpose}_tool_missing",
                    $"Tool binding '{binding.Name}' cannot resolve Tool '{binding.Tool.Name}'.");
            var tool = storedTool.Value;
            var toolScope = tool.ScopeRef
                ?? throw Error($"data_source_{purpose}_tool_scope_missing", $"Tool '{tool.Address}' has no ownership scope.");
            await EnsureUseAsync(tool, toolScope, ownerScope, ToolResourceKinds.Tool, cancellationToken);
            if (!tool.Definition.Enabled || tool.Definition.Discovery?.Available != true)
                throw Error($"data_source_{purpose}_tool_unavailable", $"Tool '{tool.Address}' is unavailable.");
            var providerReference = tool.Definition.Provider
                ?? throw Error($"data_source_{purpose}_provider_missing", $"Tool '{tool.Address}' has no provider.");
            var storedProvider = await references.ResolveAsync<ToolProviderResource>(providerReference, tool.Namespace,
                ToolResourceKinds.ToolProvider, toolScope, cancellationToken)
                ?? throw Error($"data_source_{purpose}_provider_missing",
                    $"Provider for Tool '{tool.Address}' was not found.");
            var provider = storedProvider.Value;
            var providerScope = provider.ScopeRef
                ?? throw Error($"data_source_{purpose}_provider_scope_missing", $"Provider '{provider.Address}' has no ownership scope.");
            await EnsureUseAsync(provider, providerScope, toolScope, ToolResourceKinds.ToolProvider, cancellationToken);
            if (!provider.Definition.Enabled)
                throw Error($"data_source_{purpose}_provider_unavailable", $"Provider '{provider.Address}' is disabled.");
            if (binding.RequiredProvider is { } requiredProvider)
            {
                var requiredNamespace = requiredProvider.Namespace ?? tool.Namespace;
                if (!string.Equals(requiredProvider.Name, provider.Name, StringComparison.Ordinal)
                    || requiredNamespace != provider.Namespace
                    || requiredProvider.ScopeRef is { } requiredScope && requiredScope != provider.ScopeRef)
                    throw Error($"data_source_{purpose}_provider_incompatible",
                        $"Tool '{tool.Address}' resolved provider '{provider.Address}', which does not satisfy binding '{binding.Name}'.");
            }
            tools.Add(new()
            {
                BindingName = binding.Name,
                Capability = binding.Capability,
                ToolScopeRef = toolScope,
                ToolName = tool.Name,
                ToolNamespace = tool.Namespace,
                ToolUid = tool.Uid,
                ToolGeneration = tool.Generation,
                ProviderScopeRef = providerScope,
                ProviderName = provider.Name,
                ProviderNamespace = provider.Namespace,
                ProviderUid = provider.Uid,
                ProviderGeneration = provider.Generation,
                ExternalToolId = tool.Definition.ExternalId
                    ?? throw Error($"data_source_{purpose}_tool_identity_missing",
                        $"Tool '{tool.Address}' has no external identity.")
            });
        }

        return tools;
    }

    public async Task ValidatePinnedAsync(ResourceScopeRef ownerScope, ResolvedDataSourceProfile profile,
        CancellationToken cancellationToken)
    {
        _ = ownerScope;
        await ValidateFlowPinnedAsync(profile.AcquisitionFlow, "acquisition", cancellationToken);
        await ValidateToolsPinnedAsync(profile.ToolBindings, profile.ScopeRef, "acquisition", cancellationToken);

        if (profile.Definition.DiscoveryFlow is not null)
        {
            if (profile.DiscoveryFlow is null)
                throw Error("data_source_discovery_flow_stale", "The pinned discovery Flow is missing.");
            await ValidateFlowPinnedAsync(profile.DiscoveryFlow, "discovery", cancellationToken);
            await ValidateToolsPinnedAsync(profile.DiscoveryToolBindings, profile.ScopeRef, "discovery", cancellationToken);
        }
        else if (profile.DiscoveryFlow is not null || profile.DiscoveryToolBindings.Count != 0)
        {
            throw Error("data_source_discovery_composition_stale",
                "The pinned discovery composition does not match the profile definition.");
        }
    }

    private async Task ValidateFlowPinnedAsync(ResolvedDataSourceFlowBinding binding, string purpose,
        CancellationToken cancellationToken)
    {
        var flowScope = binding.ScopeRef
            ?? throw Error($"data_source_{purpose}_flow_scope_missing", $"The pinned {purpose} Flow has no ownership scope.");
        var flow = await flows.GetVersionExactAsync(flowScope,
            new FlowId(binding.Name, binding.Namespace), binding.Version, cancellationToken)
            ?? throw Error($"data_source_{purpose}_flow_stale", $"The pinned {purpose} Flow version no longer exists.");
        var flowHash = flow.Value.DefinitionHash ?? (flow.Value.Graph is null
            ? FlowDefinitionHash.Compute(flow.Value.Definition)
            : FlowDefinitionHash.Compute(flow.Value.Graph));
        if (!string.Equals(flowHash, binding.DefinitionHash, StringComparison.Ordinal))
            throw Error($"data_source_{purpose}_flow_stale",
                $"The pinned {purpose} Flow no longer matches its integrity evidence.");
    }

    private async Task ValidateToolsPinnedAsync(IReadOnlyList<ResolvedDataSourceToolBinding> bindings,
        ResourceScopeRef consumerScope, string purpose, CancellationToken cancellationToken)
    {
        foreach (var binding in bindings)
        {
            var tool = await GetExactDependencyAsync<ToolResource>(ScopedResourceAddress.Create(binding.ToolScopeRef,
                binding.ToolNamespace, ToolResourceKinds.Tool, binding.ToolName), consumerScope, cancellationToken);
            if (tool is null || tool.Value.Uid != binding.ToolUid || tool.Value.Generation != binding.ToolGeneration)
                throw Error($"data_source_{purpose}_tool_stale", $"Pinned Tool binding '{binding.BindingName}' is stale.");
            await EnsureUseAsync(tool.Value, binding.ToolScopeRef, consumerScope,
                ToolResourceKinds.Tool, cancellationToken);
            if (!tool.Value.Definition.Enabled || tool.Value.Definition.Discovery?.Available != true
                || !string.Equals(tool.Value.Definition.ExternalId, binding.ExternalToolId, StringComparison.Ordinal))
                throw Error($"data_source_{purpose}_tool_unavailable", $"Pinned Tool binding '{binding.BindingName}' is unavailable.");

            var provider = await GetExactDependencyAsync<ToolProviderResource>(ScopedResourceAddress.Create(
                binding.ProviderScopeRef, binding.ProviderNamespace, ToolResourceKinds.ToolProvider,
                binding.ProviderName), consumerScope, cancellationToken);
            if (provider is null || provider.Value.Uid != binding.ProviderUid
                || provider.Value.Generation != binding.ProviderGeneration)
                throw Error($"data_source_{purpose}_provider_stale", $"Pinned provider for binding '{binding.BindingName}' is stale.");
            await EnsureUseAsync(provider.Value, binding.ProviderScopeRef, binding.ToolScopeRef,
                ToolResourceKinds.ToolProvider, cancellationToken);
            if (!provider.Value.Definition.Enabled)
                throw Error($"data_source_{purpose}_provider_unavailable", $"Pinned provider for binding '{binding.BindingName}' is disabled.");
        }
    }

    private static IReadOnlyDictionary<string, System.Text.Json.JsonElement> Clone(
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> values) =>
        values.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal);

    private async Task EnsureUseAsync(Resource resource, ResourceScopeRef resourceScope,
        ResourceScopeRef consumerScope, string kind, CancellationToken cancellationToken)
    {
        if (resourceScope == consumerScope) return;
        var decision = await accessPolicies.EvaluateAsync(new ResourceAccessIdentity(resource.Uid,
            resourceScope, resource.Namespace, kind, resource.Generation), consumerScope,
            ResourceAccessAction.Use, cancellationToken);
        if (!decision.Allowed)
            throw Error("data_source_dependency_access_denied",
                $"Referenced {kind} '{resource.Address}' does not authorize use from '{consumerScope}' ({decision.ReasonCode}).");
    }

    private async Task<StoredResource<T>?> GetExactDependencyAsync<T>(
        ScopedResourceAddress address,
        ResourceScopeRef consumerScope,
        CancellationToken cancellationToken) where T : Resource
    {
        using var exactScope = requestContext.AccessMode == ControlPlaneAccessMode.Workspace
            ? address.ScopeRef.Kind switch
            {
                ResourceScopeKind.Instance => requestScopes.PushInstance(requestContext.Current),
                ResourceScopeKind.Tenant when address.ScopeRef.TargetId == requestContext.Current.TenantId =>
                    requestScopes.PushTenantAdministration(requestContext.Current),
                _ => null
            }
            : null;
        return await store.GetExactAsync<T>(address, cancellationToken);
    }

    private static DataSourceAcquisitionException Error(string code, string message) => new(code, message);
}

public sealed class DataSourceAcquisitionFlowGateway(FlowRunService runs) : IDataSourceAcquisitionFlowGateway
{
    public async Task<DataSourceFlowRunSnapshot> StartAsync(
        DataSourceFlowRunRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var stored = await runs.EnsureRootAsync(new EnsureRootFlowRunCommand(
                request.RunId,
                new FlowId(request.Flow.Name, request.Flow.Namespace),
                request.Flow.Version,
                "local",
                FlowRunTrigger.Api,
                FlowInvocationOrigin.Api,
                request.CallerId,
                null,
                request.IdempotencyKey,
                request.CorrelationId,
                request.Input,
                $"data-source-acquisition:{request.RunId}",
                request.Flow.UsesActiveVersion,
                null,
                null,
                null,
                null,
                Scope(request.WorkspaceId, request.TenantId, request.PrincipalId))
            {
                DefinitionScopeRef = request.Flow.ScopeRef
            }, cancellationToken);
            return Snapshot(stored.Value);
        }
        catch (Exception exception) when (exception is FlowValidationException or FlowNotFoundException)
        {
            throw new DataSourceAcquisitionException("data_source_acquisition_flow_rejected", exception.Message);
        }
    }

    public async Task<DataSourceFlowRunSnapshot?> GetAsync(
        Guid workspaceId,
        string runId,
        Guid tenantId,
        Guid principalId,
        CancellationToken cancellationToken)
    {
        var stored = await runs.GetAsync(runId, Scope(workspaceId, tenantId, principalId), cancellationToken);
        return stored is null ? null : Snapshot(stored.Value);
    }

    public async Task<DataSourceFlowRunSnapshot> CancelAsync(
        Guid workspaceId,
        string runId,
        Guid tenantId,
        Guid principalId,
        CancellationToken cancellationToken) => Snapshot((await runs.CancelAsync(
            runId, Scope(workspaceId, tenantId, principalId), cancellationToken)).Value);

    private static FlowRunScope Scope(Guid workspaceId, Guid tenantId, Guid principalId) =>
        new(tenantId, new WorkspaceId(workspaceId), principalId);
    private static DataSourceFlowRunSnapshot Snapshot(FlowRun run) => new(
        run.Id,
        run.Status switch
        {
            FlowRunStatus.Pending => DataSourceAcquisitionState.Pending,
            FlowRunStatus.Running => DataSourceAcquisitionState.Running,
            FlowRunStatus.WaitingForInput => DataSourceAcquisitionState.WaitingForInput,
            FlowRunStatus.WaitingForChild => DataSourceAcquisitionState.WaitingForChild,
            FlowRunStatus.Succeeded => DataSourceAcquisitionState.Succeeded,
            FlowRunStatus.Cancelled => DataSourceAcquisitionState.Cancelled,
            FlowRunStatus.TimedOut => DataSourceAcquisitionState.TimedOut,
            _ => DataSourceAcquisitionState.Failed
        },
        run.Output?.Clone(),
        run.Error?.Code,
        run.Error?.Message,
        run.CompletedAt);
}

public sealed class DataSourceArtifactReferenceValidator(IResourceStore store)
    : IDataSourceArtifactReferenceValidator
{
    public async Task ValidateAsync(
        Guid workspaceId,
        IReadOnlyList<DataSourceAcquisitionArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        foreach (var artifact in artifacts)
        {
            try
            {
                var exists = artifact.Kind switch
                {
                    DataSourceArtifactKind.Staged => await store.GetExactAsync<StagedArtifactResource>(
                        ScopedResourceAddress.Create(ResourceScopeRef.Workspace(workspaceId), ResourceNamespace.Default,
                            ArtifactResourceKinds.StagedArtifact, StagedArtifactId.Parse(artifact.ArtifactId).ToString()),
                        cancellationToken) is not null,
                    DataSourceArtifactKind.Durable => await store.GetExactAsync<FlowRunArtifactResource>(
                        ScopedResourceAddress.Create(ResourceScopeRef.Workspace(workspaceId), ResourceNamespace.Default,
                            ArtifactResourceKinds.FlowRunArtifact, FlowRunArtifactId.Parse(artifact.ArtifactId).ToString()),
                        cancellationToken) is not null,
                    _ => false
                };
                if (!exists)
                    throw new DataSourceAcquisitionException("data_source_acquisition_artifact_not_found",
                        $"Artifact '{artifact.ArtifactId}' was not found in Workspace '{workspaceId:D}'.");
            }
            catch (FormatException exception)
            {
                throw new DataSourceAcquisitionException("data_source_acquisition_artifact_invalid",
                    $"Artifact identity '{artifact.ArtifactId}' is invalid: {exception.Message}");
            }
        }
    }
}

public sealed class DataSourceFlowActivationGuard : IFlowVersionActivationGuard
{
    public Task ValidateActivationAsync(WorkspaceId workspaceId, FlowVersion version, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        cancellationToken.ThrowIfCancellationRequested();
        if (!version.Metadata.TryGetValue(FlowMetadataKeys.Contract, out var contract))
            return Task.CompletedTask;
        if (!contract.StartsWith("datasource.", StringComparison.Ordinal))
            return Task.CompletedTask;
        if (string.Equals(contract, DataSourceFlowContracts.Discovery, StringComparison.Ordinal))
        {
            Require(version.Graph?.InputSchema,
                ["dataSourceId", "dataSourceUid", "dataSourceGeneration", "profile", "sourceConfiguration",
                    "parameters", "caller", "correlationId", "discoveryId"], "input", contract);
            Require(version.Graph?.OutputSchema, ["choices"], "output", contract);
            return Task.CompletedTask;
        }
        if (!string.Equals(contract, DataSourceFlowContracts.Acquisition, StringComparison.Ordinal))
            throw new FlowValidationException("data_source_flow_contract_unknown",
                $"Data Source Flow contract '{contract}' is not supported.");
        Require(version.Graph?.InputSchema,
            ["dataSourceId", "dataSourceUid", "dataSourceGeneration", "profile", "sourceConfiguration",
                "parameters", "caller", "correlationId", "acquisitionId"], "input", contract);
        Require(version.Graph?.OutputSchema, ["artifacts"], "output", contract);
        return Task.CompletedTask;
    }

    private static void Require(System.Text.Json.JsonElement? schema, IReadOnlyList<string> names, string direction,
        string contract)
    {
        if (schema is not { ValueKind: System.Text.Json.JsonValueKind.Object }
            || !schema.Value.TryGetProperty("type", out var type) || type.GetString() != "object"
            || !schema.Value.TryGetProperty("properties", out var properties)
            || properties.ValueKind != System.Text.Json.JsonValueKind.Object
            || !schema.Value.TryGetProperty("required", out var required)
            || required.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw Invalid(direction, "must be an object schema with properties and required arrays", contract);
        var requiredNames = required.EnumerateArray()
            .Where(item => item.ValueKind == System.Text.Json.JsonValueKind.String)
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var name in names)
            if (!properties.TryGetProperty(name, out _) || !requiredNames.Contains(name))
                throw Invalid(direction, $"must require property '{name}'", contract);
    }

    private static FlowValidationException Invalid(string direction, string detail, string contract) =>
        new("data_source_flow_contract_invalid", $"The {contract} {direction} schema {detail}.");
}
