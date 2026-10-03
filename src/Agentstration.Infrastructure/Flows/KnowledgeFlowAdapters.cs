using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Artifacts.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Flows;

public sealed class KnowledgeFlowResolver(FlowService flows) : IKnowledgeFlowResolver
{
    public async Task<ResolvedKnowledgeFlowBinding> ResolveAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace ownerNamespace,
        KnowledgeFlowTarget target,
        CancellationToken cancellationToken)
    {
        if (scopeRef.Kind != ResourceScopeKind.Workspace || scopeRef.TargetId is not { } workspaceId)
            throw new KnowledgeSourceValidationException("knowledge_source_scope_invalid", "A KnowledgeSource must belong to a Workspace scope.");
        var flowNamespace = target.Namespace ?? ownerNamespace;
        var reference = new FlowReference(new FlowId(target.Name, flowNamespace), target.Version, target.UseActiveVersion, flowNamespace);
        try
        {
            var resolved = await flows.ResolveAsync(new WorkspaceId(workspaceId), reference, ownerNamespace, cancellationToken);
            return new(
                resolved.FlowId.Value,
                resolved.FlowId.Namespace,
                resolved.Version,
                target.UseActiveVersion,
                resolved.Graph?.InputSchema?.Clone(),
                resolved.Graph?.OutputSchema?.Clone(),
                resolved.Metadata.GetValueOrDefault(KnowledgeFlowContracts.MetadataKey),
                Capabilities(resolved.Metadata.GetValueOrDefault(KnowledgeFlowContracts.CapabilitiesMetadataKey)));
        }
        catch (Exception exception) when (exception is FlowNotFoundException or FlowValidationException or ArgumentException)
        {
            throw new KnowledgeSourceValidationException("knowledge_source_flow_unavailable",
                $"Flow binding '{flowNamespace}/{target.Name}' is not available as a published version: {exception.Message}");
        }
    }

    private static IReadOnlyList<string>? Capabilities(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class KnowledgeAcquisitionFlowGateway(FlowRunService runs) : IKnowledgeAcquisitionFlowGateway
{
    public async Task<KnowledgeFlowRunSnapshot> StartAsync(
        KnowledgeFlowRunRequest request,
        CancellationToken cancellationToken)
    {
        var flowId = new FlowId(request.Flow.Name, request.Flow.Namespace);
        var scope = Scope(request.WorkspaceId, request.TenantId, request.PrincipalId);
        try
        {
            var stored = await runs.EnsureRootAsync(new EnsureRootFlowRunCommand(
                request.RunId,
                flowId,
                request.Flow.Version,
                "local",
                FlowRunTrigger.Api,
                FlowInvocationOrigin.Api,
                request.CallerId,
                null,
                request.IdempotencyKey,
                request.CorrelationId,
                request.Input,
                $"knowledge-acquisition:{request.RunId}",
                request.Flow.UsesActiveVersion,
                null,
                null,
                null,
                null,
                scope), cancellationToken);
            return Snapshot(stored.Value);
        }
        catch (Exception exception) when (exception is FlowValidationException or FlowNotFoundException)
        {
            throw new KnowledgeAcquisitionException("knowledge_acquisition_flow_rejected", exception.Message);
        }
    }

    public async Task<KnowledgeFlowRunSnapshot?> GetAsync(
        Guid workspaceId,
        string runId,
        Guid tenantId,
        Guid principalId,
        CancellationToken cancellationToken)
    {
        var stored = await runs.GetAsync(runId, Scope(workspaceId, tenantId, principalId), cancellationToken);
        return stored is null ? null : Snapshot(stored.Value);
    }

    public async Task<KnowledgeFlowRunSnapshot> CancelAsync(
        Guid workspaceId,
        string runId,
        Guid tenantId,
        Guid principalId,
        CancellationToken cancellationToken) => Snapshot((await runs.CancelAsync(
            runId, Scope(workspaceId, tenantId, principalId), cancellationToken)).Value);

    private static FlowRunScope Scope(Guid workspaceId, Guid tenantId, Guid principalId) =>
        new(tenantId, new WorkspaceId(workspaceId), principalId);

    private static KnowledgeFlowRunSnapshot Snapshot(FlowRun run) => new(
        run.Id,
        run.Status switch
        {
            FlowRunStatus.Pending => KnowledgeAcquisitionState.Pending,
            FlowRunStatus.Running => KnowledgeAcquisitionState.Running,
            FlowRunStatus.WaitingForInput => KnowledgeAcquisitionState.WaitingForInput,
            FlowRunStatus.WaitingForChild => KnowledgeAcquisitionState.WaitingForChild,
            FlowRunStatus.Succeeded => KnowledgeAcquisitionState.Succeeded,
            FlowRunStatus.Cancelled => KnowledgeAcquisitionState.Cancelled,
            FlowRunStatus.TimedOut => KnowledgeAcquisitionState.TimedOut,
            _ => KnowledgeAcquisitionState.Failed
        },
        run.Output?.Clone(),
        run.Error?.Code,
        run.Error?.Message,
        run.CompletedAt);
}

public sealed class KnowledgeRetrievalFlowGateway(FlowRunService runs) : IKnowledgeRetrievalFlowGateway
{
    public async Task<KnowledgeRetrievalFlowResult> ExecuteAsync(
        KnowledgeRetrievalFlowRequest request,
        CancellationToken cancellationToken)
    {
        var scope = new FlowRunScope(request.TenantId, new WorkspaceId(request.WorkspaceId), request.PrincipalId);
        var origin = request.Origin switch
        {
            KnowledgeRetrievalInvocationOrigin.Tool => FlowInvocationOrigin.Mcp,
            KnowledgeRetrievalInvocationOrigin.Mcp => FlowInvocationOrigin.Mcp,
            KnowledgeRetrievalInvocationOrigin.Flow => FlowInvocationOrigin.Mcp,
            KnowledgeRetrievalInvocationOrigin.Agent => FlowInvocationOrigin.Agent,
            _ => FlowInvocationOrigin.Api
        };
        try
        {
            var stored = await runs.EnsureRootAsync(new EnsureRootFlowRunCommand(
                request.RunId,
                new FlowId(request.Flow.Name, request.Flow.Namespace),
                request.Flow.Version,
                "local",
                request.Origin == KnowledgeRetrievalInvocationOrigin.Flow ? FlowRunTrigger.Flow : FlowRunTrigger.Api,
                origin,
                request.CallerId,
                request.ExecutionContext?.ToolInvocationId ?? request.ExecutionContext?.ToolCallId,
                request.RunId,
                request.CorrelationId,
                request.Input,
                $"knowledge-retrieval:{request.RunId}",
                request.Flow.UsesActiveVersion,
                request.ExecutionContext?.FlowRunId,
                null,
                null,
                null,
                scope), cancellationToken);
            if (!stored.Value.Status.IsTerminal())
                await runs.ExecuteAsync(new FlowRunQueueItem(stored.Value.Id, scope), cancellationToken);
            var completed = await AwaitCompletionAsync(stored.Value.Id, scope, cancellationToken);
            if (completed.Value.Status != FlowRunStatus.Succeeded)
                throw new KnowledgeRetrievalException(
                    completed.Value.Status == FlowRunStatus.WaitingForInput
                        ? "knowledge_retrieval_input_required"
                        : "knowledge_retrieval_execution_failed",
                    completed.Value.Error?.Message
                    ?? $"Retrieval Flow Run ended with status '{completed.Value.Status}'.");
            if (completed.Value.Output is null)
                throw new KnowledgeRetrievalException("knowledge_retrieval_output_missing",
                    "The retrieval Flow returned no output.");
            return new(completed.Value.Id, completed.Value.Output.Value.Clone());
        }
        catch (KnowledgeRetrievalException) { throw; }
        catch (Exception exception) when (exception is FlowValidationException or FlowNotFoundException)
        {
            throw new KnowledgeRetrievalException("knowledge_retrieval_flow_rejected", exception.Message, exception);
        }
    }

    private async Task<StoredFlowRun> AwaitCompletionAsync(
        string runId,
        FlowRunScope scope,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stored = await runs.GetAsync(runId, scope, cancellationToken)
                ?? throw new KnowledgeRetrievalException("knowledge_retrieval_flow_run_missing",
                    "The retrieval Flow Run could not be reloaded.");
            if (stored.Value.Status.IsTerminal() || stored.Value.Status == FlowRunStatus.WaitingForInput)
                return stored;
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

public sealed class KnowledgeFlowActivationGuard : IFlowVersionActivationGuard
{
    public Task ValidateActivationAsync(WorkspaceId workspaceId, FlowVersion version, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        cancellationToken.ThrowIfCancellationRequested();
        if (!version.Metadata.TryGetValue(KnowledgeFlowContracts.MetadataKey, out var contract))
            return Task.CompletedTask;
        if (string.Equals(contract, KnowledgeFlowContracts.Retrieval, StringComparison.Ordinal))
        {
            ValidateRetrieval(version);
            return Task.CompletedTask;
        }
        if (!string.Equals(contract, KnowledgeFlowContracts.Ingestion, StringComparison.Ordinal))
            throw new FlowValidationException("knowledge_flow_contract_unknown",
                $"Knowledge Flow contract '{contract}' is not supported.");
        RequireObjectSchema(version.Graph?.InputSchema, "input");
        RequireProperties(version.Graph!.InputSchema!.Value,
            ["knowledgeSourceId", "knowledgeSourceUid", "knowledgeSourceGeneration", "sourceConfiguration",
                "parameters", "caller", "correlationId", "acquisitionId"], "input");
        RequireObjectSchema(version.Graph.OutputSchema, "output");
        RequireProperties(version.Graph.OutputSchema!.Value, ["artifacts"], "output");
        return Task.CompletedTask;
    }

    private static void ValidateRetrieval(FlowVersion version)
    {
        if (!version.Metadata.TryGetValue(KnowledgeFlowContracts.CapabilitiesMetadataKey, out var declared))
            throw new FlowValidationException("knowledge_retrieval_capabilities_required",
                "A retrieval Flow must declare at least one Knowledge capability.");
        var capabilities = declared.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var supported = new[] { KnowledgeFlowContracts.Search, KnowledgeFlowContracts.Query, KnowledgeFlowContracts.Read };
        if (capabilities.Length == 0
            || capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Length
            || capabilities.Any(value => !supported.Contains(value, StringComparer.Ordinal)))
            throw new FlowValidationException("knowledge_retrieval_capabilities_invalid",
                "Retrieval capabilities must be distinct supported Knowledge capability identifiers.");
        RequireObjectSchema(version.Graph?.InputSchema, "input");
        RequireProperties(version.Graph!.InputSchema!.Value,
            ["knowledgeSourceId", "knowledgeSourceUid", "knowledgeSourceGeneration", "operation",
                "snapshot", "request", "caller", "correlationId", "retrievalId"], "input");
        RequireObjectSchema(version.Graph.OutputSchema, "output");
        RequireProperties(version.Graph.OutputSchema!.Value, ["items", "citations"], "output");
    }

    private static void RequireObjectSchema(JsonElement? schema, string direction)
    {
        if (schema is not { ValueKind: JsonValueKind.Object }
            || !schema.Value.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString() != "object")
            throw Invalid(direction, "must be an object schema");
    }

    private static void RequireProperties(JsonElement schema, IReadOnlyList<string> names, string direction)
    {
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
            throw Invalid(direction, "must declare properties and required arrays");
        var requiredNames = required.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var name in names)
            if (!properties.TryGetProperty(name, out _) || !requiredNames.Contains(name))
                throw Invalid(direction, $"must require property '{name}'");
    }

    private static FlowValidationException Invalid(string direction, string detail) =>
        new("knowledge_ingestion_contract_invalid", $"The {KnowledgeFlowContracts.Ingestion} {direction} schema {detail}.");
}

public sealed class KnowledgeArtifactReferenceValidator(IResourceStore store)
    : IKnowledgeArtifactReferenceValidator
{
    public async Task ValidateAsync(
        Guid workspaceId,
        IReadOnlyList<KnowledgeAcquisitionArtifact> references,
        CancellationToken cancellationToken)
    {
        foreach (var reference in references)
        {
            try
            {
                var exists = reference.Kind switch
                {
                    KnowledgeArtifactKind.Staged => await store.GetExactAsync<StagedArtifactResource>(ScopedResourceAddress.Create(
                        ResourceScopeRef.Workspace(workspaceId), ResourceNamespace.Default, ArtifactResourceKinds.StagedArtifact,
                        StagedArtifactId.Parse(reference.ArtifactId).ToString()), cancellationToken) is not null,
                    KnowledgeArtifactKind.Durable => await store.GetExactAsync<FlowRunArtifactResource>(ScopedResourceAddress.Create(
                        ResourceScopeRef.Workspace(workspaceId), ResourceNamespace.Default, ArtifactResourceKinds.FlowRunArtifact,
                        FlowRunArtifactId.Parse(reference.ArtifactId).ToString()), cancellationToken) is not null,
                    _ => false
                };
                if (!exists)
                    throw new KnowledgeAcquisitionException("knowledge_ingestion_artifact_not_found",
                        $"Artifact '{reference.ArtifactId}' was not found in Workspace '{workspaceId:D}'.");
            }
            catch (FormatException exception)
            {
                throw new KnowledgeAcquisitionException("knowledge_ingestion_artifact_invalid",
                    $"Artifact identity '{reference.ArtifactId}' is invalid: {exception.Message}");
            }
        }
    }
}

public sealed class KnowledgeSnapshotArtifactResolver(IResourceStore store)
    : IKnowledgeSnapshotArtifactResolver
{
    public async Task<IReadOnlyList<KnowledgeDurableArtifactEvidence>> ResolveAsync(
        Guid workspaceId,
        IReadOnlyList<string> artifactIds,
        CancellationToken cancellationToken)
    {
        var result = new List<KnowledgeDurableArtifactEvidence>(artifactIds.Count);
        foreach (var artifactId in artifactIds)
        {
            FlowRunArtifactId parsed;
            try { parsed = FlowRunArtifactId.Parse(artifactId); }
            catch (FormatException exception)
            {
                throw new KnowledgeSnapshotException("knowledge_snapshot_artifact_invalid",
                    $"FlowRunArtifact identity '{artifactId}' is invalid.", exception);
            }
            var artifact = await store.GetExactAsync<FlowRunArtifactResource>(ScopedResourceAddress.Create(
                ResourceScopeRef.Workspace(workspaceId), ResourceNamespace.Default,
                ArtifactResourceKinds.FlowRunArtifact, parsed.ToString()), cancellationToken);
            if (artifact is null || artifact.Value.WorkspaceId.Value != workspaceId)
                throw new KnowledgeSnapshotException("knowledge_snapshot_artifact_not_found",
                    $"FlowRunArtifact '{artifactId}' was not found in Workspace '{workspaceId:D}'.");
            result.Add(new(
                artifact.Value.ArtifactId.ToString(),
                artifact.Value.ProducerFlowRunId,
                artifact.Value.ProducerFlowStepId,
                artifact.Value.Receipt.StorageFlowRunId,
                artifact.Value.Receipt.MediaType,
                artifact.Value.Receipt.Length,
                artifact.Value.Receipt.Sha256,
                new Dictionary<string, string>(artifact.Value.Receipt.Provenance, StringComparer.Ordinal)));
        }
        return result;
    }
}

public sealed class KnowledgeFlowDeletionGuard(
    IResourceStore store,
    IRequestContextScopeFactory requestScopes) : IFlowDeletionGuard
{
    public async Task ValidateDeleteAsync(WorkspaceId workspaceId, FlowId flowId, CancellationToken cancellationToken)
    {
        using var requestScope = requestScopes.PushSystem();
        var sources = await store.ListAllAsync<KnowledgeSourceResource>(KnowledgeResourceKinds.KnowledgeSource, cancellationToken);
        var usage = sources.Select(value => value.Value).FirstOrDefault(value =>
            value.ScopeRef is { Kind: ResourceScopeKind.Workspace, TargetId: { } targetId }
            && targetId == workspaceId.Value
            && (References(value, value.Definition.IngestionFlow, flowId)
                || References(value, value.Definition.RetrievalFlow, flowId)));
        if (usage is not null)
            throw new FlowValidationException("flow_in_use_by_knowledge_source",
                $"Flow '{flowId}' is referenced by KnowledgeSource '{usage.Address}'.");
        var snapshots = await store.ListAllAsync<KnowledgeSnapshotResource>(KnowledgeResourceKinds.KnowledgeSnapshot, cancellationToken);
        var retained = snapshots.Select(value => value.Value).FirstOrDefault(value =>
            value.ScopeRef is { Kind: ResourceScopeKind.Workspace, TargetId: { } targetId }
            && targetId == workspaceId.Value
            && string.Equals(value.IngestionFlow.Name, flowId.Value, StringComparison.Ordinal)
            && value.IngestionFlow.Namespace == flowId.Namespace);
        if (retained is not null)
            throw new FlowValidationException("flow_in_use_by_knowledge_snapshot",
                $"Flow '{flowId}' is retained by Knowledge Snapshot '{retained.Address}'.");
    }

    private static bool References(KnowledgeSourceResource source, KnowledgeFlowTarget? target, FlowId flowId) =>
        target is not null
        && string.Equals(target.Name, flowId.Value, StringComparison.Ordinal)
        && (target.Namespace ?? source.Namespace) == flowId.Namespace;
}

public sealed class KnowledgeSnapshotFlowRunDeletionGuard(
    IResourceStore store,
    IRequestContextScopeFactory requestScopes) : IFlowRunDeletionGuard
{
    public async Task ValidateDeleteAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken)
    {
        using var requestScope = requestScopes.PushSystem();
        var snapshots = await store.ListAllAsync<KnowledgeSnapshotResource>(
            KnowledgeResourceKinds.KnowledgeSnapshot, cancellationToken);
        var retained = snapshots.Select(value => value.Value).FirstOrDefault(value =>
            value.ScopeRef is { Kind: ResourceScopeKind.Workspace, TargetId: { } targetId }
            && targetId == workspaceId.Value
            && (string.Equals(value.IngestionFlowRunId, runId, StringComparison.Ordinal)
                || value.Artifacts.Any(artifact => string.Equals(
                    artifact.StorageFlowRunId, runId, StringComparison.Ordinal))));
        if (retained is not null)
            throw new FlowValidationException("flow_run_in_use_by_knowledge_snapshot",
                $"FlowRun '{runId}' is retained by Knowledge Snapshot '{retained.Address}'.");
    }
}
