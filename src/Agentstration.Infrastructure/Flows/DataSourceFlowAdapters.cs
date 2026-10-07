using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Flows;

public sealed class DataSourceAcquisitionCompositionResolver(
    FlowService flows,
    IResourceReferenceResolver references) : IDataSourceAcquisitionCompositionResolver
{
    public async Task<ResolvedDataSourceAcquisitionComposition> ResolveAsync(
        ResourceScopeRef executionScope,
        ResourceNamespace ownerNamespace,
        ResolvedDataSourceProfile profile,
        CancellationToken cancellationToken)
    {
        if (executionScope is not { Kind: ResourceScopeKind.Workspace, TargetId: { } workspaceId })
            throw Error("data_source_acquisition_scope_invalid", "Data Source acquisitions execute in a Workspace scope.");
        var target = profile.Definition.AcquisitionFlow;
        var flowNamespace = target.Namespace ?? ownerNamespace;
        FlowVersion resolvedFlow;
        try
        {
            resolvedFlow = await flows.ResolveAsync(new WorkspaceId(workspaceId),
                new FlowReference(new FlowId(target.Name, flowNamespace), target.Version,
                    target.UseActiveVersion, flowNamespace), ownerNamespace, cancellationToken);
        }
        catch (Exception exception) when (exception is FlowNotFoundException or FlowValidationException or ArgumentException)
        {
            throw Error("data_source_acquisition_flow_unavailable",
                $"Flow binding '{flowNamespace}/{target.Name}' is unavailable: {exception.Message}");
        }

        var tools = new List<ResolvedDataSourceToolBinding>(profile.Definition.ToolBindings.Count);
        foreach (var binding in profile.Definition.ToolBindings)
        {
            var storedTool = await references.ResolveAsync<ToolResource>(binding.Tool, ownerNamespace,
                ToolResourceKinds.Tool, executionScope, cancellationToken)
                ?? throw Error("data_source_acquisition_tool_missing",
                    $"Tool binding '{binding.Name}' cannot resolve Tool '{binding.Tool.Name}'.");
            var tool = storedTool.Value;
            if (!tool.Definition.Enabled || tool.Definition.Discovery?.Available != true)
                throw Error("data_source_acquisition_tool_unavailable", $"Tool '{tool.Address}' is unavailable.");
            var providerReference = tool.Definition.Provider
                ?? throw Error("data_source_acquisition_provider_missing", $"Tool '{tool.Address}' has no provider.");
            var storedProvider = await references.ResolveAsync<ToolProviderResource>(providerReference, tool.Namespace,
                ToolResourceKinds.ToolProvider, executionScope, cancellationToken)
                ?? throw Error("data_source_acquisition_provider_missing",
                    $"Provider for Tool '{tool.Address}' was not found.");
            var provider = storedProvider.Value;
            if (!provider.Definition.Enabled)
                throw Error("data_source_acquisition_provider_unavailable", $"Provider '{provider.Address}' is disabled.");
            if (binding.RequiredProvider is { } requiredProvider)
            {
                var requiredNamespace = requiredProvider.Namespace ?? tool.Namespace;
                if (!string.Equals(requiredProvider.Name, provider.Name, StringComparison.Ordinal)
                    || requiredNamespace != provider.Namespace
                    || requiredProvider.ScopeRef is { } requiredScope && requiredScope != provider.ScopeRef)
                    throw Error("data_source_acquisition_provider_incompatible",
                        $"Tool '{tool.Address}' resolved provider '{provider.Address}', which does not satisfy binding '{binding.Name}'.");
            }
            tools.Add(new()
            {
                BindingName = binding.Name,
                Capability = binding.Capability,
                ToolScopeRef = tool.ScopeRef!.Value,
                ToolName = tool.Name,
                ToolNamespace = tool.Namespace,
                ToolUid = tool.Uid,
                ToolGeneration = tool.Generation,
                ProviderScopeRef = provider.ScopeRef!.Value,
                ProviderName = provider.Name,
                ProviderNamespace = provider.Namespace,
                ProviderUid = provider.Uid,
                ProviderGeneration = provider.Generation,
                ExternalToolId = tool.Definition.ExternalId
                    ?? throw Error("data_source_acquisition_tool_identity_missing",
                        $"Tool '{tool.Address}' has no external identity.")
            });
        }

        return new()
        {
            Profile = profile,
            Flow = new(
                resolvedFlow.FlowId.Value,
                resolvedFlow.FlowId.Namespace,
                resolvedFlow.Version,
                target.UseActiveVersion,
                resolvedFlow.Graph?.InputSchema?.Clone(),
                resolvedFlow.Graph?.OutputSchema?.Clone(),
                resolvedFlow.Metadata.GetValueOrDefault(FlowMetadataKeys.Contract)),
            Tools = tools,
            Limits = Clone(profile.Definition.Limits),
            Policies = Clone(profile.Definition.Policies),
            CompatibilityRequirements = Clone(profile.Definition.CompatibilityRequirements)
        };
    }

    private static IReadOnlyDictionary<string, System.Text.Json.JsonElement> Clone(
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> values) =>
        values.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal);
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
                Scope(request.WorkspaceId, request.TenantId, request.PrincipalId)), cancellationToken);
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
        if (!string.Equals(contract, DataSourceFlowContracts.Acquisition, StringComparison.Ordinal))
            throw new FlowValidationException("data_source_flow_contract_unknown",
                $"Data Source Flow contract '{contract}' is not supported.");
        Require(version.Graph?.InputSchema,
            ["dataSourceId", "dataSourceUid", "dataSourceGeneration", "profile", "sourceConfiguration",
                "parameters", "caller", "correlationId", "acquisitionId"], "input");
        Require(version.Graph?.OutputSchema, ["artifacts"], "output");
        return Task.CompletedTask;
    }

    private static void Require(System.Text.Json.JsonElement? schema, IReadOnlyList<string> names, string direction)
    {
        if (schema is not { ValueKind: System.Text.Json.JsonValueKind.Object }
            || !schema.Value.TryGetProperty("type", out var type) || type.GetString() != "object"
            || !schema.Value.TryGetProperty("properties", out var properties)
            || properties.ValueKind != System.Text.Json.JsonValueKind.Object
            || !schema.Value.TryGetProperty("required", out var required)
            || required.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw Invalid(direction, "must be an object schema with properties and required arrays");
        var requiredNames = required.EnumerateArray()
            .Where(item => item.ValueKind == System.Text.Json.JsonValueKind.String)
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var name in names)
            if (!properties.TryGetProperty(name, out _) || !requiredNames.Contains(name))
                throw Invalid(direction, $"must require property '{name}'");
    }

    private static FlowValidationException Invalid(string direction, string detail) =>
        new("data_source_acquisition_contract_invalid",
            $"The {DataSourceFlowContracts.Acquisition} {direction} schema {detail}.");
}
