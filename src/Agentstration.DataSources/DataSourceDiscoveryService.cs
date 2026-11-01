using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.DataSources;

public sealed class DataSourceDiscoveryService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    ISecurityAuditWriter audit,
    DataSourceManagementService sources,
    DataSourceProfileService profiles,
    IDataSourceAcquisitionCompositionResolver compositions,
    IDataSourceAcquisitionFlowGateway flowRuns,
    IDataSourceArtifactReferenceValidator artifacts,
    TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<(Guid WorkspaceId, Guid SourceUid), SemaphoreSlim> startGates = new();
    public const int MaximumChoices = 1_000;
    public const int MaximumSelectorBytes = 16 * 1024;
    public const int MaximumMetadataEntries = 32;

    public async Task<StoredResource<DataSourceDiscoveryResource>> StartAsync(
        ResourceNamespace sourceNamespace, string sourceName, ResourceScopeRef? sourceScope,
        JsonElement parameters, string? correlationId, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var context = RequireContext();
        DataSourceAcquisitionService.ValidateBoundedRequest(parameters, idempotencyKey, "Discovery");
        if (sourceScope is { } requestedScope)
            DataSourceAcquisitionService.EnsureVisible(requestedScope, context);
        var executionScope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var source = await sources.ResolveForUseAsync(sourceNamespace, sourceName, sourceScope,
            executionScope, cancellationToken);
        var ownerScope = source.Value.ScopeRef ?? throw Error("data_source_scope_required", "The Data Source has no ownership scope.");
        DataSourceAcquisitionService.EnsureVisible(ownerScope, context);
        var gate = startGates.GetOrAdd((context.WorkspaceId, source.Value.Uid), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!source.Value.Definition.Enabled)
                throw Error("data_source_disabled", $"Data Source '{source.Value.Address}' is disabled.");
            var profileUid = source.Value.ProfileUid
                ?? throw Error("data_source_profile_pin_missing",
                    $"Data Source '{source.Value.Address}' does not pin an exact Data Source Profile identity.");
            var profile = await profiles.ResolveActiveAsync(ownerScope, source.Value.Namespace,
                source.Value.Definition.Profile, profileUid, cancellationToken);
            if (profile.Definition.DiscoveryFlow is null)
                throw Error("data_source_discovery_not_supported", $"Profile '{profile.Name}' does not define discovery.");
            var composition = await compositions.ResolveDiscoveryAsync(ownerScope, source.Value.Namespace, profile, cancellationToken);
            ValidateContract(composition.Flow);
            var requestHash = Hash(JsonSerializer.SerializeToElement(new
            {
                source = new { source.Value.Uid, source.Value.Generation },
                profile = new { profile.Uid, profile.Generation, profile.Version, profile.DefinitionHash },
                flow = new { composition.Flow.Name, composition.Flow.Namespace, composition.Flow.Version },
                composition.Tools,
                composition.Limits,
                composition.Policies,
                parameters
            }));
            var existing = await store.ListExactAsync<DataSourceDiscoveryResource>(executionScope,
                DataSourceResourceKinds.DataSourceDiscovery, 0, 1_000, cancellationToken);
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var match = existing.SingleOrDefault(value => value.Value.DataSourceUid == source.Value.Uid
                    && string.Equals(value.Value.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
                if (match is not null)
                {
                    if (!string.Equals(match.Value.RequestHash, requestHash, StringComparison.Ordinal))
                        throw Error("data_source_discovery_idempotency_conflict", "The idempotency key is bound to another discovery request.");
                    return await SynchronizeAsync(match, context, cancellationToken);
                }
            }
            foreach (var active in existing.Where(value => value.Value.DataSourceUid == source.Value.Uid))
            {
                var synchronized = await SynchronizeAsync(active, context, cancellationToken);
                if (!IsTerminal(synchronized.Value.State))
                    throw Error("data_source_discovery_already_running", $"Discovery '{synchronized.Value.Name}' is already active.");
            }

            var id = string.IsNullOrWhiteSpace(idempotencyKey) ? $"discovery-{Guid.NewGuid():N}"
                : $"discovery-{HashText($"{context.WorkspaceId:D}:{source.Value.Uid:D}:{idempotencyKey}")[..32]}";
            var runId = $"flowrun-data-source-{id[10..]}";
            var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId) ? id : correlationId.Trim();
            var input = Input(id, source.Value, profile, parameters, effectiveCorrelationId, context);
            var resource = new DataSourceDiscoveryResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = DataSourceResourceKinds.DataSourceDiscovery,
                Metadata = new() { Name = id, Namespace = source.Value.Namespace },
                ScopeRef = executionScope,
                Generation = 1,
                Status = Accepted(),
                DataSourceScopeRef = ownerScope,
                DataSourceUid = source.Value.Uid,
                DataSourceName = source.Value.Name,
                DataSourceNamespace = source.Value.Namespace,
                DataSourceGeneration = source.Value.Generation,
                Composition = composition,
                FlowRunId = runId,
                State = DataSourceDiscoveryState.Pending,
                CorrelationId = effectiveCorrelationId,
                IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
                RequestHash = requestHash,
                SourceConfiguration = source.Value.Definition.Configuration.Clone(),
                Parameters = parameters.Clone(),
                CreatedBy = context.PrincipalId,
                TenantId = context.TenantId,
                WorkspaceId = context.WorkspaceId,
                CreatedAt = timeProvider.GetUtcNow()
            };
            var stored = await scopeOperations.WriteAsync(resource, executionScope, AuthorizationPermissions.RunsExecute,
                async token =>
                {
                    var created = await store.PutExactAsync(executionScope, resource, null, true, token);
                    try
                    {
                        _ = await flowRuns.StartAsync(new(runId, composition.Flow, input,
                            context.PrincipalId.ToString("D"), effectiveCorrelationId, idempotencyKey,
                            context.TenantId, context.WorkspaceId, context.PrincipalId), token);
                        return created;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var failed = resource with
                        {
                            Generation = 2,
                            State = DataSourceDiscoveryState.Failed,
                            CompletedAt = timeProvider.GetUtcNow(),
                            ErrorCode = "data_source_discovery_start_failed",
                            ErrorMessage = exception.Message,
                            Status = Failed()
                        };
                        _ = await store.PutExactAsync(executionScope, failed, created.ETag, false, token);
                        throw;
                    }
                }, cancellationToken);
            await AuditAsync(SecurityAuditActions.DataSourceDiscoveryStarted, stored.Value.Name, context, cancellationToken);
            return stored;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<DataSourceDiscoveryResource>> ListAsync(ResourceNamespace sourceNamespace,
        string sourceName, ResourceScopeRef? sourceScope, CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        if (sourceScope is { } requestedScope)
            DataSourceAcquisitionService.EnsureVisible(requestedScope, context);
        var source = await sources.GetAsync(sourceNamespace, sourceName, sourceScope, cancellationToken)
            ?? throw Error("data_source_not_found", $"Data Source '{sourceNamespace}/{sourceName}' was not found.");
        DataSourceAcquisitionService.EnsureVisible(source.Value.ScopeRef!.Value, context);
        var stored = await store.ListExactAsync<DataSourceDiscoveryResource>(ResourceScopeRef.Workspace(context.WorkspaceId),
            DataSourceResourceKinds.DataSourceDiscovery, 0, 1_000, cancellationToken);
        var result = new List<DataSourceDiscoveryResource>();
        foreach (var item in stored.Where(value => value.Value.DataSourceUid == source.Value.Uid)
                     .OrderByDescending(value => value.Value.CreatedAt))
            result.Add((await SynchronizeAsync(item, context, cancellationToken)).Value);
        return result;
    }

    public async Task<StoredResource<DataSourceDiscoveryResource>> GetAsync(ResourceNamespace ns, string id,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        var stored = await store.GetExactAsync<DataSourceDiscoveryResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Workspace(context.WorkspaceId), ns, DataSourceResourceKinds.DataSourceDiscovery, id), cancellationToken)
            ?? throw Error("data_source_discovery_not_found", $"Discovery '{id}' was not found.");
        return await SynchronizeAsync(stored, context, cancellationToken);
    }

    public async Task<StoredResource<DataSourceDiscoveryResource>> CancelAsync(ResourceNamespace ns, string id,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken);
        var stored = await GetAsync(ns, id, cancellationToken);
        if (!IsTerminal(stored.Value.State))
            _ = await flowRuns.CancelAsync(context.WorkspaceId, stored.Value.FlowRunId, context.TenantId,
                context.PrincipalId, cancellationToken);
        var result = await SynchronizeAsync(stored, context, cancellationToken);
        await AuditAsync(SecurityAuditActions.DataSourceDiscoveryCancelled, id, context, cancellationToken);
        return result;
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        foreach (var stored in (await store.ListAllAsync<DataSourceDiscoveryResource>(DataSourceResourceKinds.DataSourceDiscovery,
                     cancellationToken)).Where(value => value.Value.State == DataSourceDiscoveryState.Pending))
        {
            var value = stored.Value;
            if (value.ScopeRef is not { Kind: ResourceScopeKind.Workspace, TargetId: { } workspaceId }) continue;
            if (await flowRuns.GetAsync(workspaceId, value.FlowRunId, value.TenantId, value.CreatedBy, cancellationToken) is not null) continue;
            var input = JsonSerializer.SerializeToElement(new DataSourceDiscoveryInput
            {
                DataSourceId = $"{value.DataSourceNamespace}/{value.DataSourceName}",
                DataSourceUid = value.DataSourceUid,
                DataSourceGeneration = value.DataSourceGeneration,
                Profile = value.Composition.Profile,
                SourceConfiguration = value.SourceConfiguration.Clone(),
                Parameters = value.Parameters.Clone(),
                Caller = new(value.CreatedBy, value.TenantId, workspaceId),
                CorrelationId = value.CorrelationId,
                DiscoveryId = value.Name
            });
            try
            {
                _ = await flowRuns.StartAsync(new(value.FlowRunId, value.Composition.Flow, input,
                value.CreatedBy.ToString("D"), value.CorrelationId, value.IdempotencyKey, value.TenantId,
                workspaceId, value.CreatedBy), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failed = value with
                {
                    Generation = checked(value.Generation + 1),
                    State = DataSourceDiscoveryState.Failed,
                    CompletedAt = timeProvider.GetUtcNow(),
                    ErrorCode = "data_source_discovery_recovery_failed",
                    ErrorMessage = exception.Message,
                    Status = Failed()
                };
                try { _ = await store.PutExactAsync(value.ScopeRef.Value, failed, stored.ETag, false, cancellationToken); }
                catch (ResourceConcurrencyException) { }
            }
        }
    }

    private async Task<StoredResource<DataSourceDiscoveryResource>> SynchronizeAsync(
        StoredResource<DataSourceDiscoveryResource> stored, RequestContext context, CancellationToken cancellationToken)
    {
        var run = await flowRuns.GetAsync(context.WorkspaceId, stored.Value.FlowRunId, context.TenantId,
            context.PrincipalId, cancellationToken);
        if (run is null) return stored;
        var state = (DataSourceDiscoveryState)(int)run.State;
        if (state == stored.Value.State && (!IsTerminal(state) || stored.Value.CompletedAt is not null)) return stored;
        var errorCode = run.ErrorCode;
        var errorMessage = run.ErrorMessage;
        var manifest = stored.Value.Manifest;
        if (state == DataSourceDiscoveryState.Succeeded)
        {
            try
            {
                manifest = ParseManifest(run.Output);
                await artifacts.ValidateAsync(context.WorkspaceId, manifest.Artifacts, cancellationToken);
            }
            catch (DataSourceAcquisitionException exception)
            { state = DataSourceDiscoveryState.Failed; errorCode = exception.Code; errorMessage = exception.Message; }
        }
        var updated = stored.Value with
        {
            Generation = checked(stored.Value.Generation + 1),
            State = state,
            Manifest = manifest,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            CompletedAt = IsTerminal(state) ? run.CompletedAt ?? timeProvider.GetUtcNow() : null,
            Status = state == DataSourceDiscoveryState.Succeeded ? Succeeded() : IsTerminal(state) ? Failed() : Accepted()
        };
        try { return await store.PutExactAsync(updated.ScopeRef!.Value, updated, stored.ETag, false, cancellationToken); }
        catch (ResourceConcurrencyException)
        {
            return await store.GetExactAsync<DataSourceDiscoveryResource>(ScopedResourceAddress.Create(
                updated.ScopeRef!.Value, updated.Namespace, updated.Kind, updated.Name), cancellationToken) ?? stored;
        }
    }

    private static DataSourceDiscoveryManifest ParseManifest(JsonElement? output)
    {
        if (output is not { ValueKind: JsonValueKind.Object }
            || !output.Value.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            throw Error("data_source_discovery_manifest_invalid", "The discovery Flow must return a choices array.");
        var choiceValues = choices.EnumerateArray().ToArray();
        if (choiceValues.Length > MaximumChoices)
            throw Error("data_source_discovery_manifest_too_large", $"A discovery cannot return more than {MaximumChoices} choices.");
        var parsed = new List<DataSourceDiscoveryChoice>(choiceValues.Length);
        foreach (var value in choiceValues)
        {
            if (value.ValueKind != JsonValueKind.Object || !RequiredString(value, "id", out var id)
                || !RequiredString(value, "displayName", out var displayName)
                || !value.TryGetProperty("selector", out var selector))
                throw Error("data_source_discovery_manifest_invalid", "Each choice requires id, displayName and selector.");
            if (Encoding.UTF8.GetByteCount(selector.GetRawText()) > MaximumSelectorBytes)
                throw Error("data_source_discovery_selector_too_large", $"Selector '{id}' exceeds {MaximumSelectorBytes} bytes.");
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (value.TryGetProperty("metadata", out var metadataValue))
            {
                if (metadataValue.ValueKind != JsonValueKind.Object || metadataValue.EnumerateObject().Count() > MaximumMetadataEntries)
                    throw Error("data_source_discovery_metadata_invalid", "Choice metadata must be a bounded string map.");
                foreach (var property in metadataValue.EnumerateObject())
                    if (property.Value.ValueKind != JsonValueKind.String || property.Name.Length > 128 || property.Value.GetString()!.Length > 1_024)
                        throw Error("data_source_discovery_metadata_invalid", "Choice metadata keys and values are bounded strings.");
                    else metadata.Add(property.Name, property.Value.GetString()!);
            }
            parsed.Add(new()
            {
                Id = id!,
                ParentId = OptionalString(value, "parentId"),
                DisplayName = displayName!,
                Description = OptionalString(value, "description"),
                Selector = selector.Clone(),
                Metadata = metadata,
                Eligible = !value.TryGetProperty("eligible", out var eligible) || eligible.ValueKind != JsonValueKind.False,
                IneligibleReason = OptionalString(value, "ineligibleReason")
            });
        }
        ValidateHierarchy(parsed);
        var acquisitionManifest = DataSourceAcquisitionService.ParseArtifactManifest(output, artifactsOptional: true);
        return new() { Choices = parsed, Artifacts = acquisitionManifest.Artifacts };
    }

    private static void ValidateHierarchy(IReadOnlyList<DataSourceDiscoveryChoice> choices)
    {
        var byId = new Dictionary<string, DataSourceDiscoveryChoice>(StringComparer.Ordinal);
        foreach (var choice in choices)
            if (string.IsNullOrWhiteSpace(choice.Id) || choice.Id.Length > 256 || !byId.TryAdd(choice.Id, choice))
                throw Error("data_source_discovery_choice_identity_invalid", "Choice identities must be unique, non-empty and at most 256 characters.");
        foreach (var choice in choices)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { choice.Id };
            var parent = choice.ParentId;
            while (parent is not null)
            {
                if (!byId.TryGetValue(parent, out var parentChoice))
                    throw Error("data_source_discovery_parent_missing", $"Choice '{choice.Id}' references missing parent '{parent}'.");
                if (!seen.Add(parent)) throw Error("data_source_discovery_hierarchy_cycle", $"Choice '{choice.Id}' creates a hierarchy cycle.");
                parent = parentChoice.ParentId;
            }
        }
    }

    private static void ValidateContract(ResolvedDataSourceFlowBinding flow)
    {
        if (!string.Equals(flow.Contract, DataSourceFlowContracts.Discovery, StringComparison.Ordinal))
            throw Error("data_source_discovery_contract_required", $"Flow '{flow.Namespace}/{flow.Name}:{flow.Version}' must declare contract '{DataSourceFlowContracts.Discovery}'.");
        // Compatibility seam for #759: validation currently consumes the published effective schemas exposed
        // by the existing Flow resolver. It deliberately does not resolve schemas or inspect inline bindings here.
        DataSourceAcquisitionService.RequireContractProperties(flow.InputSchema,
            ["dataSourceId", "dataSourceUid", "dataSourceGeneration", "profile", "sourceConfiguration", "parameters", "caller", "correlationId", "discoveryId"],
            "input", DataSourceFlowContracts.Discovery, "data_source_discovery_contract_invalid");
        DataSourceAcquisitionService.RequireContractProperties(flow.OutputSchema, ["choices"], "output",
            DataSourceFlowContracts.Discovery, "data_source_discovery_contract_invalid");
    }

    private static JsonElement Input(string id, DataSourceResource source, ResolvedDataSourceProfile profile,
        JsonElement parameters, string correlationId, RequestContext context) => JsonSerializer.SerializeToElement(new DataSourceDiscoveryInput
        {
            DataSourceId = $"{source.Namespace}/{source.Name}",
            DataSourceUid = source.Uid,
            DataSourceGeneration = source.Generation,
            Profile = profile,
            SourceConfiguration = source.Definition.Configuration.Clone(),
            Parameters = parameters.Clone(),
            Caller = new(context.PrincipalId, context.TenantId, context.WorkspaceId),
            CorrelationId = correlationId,
            DiscoveryId = id
        });
    private RequestContext RequireContext() => requestContext.IsInitialized ? requestContext.Current
        : throw Error("data_source_discovery_context_required", "A Workspace request context is required.");
    private Task AuditAsync(string action, string reason, RequestContext context, CancellationToken token) =>
        audit.WriteAsync(new(action, ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId,
            WorkspaceId: context.WorkspaceId, ReasonCode: reason), token);
    private static bool IsTerminal(DataSourceDiscoveryState state) => state is DataSourceDiscoveryState.Succeeded
        or DataSourceDiscoveryState.Failed or DataSourceDiscoveryState.Cancelled or DataSourceDiscoveryState.TimedOut;
    private static bool RequiredString(JsonElement value, string name, out string? result)
    { result = OptionalString(value, name); return !string.IsNullOrWhiteSpace(result); }
    private static string? OptionalString(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string Hash(JsonElement value) => HashText(value.GetRawText());
    private static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ResourceStatus Accepted() => new() { ProvisioningState = ProvisioningState.Accepted };
    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
    private static ResourceStatus Failed() => new() { ProvisioningState = ProvisioningState.Failed };
    private static DataSourceAcquisitionException Error(string code, string message) => new(code, message);
}

public sealed class DataSourceAcquisitionTargetService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    ISecurityAuditWriter audit,
    DataSourceManagementService sources,
    DataSourceProfileService profiles,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<DataSourceAcquisitionTargetView>> ListAsync(ResourceNamespace sourceNamespace,
        string sourceName, ResourceScopeRef? sourceScope, CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.ResourcesRead, cancellationToken);
        if (sourceScope is { } requestedScope)
            DataSourceAcquisitionService.EnsureVisible(requestedScope, context);
        var source = await sources.GetAsync(sourceNamespace, sourceName, sourceScope, cancellationToken)
            ?? throw Error("data_source_not_found", $"Data Source '{sourceNamespace}/{sourceName}' was not found.");
        DataSourceAcquisitionService.EnsureVisible(source.Value.ScopeRef!.Value, context);
        var profile = await profiles.ResolveActiveAsync(source.Value.ScopeRef!.Value, source.Value.Namespace,
            source.Value.Definition.Profile, cancellationToken);
        var discoveries = await store.ListExactAsync<DataSourceDiscoveryResource>(ResourceScopeRef.Workspace(context.WorkspaceId),
            DataSourceResourceKinds.DataSourceDiscovery, 0, 1_000, cancellationToken);
        var latestDiscovery = discoveries.Where(value => value.Value.DataSourceUid == source.Value.Uid
            && value.Value.State == DataSourceDiscoveryState.Succeeded).OrderByDescending(value => value.Value.CompletedAt).FirstOrDefault();
        var targets = await store.ListExactAsync<DataSourceAcquisitionTargetResource>(ResourceScopeRef.Workspace(context.WorkspaceId),
            DataSourceResourceKinds.DataSourceAcquisitionTarget, 0, 1_000, cancellationToken);
        return targets.Where(value => value.Value.DataSourceUid == source.Value.Uid)
            .Select(value => View(value.Value, source.Value, profile, latestDiscovery?.Value)).ToArray();
    }

    public async Task<StoredResource<DataSourceAcquisitionTargetResource>> PromoteAsync(ResourceNamespace ns,
        string discoveryId, PromoteDataSourceAcquisitionTargetRequest request, CancellationToken cancellationToken)
    {
        var context = RequireContext();
        ValidateName(request.Name);
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var discovery = await store.GetExactAsync<DataSourceDiscoveryResource>(ScopedResourceAddress.Create(scope, ns,
            DataSourceResourceKinds.DataSourceDiscovery, discoveryId), cancellationToken)
            ?? throw Error("data_source_discovery_not_found", $"Discovery '{discoveryId}' was not found.");
        if (discovery.Value.State != DataSourceDiscoveryState.Succeeded || discovery.Value.Manifest is null)
            throw Error("data_source_discovery_not_ready", $"Discovery '{discoveryId}' is not successful.");
        var choice = discovery.Value.Manifest.Choices.SingleOrDefault(value => string.Equals(value.Id, request.ChoiceId, StringComparison.Ordinal))
            ?? throw Error("data_source_discovery_choice_not_found", $"Choice '{request.ChoiceId}' was not found.");
        if (!choice.Eligible) throw Error("data_source_discovery_choice_ineligible", choice.IneligibleReason ?? $"Choice '{choice.Id}' is not eligible.");
        var target = new DataSourceAcquisitionTargetResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSourceAcquisitionTarget,
            Metadata = new() { Name = request.Name, Namespace = ns },
            ScopeRef = scope,
            Generation = 1,
            Status = new() { ProvisioningState = ProvisioningState.Succeeded },
            DisplayName = request.DisplayName ?? choice.DisplayName,
            Description = request.Description ?? choice.Description,
            DataSourceScopeRef = discovery.Value.DataSourceScopeRef,
            DataSourceUid = discovery.Value.DataSourceUid,
            DataSourceName = discovery.Value.DataSourceName,
            DataSourceNamespace = discovery.Value.DataSourceNamespace,
            DataSourceGeneration = discovery.Value.DataSourceGeneration,
            DiscoveryId = discovery.Value.Name,
            DiscoveryUid = discovery.Value.Uid,
            ChoiceId = choice.Id,
            ChoiceDisplayName = choice.DisplayName,
            Selector = choice.Selector.Clone(),
            ChoiceMetadata = choice.Metadata,
            Profile = discovery.Value.Composition.Profile,
            PromotedAt = timeProvider.GetUtcNow(),
            PromotedBy = context.PrincipalId
        };
        var stored = await scopeOperations.WriteAsync(target, scope, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scope, target, null, true, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.DataSourceAcquisitionTargetPromoted, target.Name, context, cancellationToken);
        return stored;
    }

    public async Task<StoredResource<DataSourceAcquisitionTargetResource>> PutAsync(ResourceNamespace ns, string name,
        PutDataSourceAcquisitionTargetRequest request, string? ifMatch, CancellationToken cancellationToken)
    {
        RequireEtag(ifMatch);
        var context = RequireContext();
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var existing = await store.GetExactAsync<DataSourceAcquisitionTargetResource>(ScopedResourceAddress.Create(scope, ns,
            DataSourceResourceKinds.DataSourceAcquisitionTarget, name), cancellationToken)
            ?? throw Error("data_source_acquisition_target_not_found", $"Acquisition target '{name}' was not found.");
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 200 || request.Description?.Length > 2_000)
            throw Error("data_source_acquisition_target_metadata_invalid", "Target display name or description is invalid.");
        var updated = existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            DisplayName = request.DisplayName,
            Description = request.Description,
            Enabled = request.Enabled
        };
        return await scopeOperations.WriteAsync(updated, scope, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scope, updated, ifMatch, false, token), cancellationToken);
    }

    public async Task<StoredResource<DataSourceAcquisitionTargetResource>> ReconcileAsync(ResourceNamespace ns, string name,
        ReconcileDataSourceAcquisitionTargetRequest request, string? ifMatch, CancellationToken cancellationToken)
    {
        RequireEtag(ifMatch);
        var context = RequireContext();
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var existing = await store.GetExactAsync<DataSourceAcquisitionTargetResource>(ScopedResourceAddress.Create(scope, ns,
            DataSourceResourceKinds.DataSourceAcquisitionTarget, name), cancellationToken)
            ?? throw Error("data_source_acquisition_target_not_found", $"Acquisition target '{name}' was not found.");
        var discovery = await store.GetExactAsync<DataSourceDiscoveryResource>(ScopedResourceAddress.Create(scope, ns,
            DataSourceResourceKinds.DataSourceDiscovery, request.DiscoveryId), cancellationToken)
            ?? throw Error("data_source_discovery_not_found", $"Discovery '{request.DiscoveryId}' was not found.");
        if (discovery.Value.State != DataSourceDiscoveryState.Succeeded || discovery.Value.Manifest is null)
            throw Error("data_source_discovery_not_ready", $"Discovery '{request.DiscoveryId}' is not successful.");
        if (discovery.Value.DataSourceUid != existing.Value.DataSourceUid)
            throw Error("data_source_acquisition_target_mismatch", "The discovery belongs to another Data Source.");
        var choice = discovery.Value.Manifest.Choices.SingleOrDefault(value => value.Id == request.ChoiceId)
            ?? throw Error("data_source_discovery_choice_not_found", $"Choice '{request.ChoiceId}' was not found.");
        if (!choice.Eligible) throw Error("data_source_discovery_choice_ineligible", choice.IneligibleReason ?? $"Choice '{choice.Id}' is not eligible.");
        var updated = existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            DataSourceGeneration = discovery.Value.DataSourceGeneration,
            DiscoveryId = discovery.Value.Name,
            DiscoveryUid = discovery.Value.Uid,
            ChoiceId = choice.Id,
            ChoiceDisplayName = choice.DisplayName,
            Selector = choice.Selector.Clone(),
            ChoiceMetadata = choice.Metadata,
            Profile = discovery.Value.Composition.Profile,
            PromotedAt = timeProvider.GetUtcNow(),
            PromotedBy = context.PrincipalId
        };
        var stored = await scopeOperations.WriteAsync(updated, scope, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scope, updated, ifMatch, false, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.DataSourceAcquisitionTargetReconciled, stored.Value.Name, context, cancellationToken);
        return stored;
    }

    public async Task DeleteAsync(ResourceNamespace ns, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        RequireEtag(ifMatch);
        var context = RequireContext();
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        await scopeOperations.WriteAsync(DataSourceResourceKinds.DataSourceAcquisitionTarget, scope,
            AuthorizationPermissions.ResourcesDelete, async token =>
            { await store.DeleteExactAsync(ScopedResourceAddress.Create(scope, ns, DataSourceResourceKinds.DataSourceAcquisitionTarget, name), ifMatch, token); return true; }, cancellationToken);
    }

    private static DataSourceAcquisitionTargetView View(DataSourceAcquisitionTargetResource target,
        DataSourceResource source, ResolvedDataSourceProfile profile, DataSourceDiscoveryResource? latest)
    {
        var reasons = new List<string>();
        if (target.DataSourceGeneration != source.Generation) reasons.Add("The Data Source changed after this target was promoted.");
        if (target.Profile.Uid != profile.Uid || target.Profile.Generation != profile.Generation
            || !string.Equals(target.Profile.Version, profile.Version, StringComparison.Ordinal)
            || !string.Equals(target.Profile.DefinitionHash, profile.DefinitionHash, StringComparison.Ordinal))
            reasons.Add("The active Data Source Profile revision changed after this target was promoted.");
        if (latest is not null && latest.Uid != target.DiscoveryUid)
            reasons.Add("A newer successful discovery exists; explicitly reconcile or promote a new target.");
        return new(target, reasons.Count != 0, reasons);
    }
    private RequestContext RequireContext() => requestContext.IsInitialized ? requestContext.Current
        : throw Error("data_source_target_context_required", "A Workspace request context is required.");
    private Task AuditAsync(string action, string reason, RequestContext context, CancellationToken token) => audit.WriteAsync(
        new(action, ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId, WorkspaceId: context.WorkspaceId, ReasonCode: reason), token);
    private static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw Error("data_source_acquisition_target_name_invalid", "Target names contain unsupported characters or are too long.");
    }
    private static void RequireEtag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Error("data_source_acquisition_target_etag_required", "An If-Match ETag is required for target mutations.");
    }
    private static DataSourceAcquisitionException Error(string code, string message) => new(code, message);
}
