using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using System.Text.Json;
using System.Security.Cryptography;
using Agentstration.Storage;

namespace Agentstration.Infrastructure.Flows;

public sealed class KnowledgeProjectionInputResolver(
    IResourceReferenceResolver references,
    DataSourceManagementService dataSources,
    DataSourceAcquisitionService acquisitions,
    DataSourceAcquisitionTargetService targets,
    DataSourceDatasetAcquisitionService datasets,
    ICurrentRequestContext current,
    IRequestContextScopeFactory requestScopes,
    TimeProvider timeProvider) : IKnowledgeProjectionInputResolver
{
    public async Task<ResolvedKnowledgeDataSourceBinding> ResolveDependencyAsync(
        ResourceScopeRef ownerScope,
        ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding,
        CancellationToken cancellationToken)
    {
        var ns = binding.DataSource.Namespace ?? ownerNamespace;
        // ResolveForUseAsync still checks the tenant consumer's grant on the exact ancestor.
        using var inheritedScope = current.AccessMode == ControlPlaneAccessMode.TenantAdministration
            && binding.DataSource.ScopeRef == ResourceScopeRef.Instance
            && ownerScope == ResourceScopeRef.Tenant(current.Current.TenantId)
            ? requestScopes.PushInstance(current.Current) : null;
        StoredResource<DataSourceResource> source;
        try
        {
            source = await dataSources.ResolveForUseAsync(ns, binding.DataSource.Name,
                binding.DataSource.ScopeRef, ownerScope, cancellationToken);
        }
        catch (DataSourceValidationException exception)
        {
            throw new KnowledgeSourceValidationException(exception.Code, exception.Message);
        }
        var sourceScope = source.Value.ScopeRef
            ?? throw Error("knowledge_projection_data_source_scope_missing",
                $"Data Source '{source.Value.Address}' has no ownership scope.");
        if (source.Value.Definition.Dataset is not null && binding.AcquisitionTarget is not null)
            throw new KnowledgeSourceValidationException("knowledge_projection_target_incompatible", "Native datasets do not use historical acquisition targets.");
        ValidateFallback(binding, source.Value);
        if (source.Value.Definition.Dataset is not null)
            return new()
            {
                BindingName = binding.Name,
                DataSourceScopeRef = sourceScope,
                DataSourceNamespace = source.Value.Namespace,
                DataSourceName = source.Value.Name,
                DataSourceUid = source.Value.Uid,
                DataSourceGeneration = source.Value.Generation,
                DatasetDependencyHash = DatasetHash(source.Value)
            };
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
            var latest = Compatible(values, source.Value, dependency, target?.Uid).FirstOrDefault();
            if (binding.RefreshIfNecessary || source.Value.Definition.Dataset?.Lifecycle is not null)
                return new(binding.Name, source.Value.ScopeRef, source.Value.Uid, true, null, null);
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

    public async Task RevalidateAsync(ResourceScopeRef executionScope, ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding, ResolvedKnowledgeDataSourceBinding dependency,
        KnowledgeProjectionInputEvidence evidence, CancellationToken cancellationToken)
    {
        try { await RevalidateCoreAsync(executionScope, ownerNamespace, binding, dependency, evidence, cancellationToken); }
        catch (DataSourceAcquisitionException exception) { throw Error(exception.Code, exception.Message); }
        catch (DataSourceValidationException exception) { throw Error(exception.Code, exception.Message); }
        catch (StorageException exception) { throw Error(exception.Code, exception.Message); }
    }

    private async Task RevalidateCoreAsync(ResourceScopeRef executionScope, ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding, ResolvedKnowledgeDataSourceBinding dependency,
        KnowledgeProjectionInputEvidence evidence, CancellationToken cancellationToken)
    {
        var source = await ResolveSourceAsync(executionScope, ownerNamespace, binding, dependency, cancellationToken);
        var acquisition = (await acquisitions.GetAsync(source.Value.Namespace, evidence.AcquisitionId, cancellationToken)).Value;
        if (acquisition.Uid != evidence.AcquisitionUid || acquisition.DataSourceUid != source.Value.Uid
            || acquisition.State != DataSourceAcquisitionState.Succeeded)
            throw Error("knowledge_projection_input_changed", "The accepted acquisition evidence no longer matches.");
        if (acquisition.DirectResult is { } direct) _ = await datasets.ReadDirectAsync(acquisition, direct.RequestId, cancellationToken);
        else if (acquisition.Dataset is not null) _ = await datasets.ReadAsync(acquisition, 0, 1, cancellationToken);
    }

    public async Task<KnowledgeProjectionInputEvidence> ResolveAsync(
        ResourceScopeRef executionScope,
        ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding,
        ResolvedKnowledgeDataSourceBinding dependency,
        string? acquisitionId,
        string requestId,
        CancellationToken cancellationToken)
    {
        try { return await ResolveCoreAsync(executionScope, ownerNamespace, binding, dependency, acquisitionId, requestId, cancellationToken); }
        catch (DataSourceAcquisitionException exception) { throw Error(exception.Code, exception.Message); }
        catch (DataSourceValidationException exception) { throw Error(exception.Code, exception.Message); }
        catch (StorageException exception) { throw Error(exception.Code, exception.Message); }
    }

    private async Task<KnowledgeProjectionInputEvidence> ResolveCoreAsync(
        ResourceScopeRef executionScope,
        ResourceNamespace ownerNamespace,
        KnowledgeDataSourceBinding binding,
        ResolvedKnowledgeDataSourceBinding dependency,
        string? acquisitionId,
        string requestId,
        CancellationToken cancellationToken)
    {
        var source = await ResolveSourceAsync(executionScope, ownerNamespace, binding, dependency, cancellationToken);
        if (!source.Value.Definition.Enabled)
            throw Error("knowledge_projection_data_source_disabled", $"Data Source '{source.Value.Address}' is disabled.");
        var available = await acquisitions.ListAsync(source.Value.Namespace, source.Value.Name,
            source.Value.ScopeRef, cancellationToken);
        var target = await ResolveTargetAsync(executionScope, ownerNamespace, source.Value, binding, cancellationToken);
        var compatible = Compatible(available, source.Value, dependency, target?.Uid).ToArray();
        var acquisition = string.IsNullOrWhiteSpace(acquisitionId)
            ? compatible.FirstOrDefault(value => value.DirectResult is null || value.DirectResult.RequestId == requestId)
            : compatible.SingleOrDefault(value => string.Equals(value.Name, acquisitionId, StringComparison.Ordinal));
        var policy = source.Value.Definition.Dataset?.Lifecycle;
        var directOnly = source.Value.Definition.Dataset is { Storage: null };
        KnowledgeInputRefreshFailure? refreshFailure = null;
        var needsRefresh = string.IsNullOrWhiteSpace(acquisitionId) && (directOnly || policy is not null
            ? directOnly || acquisition?.CompletedAt is null || policy?.TimeToLive is { } ttl && timeProvider.GetUtcNow() >= acquisition.CompletedAt + ttl
            : KnowledgeFreshness.RequiresRefresh(binding, acquisition?.CompletedAt, timeProvider));
        if (!needsRefresh && acquisition is { Dataset: not null, DirectResult: null } && policy is not null)
            try { _ = await datasets.ReadAsync(acquisition, 0, 1, cancellationToken); }
            catch (Exception exception) when (exception is DataSourceAcquisitionException or StorageException)
            { needsRefresh = string.IsNullOrWhiteSpace(acquisitionId); }
        if (needsRefresh)
        {
            var fallback = acquisition;
            string? refreshId = null;
            try
            {
                StoredResource<DataSourceAcquisitionResource> started;
                try
                {
                    started = await acquisitions.StartAsync(source.Value.Namespace, source.Value.Name, source.Value.ScopeRef,
                        JsonSerializer.SerializeToElement(new { }), requestId, requestId, binding.AcquisitionTarget?.Name, cancellationToken);
                }
                catch (DataSourceAcquisitionException exception) when (!directOnly && exception.Code == "data_source_acquisition_already_running")
                {
                    var running = (await acquisitions.ListAsync(source.Value.Namespace, source.Value.Name, source.Value.ScopeRef, cancellationToken))
                        .FirstOrDefault(value => value.DataSourceUid == source.Value.Uid && value.DataSourceGeneration == source.Value.Generation
                            && value.State is DataSourceAcquisitionState.Pending or DataSourceAcquisitionState.Running)
                        ?? throw Error("knowledge_refresh_unavailable", "The concurrent refresh is unavailable.");
                    started = await acquisitions.GetAsync(running.Namespace, running.Name, cancellationToken);
                }
                refreshId = started.Value.Name;
                var deadline = timeProvider.GetUtcNow().AddMinutes(2);
                do
                {
                    acquisition = (await acquisitions.GetAsync(started.Value.Namespace, started.Value.Name, cancellationToken)).Value;
                    if (acquisition.State is not (DataSourceAcquisitionState.Pending or DataSourceAcquisitionState.Running)) break;
                    if (timeProvider.GetUtcNow() >= deadline) throw Error("knowledge_refresh_timeout", "The acquisition refresh timed out.");
                    await Task.Delay(TimeSpan.FromMilliseconds(50), timeProvider, cancellationToken);
                } while (true);
                if (acquisition.State != DataSourceAcquisitionState.Succeeded)
                    throw Error(acquisition.ErrorCode ?? "knowledge_refresh_failed", "The required acquisition refresh failed.");
                if (acquisition.Dataset is not null && acquisition.DirectResult is null) _ = await datasets.ReadAsync(acquisition, 0, 1, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is KnowledgeProjectionException or DataSourceAcquisitionException or DataSourceValidationException or StorageException)
            {
                if (directOnly || !binding.AllowStaleOnRefreshFailure || fallback?.DirectResult is not null
                    || fallback?.CompletedAt is not { } completed || binding.MaximumFallbackAge is not { } ceiling
                    || timeProvider.GetUtcNow() - completed > ceiling) throw;
                _ = await ResolveSourceAsync(executionScope, ownerNamespace, binding, dependency, cancellationToken);
                if (fallback.Dataset is not null) _ = await datasets.ReadAsync(fallback, 0, 1, cancellationToken);
                acquisition = fallback;
                refreshFailure = new(refreshId, exception switch
                {
                    KnowledgeProjectionException failure => failure.Code,
                    DataSourceAcquisitionException failure => failure.Code,
                    DataSourceValidationException failure => failure.Code,
                    StorageException failure => failure.Code,
                    _ => "knowledge_refresh_failed"
                }, timeProvider.GetUtcNow());
            }
        }
        if (acquisition is null)
            throw Error("knowledge_projection_acquisition_missing", $"Binding '{binding.Name}' has no matching acquisition.");
        if (acquisition.State != DataSourceAcquisitionState.Succeeded || (acquisition.Manifest is null && acquisition.Content is null && acquisition.DirectResult is null)
            || acquisition.CompletedAt is null)
            throw Error("knowledge_projection_acquisition_not_ready", "The selected acquisition has no successful result.");
        if (refreshFailure is null && (policy is null && !directOnly ? IsStale(binding, acquisition.CompletedAt)
            : !directOnly && policy?.TimeToLive is { } maximum && timeProvider.GetUtcNow() >= acquisition.CompletedAt + maximum))
            throw Error("knowledge_projection_acquisition_stale", "The selected acquisition exceeds its source freshness policy.");
        var selected = acquisition.Dataset is not null
            ? await NativeContentAsync(acquisition, requestId, cancellationToken)
            : acquisition.Manifest!.Artifacts
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
            UsesAcquisitionLifecycle = policy is not null,
            AcquisitionTimeToLive = policy?.TimeToLive,
            InputLeaseId = acquisition.Dataset is not null && acquisition.DirectResult is null ? requestId : null,
            RefreshFailure = refreshFailure,
            DirectInput = acquisition.DirectResult is { } direct ? new(direct.Length, direct.Sha256, direct.ExpiresAt, direct.RequestId) : null,
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
            StorageInput = acquisition.Dataset is { } nativeDataset && acquisition.Content is { } content
                ? new(nativeDataset.Definition.Storage!, content.Key, content.Version, content.Length, content.Sha256)
                : null,
            AcquiredAt = acquisition.CompletedAt.Value,
            AcquisitionComposition = JsonSerializer.SerializeToElement<object?>(acquisition.Dataset is { } native ? native : acquisition.Composition),
            AcquiredArtifacts = selected,
            PreparedArtifacts = selected
        };
    }

    public async Task ReleaseAsync(KnowledgeProjectionInputEvidence evidence, CancellationToken cancellationToken)
    {
        if (evidence.InputLeaseId is not { } requestId) return;
        try
        {
            var acquisition = await acquisitions.GetAsync(evidence.DataSourceNamespace, evidence.AcquisitionId, cancellationToken);
            await datasets.ReleaseProjectionLeaseAsync(acquisition.Value, requestId, cancellationToken);
        }
        catch (DataSourceAcquisitionException exception) { throw Error(exception.Code, exception.Message); }
    }

    private static void ValidateFallback(KnowledgeDataSourceBinding binding, DataSourceResource source)
    {
        if (!binding.AllowStaleOnRefreshFailure && binding.MaximumFallbackAge is null) return;
        if (!binding.AllowStaleOnRefreshFailure || binding.MaximumFallbackAge is not { } maximum || maximum <= TimeSpan.Zero
            || maximum > TimeSpan.FromDays(3650) || source.Definition.Dataset is { Storage: null }
            || source.Definition.Dataset?.Lifecycle?.TimeToLive is { } ttl && maximum < ttl)
            throw new KnowledgeSourceValidationException("knowledge_source_fallback_invalid", "Stale fallback requires a retained input and a positive age ceiling at least as long as the Data Source TTL.");
    }

    private static string DatasetHash(DataSourceResource source) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes<object?>(source.Definition.Dataset is { } dataset
            && dataset.Lifecycle is null && dataset.Orchestration is null && dataset.Transformations.Count == 0 && dataset.Compositions.Count == 0
                ? new { dataset.Storage, dataset.Rule, dataset.Acquisition } : source.Definition.Dataset)));

    private static IEnumerable<DataSourceAcquisitionResource> Compatible(IEnumerable<DataSourceAcquisitionResource> values,
        DataSourceResource source, ResolvedKnowledgeDataSourceBinding dependency, Guid? targetUid) => values.Where(value => value.DataSourceUid == source.Uid
            && value.DataSourceGeneration == source.Generation && value.Target?.Uid == targetUid
            && value.State == DataSourceAcquisitionState.Succeeded && value.CompletedAt is not null && value.ContentRetiredAt is null
            && (value.Dataset is not null && source.Definition.Dataset is not null
                ? JsonElement.DeepEquals(JsonSerializer.SerializeToElement(value.Dataset.Definition), JsonSerializer.SerializeToElement(source.Definition.Dataset))
                : value.Manifest?.Artifacts.Count > 0 && value.Composition?.Profile is { } profile
                    && profile.Uid == dependency.ProfileUid && profile.Version == dependency.ProfileVersion
                    && profile.DefinitionHash == dependency.ProfileDefinitionHash && profile.DependencyHash == dependency.ProfileDependencyHash))
        .OrderByDescending(value => value.CompletedAt).ThenBy(value => value.Name, StringComparer.Ordinal);

    private async Task<KnowledgeProjectionArtifact[]> NativeContentAsync(DataSourceAcquisitionResource acquisition, string requestId, CancellationToken cancellationToken)
    {
        if (acquisition.DirectResult is { } direct)
        {
            var value = await datasets.ReadDirectAsync(acquisition, requestId, cancellationToken);
            return [new() { ArtifactId = "direct-" + acquisition.Uid.ToString("N"), Kind = KnowledgeArtifactKind.Inline,
                Disposition = KnowledgeArtifactDisposition.Publishable, MediaType = "application/json", Digest = direct.Sha256,
                DataContract = DatasetContract(acquisition), ContentBase64 = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)) }];
        }
        await datasets.AcquireProjectionLeaseAsync(acquisition, requestId, cancellationToken);
        var item = acquisition.Content ?? throw Error("knowledge_projection_acquisition_empty", "The acquisition has no content.");
        if (item.Length > 65536) throw Error("knowledge_projection_input_too_large", "The native input exceeds the inline Flow limit.");
        using var buffer = new MemoryStream();
        var chunkSize = Math.Min(65536, acquisition.Dataset!.Definition.Rule.MaximumReadBytes);
        while (buffer.Length < item.Length)
        {
            Agentstration.Storage.Contracts.StorageObjectContent chunk;
            try
            {
                chunk = await datasets.ReadAsync(acquisition, buffer.Length,
                (int)Math.Min(chunkSize, item.Length - buffer.Length), cancellationToken);
            }
            catch (StorageException exception) when (exception.Code == "storage_content_limit_exceeded" && chunkSize > 1)
            {
                // The Storage owns its effective envelope. Discover a compatible bounded chunk
                // without copying physical restrictions into the Knowledge definition.
                chunkSize = Math.Max(1, chunkSize / 2);
                continue;
            }
            var bytes = Convert.FromBase64String(chunk.ContentBase64);
            if (bytes.Length == 0) throw Error("knowledge_projection_input_incomplete", "The acquired content is incomplete.");
            await buffer.WriteAsync(bytes, cancellationToken);
        }
        return [new() { ArtifactId = acquisition.Manifest?.Artifacts.SingleOrDefault()?.ArtifactId ?? acquisition.Name, Kind = KnowledgeArtifactKind.Inline,
            Disposition = KnowledgeArtifactDisposition.Publishable, MediaType = "application/json",
            DataContract = DatasetContract(acquisition), Digest = item.Sha256, ContentBase64 = Convert.ToBase64String(buffer.ToArray()) }];
    }

    private static Agentstration.Schemas.ResolvedDataContract? DatasetContract(DataSourceAcquisitionResource acquisition) =>
        Agentstration.Schemas.DataContracts.Declared(acquisition.Dataset?.OutputSchema,
            $"datasource:{acquisition.DataSourceName}/acquisition:{acquisition.Name}/output", "application/json") is { } contract
                ? contract with { Validated = true } : null;

    private bool IsStale(KnowledgeDataSourceBinding binding, DateTimeOffset? completedAt) =>
        !KnowledgeFreshness.IsFresh(binding, completedAt, timeProvider);

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
        if (source.Value.Definition.Dataset is not null)
        {
            if (dependency.DatasetDependencyHash != DatasetHash(source.Value) || !readiness.Ready)
                throw Error("knowledge_projection_data_source_stale", "The native Data Source dependency is stale or unavailable.");
            return source;
        }
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
        var scope = new FlowRunScope(request.TenantId, new WorkspaceId(request.WorkspaceId), request.PrincipalId) { LogicalScopeRef = request.LogicalScopeRef };
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
                DefinitionScopeRef = request.Flow.ScopeRef,
                Compositions = request.Compositions,
                TransientPayloads = request.TransientPayloads
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
