using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Flows;

public sealed class KnowledgeProjectionInputResolver(
    IResourceReferenceResolver references,
    DataSourceManagementService dataSources,
    DataSourceAcquisitionService acquisitions,
    DataSourceAcquisitionTargetService targets,
    TimeProvider timeProvider) : IKnowledgeProjectionInputResolver
{
    public async Task<ResolvedKnowledgeDataSourceBinding> ResolveDependencyAsync(
        ResourceScopeRef ownerScope,
        ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding,
        CancellationToken cancellationToken)
    {
        var ns = binding.DataSource.Namespace ?? ownerNamespace;
        var source = await dataSources.ResolveForUseAsync(ns, binding.DataSource.Name,
            binding.DataSource.ScopeRef, ownerScope, cancellationToken);
        var sourceScope = source.Value.ScopeRef
            ?? throw Error("knowledge_projection_data_source_scope_missing",
                $"Data Source '{source.Value.Address}' has no ownership scope.");
        var readiness = await dataSources.GetReadinessAsync(source.Value.Namespace, source.Value.Name,
            sourceScope, cancellationToken);
        var profile = readiness.Profile
            ?? throw Error("knowledge_projection_data_source_profile_missing",
                $"Data Source '{source.Value.Address}' has no resolved profile.");
        if (!readiness.Ready)
            throw Error("knowledge_projection_data_source_not_ready",
                $"Data Source '{source.Value.Address}' is not ready: {string.Join(' ', readiness.Issues)}");
        return new()
        {
            BindingName = binding.Name,
            DataSourceScopeRef = sourceScope,
            DataSourceNamespace = source.Value.Namespace,
            DataSourceName = source.Value.Name,
            DataSourceUid = source.Value.Uid,
            DataSourceGeneration = source.Value.Generation,
            ProfileScopeRef = profile.ScopeRef,
            ProfileNamespace = profile.Namespace,
            ProfileName = profile.Name,
            ProfileUid = profile.Uid,
            ProfileGeneration = profile.Generation,
            ProfileVersion = profile.Version,
            ProfileDefinitionHash = profile.DefinitionHash,
            ProfileDependencyHash = profile.DependencyHash
        };
    }

    public async Task<KnowledgeDataSourceBindingReadiness> GetReadinessAsync(
        ResourceScopeRef executionScope,
        ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding,
        ResolvedKnowledgeDataSourceBinding dependency,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = await ResolveSourceAsync(executionScope, ownerNamespace, binding, dependency, cancellationToken);
            if (!source.Value.Definition.Enabled)
                return new(binding.Name, source.Value.ScopeRef, source.Value.Uid, false,
                    $"Data Source '{source.Value.Address}' is disabled.", null);
            var values = await acquisitions.ListAsync(source.Value.Namespace, source.Value.Name,
                source.Value.ScopeRef, cancellationToken);
            var target = await ResolveTargetAsync(executionScope, ownerNamespace, source.Value, binding, cancellationToken);
            var latest = values.FirstOrDefault(value => value.Target?.Uid == target?.Uid
                && value.State == DataSourceAcquisitionState.Succeeded
                && value.Manifest is not null && value.Manifest.Artifacts.Count != 0);
            if (latest is null)
                return new(binding.Name, source.Value.ScopeRef, source.Value.Uid, false,
                    $"Data Source '{source.Value.Address}' has no successful acquisition with artifacts.", null);
            if (IsStale(binding, latest.CompletedAt))
                return new(binding.Name, source.Value.ScopeRef, source.Value.Uid, false,
                    $"Data Source acquisition '{latest.Name}' is older than the maximum age configured for binding '{binding.Name}'.", null);
            return new(binding.Name, source.Value.ScopeRef, source.Value.Uid, true, null, null);
        }
        catch (Exception exception) when (exception is ResourceReferenceOutsideScopeException
            or ResourceReferenceAmbiguousException or KnowledgeProjectionException)
        {
            return new(binding.Name, null, null, false, exception.Message, null);
        }
    }

    public async Task<KnowledgeProjectionInputEvidence> ResolveAsync(
        ResourceScopeRef executionScope,
        ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding,
        ResolvedKnowledgeDataSourceBinding dependency,
        string? acquisitionId,
        CancellationToken cancellationToken)
    {
        var source = await ResolveSourceAsync(executionScope, ownerNamespace, binding, dependency, cancellationToken);
        if (!source.Value.Definition.Enabled)
            throw Error("knowledge_projection_data_source_disabled", $"Data Source '{source.Value.Address}' is disabled.");
        var available = await acquisitions.ListAsync(source.Value.Namespace, source.Value.Name,
            source.Value.ScopeRef, cancellationToken);
        var target = await ResolveTargetAsync(executionScope, ownerNamespace, source.Value, binding, cancellationToken);
        available = available.Where(value => value.Target?.Uid == target?.Uid).ToArray();
        var acquisition = string.IsNullOrWhiteSpace(acquisitionId)
            ? available.FirstOrDefault(value => value.State == DataSourceAcquisitionState.Succeeded
                && value.Manifest is not null && value.Manifest.Artifacts.Count != 0)
            : available.SingleOrDefault(value => string.Equals(value.Name, acquisitionId, StringComparison.Ordinal));
        if (acquisition is null)
            throw Error("knowledge_projection_acquisition_missing",
                $"Binding '{binding.Name}' has no matching Data Source acquisition.");
        if (acquisition.State != DataSourceAcquisitionState.Succeeded || acquisition.Manifest is null
            || acquisition.CompletedAt is null)
            throw Error("knowledge_projection_acquisition_not_ready",
                $"Data Source acquisition '{acquisition.Name}' for binding '{binding.Name}' is not successful.");
        if (IsStale(binding, acquisition.CompletedAt))
            throw Error("knowledge_projection_acquisition_stale",
                $"Data Source acquisition '{acquisition.Name}' is older than the maximum age configured for binding '{binding.Name}'.");
        var selected = acquisition.Manifest.Artifacts
            .Where(value => value.Disposition == DataSourceArtifactDisposition.Publishable)
            .Select(value => new KnowledgeProjectionArtifact
            {
                ArtifactId = value.ArtifactId,
                Kind = value.Kind == DataSourceArtifactKind.Durable
                    ? KnowledgeArtifactKind.Durable : KnowledgeArtifactKind.Staged,
                Disposition = KnowledgeArtifactDisposition.Publishable,
                Name = value.Name,
                MediaType = value.MediaType,
                Digest = value.Digest
            }).ToArray();
        if (selected.Length == 0)
            throw Error("knowledge_projection_acquisition_empty",
                $"Data Source acquisition '{acquisition.Name}' exposes no publishable artifacts.");
        return new()
        {
            BindingName = binding.Name,
            DataSourceScopeRef = source.Value.ScopeRef!.Value,
            DataSourceUid = source.Value.Uid,
            DataSourceName = source.Value.Name,
            DataSourceNamespace = source.Value.Namespace,
            DataSourceGeneration = source.Value.Generation,
            AcquisitionTargetName = target?.Name,
            AcquisitionTargetUid = target?.Uid,
            DiscoveryId = target?.DiscoveryId,
            ChoiceId = target?.ChoiceId,
            AcquisitionId = acquisition.Name,
            AcquisitionUid = acquisition.Uid,
            AcquisitionFlowRunId = acquisition.FlowRunId,
            AcquiredAt = acquisition.CompletedAt.Value,
            AcquisitionComposition = System.Text.Json.JsonSerializer.SerializeToElement(acquisition.Composition),
            AcquiredArtifacts = selected,
            PreparedArtifacts = selected
        };
    }

    private bool IsStale(KnowledgeDataSourceBinding binding, DateTimeOffset? completedAt) =>
        binding.MaximumAge is { } maximumAge && completedAt is { } completed
        && completed < timeProvider.GetUtcNow() - maximumAge;

    private async Task<StoredResource<DataSourceResource>> ResolveSourceAsync(
        ResourceScopeRef executionScope,
        ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding,
        ResolvedKnowledgeDataSourceBinding dependency,
        CancellationToken cancellationToken)
    {
        var source = await dataSources.ResolveForUseAsync(dependency.DataSourceNamespace,
            dependency.DataSourceName, dependency.DataSourceScopeRef, executionScope, cancellationToken);
        if (source.Value.Uid != dependency.DataSourceUid
            || source.Value.Generation != dependency.DataSourceGeneration)
            throw Error("knowledge_projection_data_source_stale",
                $"Pinned Data Source dependency for binding '{binding.Name}' is stale.");
        var readiness = await dataSources.GetReadinessAsync(source.Value.Namespace, source.Value.Name,
            dependency.DataSourceScopeRef, cancellationToken);
        var profile = readiness.Profile;
        if (profile is null
            || profile.ScopeRef != dependency.ProfileScopeRef
            || profile.Namespace != dependency.ProfileNamespace
            || !string.Equals(profile.Name, dependency.ProfileName, StringComparison.Ordinal)
            || profile.Uid != dependency.ProfileUid
            || profile.Generation != dependency.ProfileGeneration
            || !string.Equals(profile.Version, dependency.ProfileVersion, StringComparison.Ordinal)
            || !string.Equals(profile.DefinitionHash, dependency.ProfileDefinitionHash, StringComparison.Ordinal)
            || !string.Equals(profile.DependencyHash, dependency.ProfileDependencyHash, StringComparison.Ordinal))
            throw Error("knowledge_projection_data_source_profile_stale",
                $"Pinned Data Source Profile dependency for binding '{binding.Name}' is stale.");
        return source;
    }

    private async Task<DataSourceAcquisitionTargetResource?> ResolveTargetAsync(
        ResourceScopeRef executionScope, ResourceNamespace ownerNamespace, DataSourceResource source,
        KnowledgeDataSourceBinding binding, CancellationToken cancellationToken)
    {
        if (binding.AcquisitionTarget is null) return null;
        var stored = await references.ResolveAsync<DataSourceAcquisitionTargetResource>(binding.AcquisitionTarget,
            ownerNamespace, DataSourceResourceKinds.DataSourceAcquisitionTarget, executionScope, cancellationToken)
            ?? throw Error("knowledge_projection_acquisition_target_missing",
                $"Binding '{binding.Name}' cannot resolve acquisition target '{binding.AcquisitionTarget.Name}'.");
        if (stored.Value.DataSourceUid != source.Uid || stored.Value.DataSourceScopeRef != source.ScopeRef)
            throw Error("knowledge_projection_acquisition_target_mismatch",
                $"Acquisition target '{stored.Value.Name}' belongs to another Data Source.");
        if (!stored.Value.Enabled)
            throw Error("knowledge_projection_acquisition_target_disabled", $"Acquisition target '{stored.Value.Name}' is disabled.");
        var views = await targets.ListAsync(source.Namespace, source.Name, source.ScopeRef, cancellationToken);
        var view = views.Single(value => value.Target.Uid == stored.Value.Uid);
        if (view.Stale)
            throw Error("knowledge_projection_acquisition_target_stale", string.Join(' ', view.StalenessReasons));
        return stored.Value;
    }

    private static KnowledgeProjectionException Error(string code, string message) => new(code, message);
}

public sealed class KnowledgeProjectionFlowGateway(FlowRunService runs) : IKnowledgeProjectionFlowGateway
{
    public async Task<KnowledgeProjectionFlowRunResult> ExecuteAsync(
        KnowledgeProjectionFlowRunRequest request,
        CancellationToken cancellationToken)
    {
        var scope = new FlowRunScope(request.TenantId, new WorkspaceId(request.WorkspaceId), request.PrincipalId);
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
                request.RunId,
                request.CorrelationId,
                request.Input,
                $"knowledge-projection:{request.RunId}",
                request.Flow.UsesActiveVersion,
                null,
                null,
                null,
                null,
                scope)
            {
                DefinitionScopeRef = request.Flow.ScopeRef
            }, cancellationToken);
            var completed = await AwaitCompletionAsync(stored.Value.Id, scope, cancellationToken);
            return Snapshot(completed.Value);
        }
        catch (KnowledgeProjectionException) { throw; }
        catch (Exception exception) when (exception is FlowValidationException or FlowNotFoundException)
        {
            throw new KnowledgeProjectionException("knowledge_projection_flow_rejected", exception.Message, exception);
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
                ?? throw new KnowledgeProjectionException("knowledge_projection_flow_run_missing",
                    $"Flow Run '{runId}' could not be reloaded.");
            if (stored.Value.Status.IsTerminal() || stored.Value.Status == FlowRunStatus.WaitingForInput)
                return stored;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static KnowledgeProjectionFlowRunResult Snapshot(FlowRun run) => new(
        run.Id,
        run.Status switch
        {
            FlowRunStatus.Pending => KnowledgeProjectionState.Pending,
            FlowRunStatus.Running => KnowledgeProjectionState.Running,
            FlowRunStatus.WaitingForInput => KnowledgeProjectionState.WaitingForInput,
            FlowRunStatus.WaitingForChild => KnowledgeProjectionState.WaitingForChild,
            FlowRunStatus.Succeeded => KnowledgeProjectionState.Succeeded,
            FlowRunStatus.Cancelled => KnowledgeProjectionState.Cancelled,
            FlowRunStatus.TimedOut => KnowledgeProjectionState.TimedOut,
            _ => KnowledgeProjectionState.Failed
        },
        run.Output?.Clone(),
        run.Error?.Code,
        run.Error?.Message,
        run.CompletedAt);
}
