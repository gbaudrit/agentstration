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

public sealed class DataSourceAcquisitionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record DataSourceFlowRunRequest(
    string RunId,
    ResolvedDataSourceFlowBinding Flow,
    JsonElement Input,
    string CallerId,
    string CorrelationId,
    string? IdempotencyKey,
    Guid TenantId,
    Guid WorkspaceId,
    Guid PrincipalId);

public sealed record DataSourceFlowRunSnapshot(
    string RunId,
    DataSourceAcquisitionState State,
    JsonElement? Output,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? CompletedAt);

public interface IDataSourceAcquisitionCompositionResolver
{
    Task<ResolvedDataSourceAcquisitionComposition> ResolveAsync(
        ResourceScopeRef executionScope,
        ResourceNamespace ownerNamespace,
        ResolvedDataSourceProfile profile,
        CancellationToken cancellationToken);
}

public interface IDataSourceAcquisitionFlowGateway
{
    Task<DataSourceFlowRunSnapshot> StartAsync(DataSourceFlowRunRequest request, CancellationToken cancellationToken);
    Task<DataSourceFlowRunSnapshot?> GetAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId, CancellationToken cancellationToken);
    Task<DataSourceFlowRunSnapshot> CancelAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId, CancellationToken cancellationToken);
}

public interface IDataSourceArtifactReferenceValidator
{
    Task ValidateAsync(Guid workspaceId, IReadOnlyList<DataSourceAcquisitionArtifact> artifacts, CancellationToken cancellationToken);
}

public sealed class DataSourceAcquisitionService(
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

    public const int MaximumManifestArtifacts = 100;
    public const int MaximumParametersBytes = 64 * 1024;
    public const int MaximumIdempotencyKeyLength = 200;

    public async Task<StoredResource<DataSourceAcquisitionResource>> StartAsync(
        ResourceNamespace sourceNamespace,
        string sourceName,
        ResourceScopeRef? sourceScope,
        JsonElement parameters,
        string? correlationId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        ValidateRequest(parameters, idempotencyKey);
        var source = await sources.GetAsync(sourceNamespace, sourceName, sourceScope, cancellationToken)
            ?? throw Error("data_source_not_found", $"Data Source '{sourceNamespace}/{sourceName}' was not found.");
        var sourceOwnerScope = source.Value.ScopeRef
            ?? throw Error("data_source_scope_required", "The Data Source has no ownership scope.");
        var gate = startGates.GetOrAdd((context.WorkspaceId, source.Value.Uid), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!source.Value.Definition.Enabled)
                throw Error("data_source_disabled", $"Data Source '{source.Value.Address}' is disabled.");
            var profile = await profiles.ResolveActiveAsync(sourceOwnerScope, source.Value.Namespace,
                source.Value.Definition.Profile, cancellationToken);
            var configurationIssues = DataSourceProfileService.ValidateConfiguration(
                profile.Definition.ConfigurationSchema, source.Value.Definition.Configuration);
            if (configurationIssues.Count != 0)
                throw Error("data_source_configuration_invalid", string.Join(' ', configurationIssues));

            var executionScope = ResourceScopeRef.Workspace(context.WorkspaceId);
            var composition = await compositions.ResolveAsync(executionScope, source.Value.Namespace, profile, cancellationToken);
            ValidateContract(composition.Flow);
            var requestHash = Hash(JsonSerializer.SerializeToElement(new
            {
                sourceScope = sourceOwnerScope,
                source = new { source.Value.Uid, source.Value.Generation },
                profile = new
                {
                    composition.Profile.ScopeRef,
                    composition.Profile.Uid,
                    composition.Profile.Generation,
                    composition.Profile.Version,
                    composition.Profile.DefinitionHash
                },
                flow = new { composition.Flow.Name, composition.Flow.Namespace, composition.Flow.Version },
                tools = composition.Tools,
                composition.Limits,
                composition.Policies,
                parameters
            }));
            var existing = await store.ListExactAsync<DataSourceAcquisitionResource>(executionScope,
                DataSourceResourceKinds.DataSourceAcquisition, 0, 1000, cancellationToken);
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var match = existing.SingleOrDefault(item => item.Value.DataSourceUid == source.Value.Uid
                    && string.Equals(item.Value.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
                if (match is not null)
                {
                    if (!string.Equals(match.Value.RequestHash, requestHash, StringComparison.Ordinal))
                        throw Error("data_source_acquisition_idempotency_conflict",
                            "The idempotency key is already bound to another acquisition request.");
                    return await SynchronizeAsync(match, context, cancellationToken);
                }
            }
            foreach (var active in existing.Where(item => item.Value.DataSourceUid == source.Value.Uid))
            {
                var synchronized = await SynchronizeAsync(active, context, cancellationToken);
                if (!IsTerminal(synchronized.Value.State))
                    throw Error("data_source_acquisition_already_running",
                        $"Acquisition '{synchronized.Value.Name}' is already active for this Data Source.");
            }

            var acquisitionId = string.IsNullOrWhiteSpace(idempotencyKey)
                ? $"acquisition-{Guid.NewGuid():N}"
                : $"acquisition-{HashText($"{context.WorkspaceId:D}:{source.Value.Uid:D}:{idempotencyKey}")[..32]}";
            var runId = $"flowrun-data-source-{acquisitionId[12..]}";
            var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId) ? acquisitionId : correlationId.Trim();
            var input = JsonSerializer.SerializeToElement(new DataSourceAcquisitionInput
            {
                DataSourceId = $"{source.Value.Namespace}/{source.Value.Name}",
                DataSourceUid = source.Value.Uid,
                DataSourceGeneration = source.Value.Generation,
                Profile = composition.Profile,
                SourceConfiguration = source.Value.Definition.Configuration.Clone(),
                Parameters = parameters.Clone(),
                Caller = new(context.PrincipalId, context.TenantId, context.WorkspaceId),
                CorrelationId = effectiveCorrelationId,
                AcquisitionId = acquisitionId
            });
            var resource = new DataSourceAcquisitionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = DataSourceResourceKinds.DataSourceAcquisition,
                Metadata = new ResourceMetadata { Name = acquisitionId, Namespace = source.Value.Namespace },
                ScopeRef = executionScope,
                Generation = 1,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Accepted },
                DataSourceScopeRef = sourceOwnerScope,
                DataSourceUid = source.Value.Uid,
                DataSourceName = source.Value.Name,
                DataSourceNamespace = source.Value.Namespace,
                DataSourceGeneration = source.Value.Generation,
                Composition = composition,
                FlowRunId = runId,
                State = DataSourceAcquisitionState.Pending,
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
            var result = await scopeOperations.WriteAsync(resource, executionScope, AuthorizationPermissions.RunsExecute,
                async token =>
                {
                    var stored = await store.PutExactAsync(executionScope, resource, null, true, token);
                    try
                    {
                        _ = await flowRuns.StartAsync(new(runId, composition.Flow, input,
                            context.PrincipalId.ToString("D"), effectiveCorrelationId, idempotencyKey,
                            context.TenantId, context.WorkspaceId, context.PrincipalId), token);
                        return stored;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var failed = resource with
                        {
                            Generation = 2,
                            State = DataSourceAcquisitionState.Failed,
                            CompletedAt = timeProvider.GetUtcNow(),
                            ErrorCode = "data_source_acquisition_start_failed",
                            ErrorMessage = exception.Message,
                            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Failed }
                        };
                        _ = await store.PutExactAsync(executionScope, failed, stored.ETag, false, token);
                        throw;
                    }
                }, cancellationToken);
            await AuditAsync(SecurityAuditActions.DataSourceAcquisitionStarted, result.Value.Name, context, cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<DataSourceAcquisitionResource>> ListAsync(
        ResourceNamespace? sourceNamespace,
        string? sourceName,
        ResourceScopeRef? sourceScope,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        var stored = await store.ListExactAsync<DataSourceAcquisitionResource>(ResourceScopeRef.Workspace(context.WorkspaceId),
            DataSourceResourceKinds.DataSourceAcquisition, 0, 1000, cancellationToken);
        IEnumerable<StoredResource<DataSourceAcquisitionResource>> selected = stored;
        if (sourceName is not null)
        {
            var source = await sources.GetAsync(sourceNamespace ?? ResourceNamespace.Default, sourceName,
                sourceScope, cancellationToken)
                ?? throw Error("data_source_not_found",
                    $"Data Source '{sourceNamespace ?? ResourceNamespace.Default}/{sourceName}' was not found.");
            selected = stored.Where(item => item.Value.DataSourceUid == source.Value.Uid
                && item.Value.DataSourceScopeRef == source.Value.ScopeRef);
        }
        var result = new List<DataSourceAcquisitionResource>();
        foreach (var item in selected.OrderByDescending(item => item.Value.CreatedAt))
            result.Add((await SynchronizeAsync(item, context, cancellationToken)).Value);
        return result;
    }

    public async Task<StoredResource<DataSourceAcquisitionResource>> GetAsync(
        ResourceNamespace @namespace,
        string acquisitionId,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        return await GetCoreAsync(@namespace, acquisitionId, context, cancellationToken);
    }

    public async Task<StoredResource<DataSourceAcquisitionResource>> CancelAsync(
        ResourceNamespace @namespace,
        string acquisitionId,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken);
        var stored = await GetCoreAsync(@namespace, acquisitionId, context, cancellationToken);
        if (!IsTerminal(stored.Value.State))
            _ = await flowRuns.CancelAsync(context.WorkspaceId, stored.Value.FlowRunId,
                context.TenantId, context.PrincipalId, cancellationToken);
        var result = await SynchronizeAsync(stored, context, cancellationToken);
        await AuditAsync(SecurityAuditActions.DataSourceAcquisitionCancelled, acquisitionId, context, cancellationToken);
        return result;
    }

    public async Task<StoredResource<DataSourceAcquisitionResource>> RetryAsync(
        ResourceNamespace @namespace,
        string acquisitionId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken);
        var original = await GetCoreAsync(@namespace, acquisitionId, context, cancellationToken);
        if (!IsTerminal(original.Value.State))
            throw Error("data_source_acquisition_retry_not_terminal", "Only a terminal acquisition can be retried.");
        var retried = await StartAsync(original.Value.DataSourceNamespace, original.Value.DataSourceName,
            original.Value.DataSourceScopeRef, original.Value.Parameters, correlationId, null, cancellationToken);
        var updated = retried.Value with
        {
            RetriedFrom = original.Value.Name,
            Attempt = checked(original.Value.Attempt + 1),
            Generation = checked(retried.Value.Generation + 1)
        };
        var result = await store.PutExactAsync(updated.ScopeRef!.Value, updated, retried.ETag, false, cancellationToken);
        await AuditAsync(SecurityAuditActions.DataSourceAcquisitionRetried, acquisitionId, context, cancellationToken);
        return result;
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        var pending = (await store.ListAllAsync<DataSourceAcquisitionResource>(
            DataSourceResourceKinds.DataSourceAcquisition, cancellationToken))
            .Where(item => item.Value.State == DataSourceAcquisitionState.Pending)
            .ToArray();
        foreach (var stored in pending)
        {
            var value = stored.Value;
            if (value.ScopeRef is not { Kind: ResourceScopeKind.Workspace, TargetId: { } workspaceId }) continue;
            if (await flowRuns.GetAsync(workspaceId, value.FlowRunId, value.TenantId, value.CreatedBy,
                    cancellationToken) is not null) continue;
            var input = JsonSerializer.SerializeToElement(new DataSourceAcquisitionInput
            {
                DataSourceId = $"{value.DataSourceNamespace}/{value.DataSourceName}",
                DataSourceUid = value.DataSourceUid,
                DataSourceGeneration = value.DataSourceGeneration,
                Profile = value.Composition.Profile,
                SourceConfiguration = value.SourceConfiguration.Clone(),
                Parameters = value.Parameters.Clone(),
                Caller = new(value.CreatedBy, value.TenantId, workspaceId),
                CorrelationId = value.CorrelationId,
                AcquisitionId = value.Name
            });
            try
            {
                _ = await flowRuns.StartAsync(new(value.FlowRunId, value.Composition.Flow, input,
                    value.CreatedBy.ToString("D"), value.CorrelationId, value.IdempotencyKey,
                    value.TenantId, workspaceId, value.CreatedBy), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failed = value with
                {
                    Generation = checked(value.Generation + 1),
                    State = DataSourceAcquisitionState.Failed,
                    CompletedAt = timeProvider.GetUtcNow(),
                    ErrorCode = "data_source_acquisition_recovery_failed",
                    ErrorMessage = exception.Message,
                    Status = new ResourceStatus { ProvisioningState = ProvisioningState.Failed }
                };
                try { _ = await store.PutExactAsync(value.ScopeRef.Value, failed, stored.ETag, false, cancellationToken); }
                catch (ResourceConcurrencyException) { }
            }
        }
    }

    private async Task<StoredResource<DataSourceAcquisitionResource>> GetCoreAsync(
        ResourceNamespace @namespace,
        string acquisitionId,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var address = ScopedResourceAddress.Create(ResourceScopeRef.Workspace(context.WorkspaceId), @namespace,
            DataSourceResourceKinds.DataSourceAcquisition, acquisitionId);
        var stored = await store.GetExactAsync<DataSourceAcquisitionResource>(address, cancellationToken)
            ?? throw Error("data_source_acquisition_not_found", $"Data Source acquisition '{acquisitionId}' was not found.");
        return await SynchronizeAsync(stored, context, cancellationToken);
    }

    private async Task<StoredResource<DataSourceAcquisitionResource>> SynchronizeAsync(
        StoredResource<DataSourceAcquisitionResource> stored,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var run = await flowRuns.GetAsync(context.WorkspaceId, stored.Value.FlowRunId,
            context.TenantId, context.PrincipalId, cancellationToken);
        if (run is null || run.State == stored.Value.State && (!IsTerminal(run.State) || stored.Value.CompletedAt is not null))
            return stored;
        var state = run.State;
        var errorCode = run.ErrorCode;
        var errorMessage = run.ErrorMessage;
        DataSourceAcquisitionManifest? manifest = stored.Value.Manifest;
        if (state == DataSourceAcquisitionState.Succeeded)
        {
            try
            {
                manifest = ParseManifest(run.Output);
                await artifacts.ValidateAsync(context.WorkspaceId, manifest.Artifacts, cancellationToken);
            }
            catch (DataSourceAcquisitionException exception)
            {
                state = DataSourceAcquisitionState.Failed;
                errorCode = exception.Code;
                errorMessage = exception.Message;
            }
        }
        var updated = stored.Value with
        {
            Generation = checked(stored.Value.Generation + 1),
            State = state,
            Manifest = manifest,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            CompletedAt = IsTerminal(state) ? run.CompletedAt ?? timeProvider.GetUtcNow() : null,
            Status = new ResourceStatus
            {
                ProvisioningState = state switch
                {
                    DataSourceAcquisitionState.Succeeded => ProvisioningState.Succeeded,
                    DataSourceAcquisitionState.Failed or DataSourceAcquisitionState.Cancelled
                        or DataSourceAcquisitionState.TimedOut => ProvisioningState.Failed,
                    _ => ProvisioningState.Accepted
                }
            }
        };
        try
        {
            return await store.PutExactAsync(updated.ScopeRef!.Value, updated, stored.ETag, false, cancellationToken);
        }
        catch (ResourceConcurrencyException)
        {
            return await store.GetExactAsync<DataSourceAcquisitionResource>(ScopedResourceAddress.Create(
                    stored.Value.ScopeRef!.Value, stored.Value.Namespace, stored.Value.Kind, stored.Value.Name), cancellationToken)
                ?? stored;
        }
    }

    private static DataSourceAcquisitionManifest ParseManifest(JsonElement? output)
    {
        if (output is not { ValueKind: JsonValueKind.Object }
            || !output.Value.TryGetProperty("artifacts", out var values)
            || values.ValueKind != JsonValueKind.Array)
            throw Error("data_source_acquisition_manifest_invalid", "The acquisition Flow must return an artifacts array.");
        var entries = values.EnumerateArray().ToArray();
        if (entries.Length > MaximumManifestArtifacts)
            throw Error("data_source_acquisition_manifest_too_large",
                $"An acquisition manifest cannot contain more than {MaximumManifestArtifacts} artifacts.");
        var result = new List<DataSourceAcquisitionArtifact>(entries.Length);
        foreach (var value in entries)
        {
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("artifactId", out var id) || id.ValueKind != JsonValueKind.String
                || !value.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                || !Enum.TryParse<DataSourceArtifactKind>(kind.GetString(), true, out var parsedKind)
                || !value.TryGetProperty("disposition", out var disposition) || disposition.ValueKind != JsonValueKind.String
                || !Enum.TryParse<DataSourceArtifactDisposition>(disposition.GetString(), true, out var parsedDisposition))
                throw Error("data_source_acquisition_manifest_invalid",
                    "Each manifest item requires artifactId, kind and disposition.");
            result.Add(new()
            {
                ArtifactId = id.GetString()!,
                Kind = parsedKind,
                Disposition = parsedDisposition,
                Name = OptionalString(value, "name"),
                MediaType = OptionalString(value, "mediaType"),
                Digest = OptionalString(value, "digest")
            });
        }
        return new() { Artifacts = result };
    }

    private static void ValidateContract(ResolvedDataSourceFlowBinding flow)
    {
        if (!string.Equals(flow.Contract, DataSourceFlowContracts.Acquisition, StringComparison.Ordinal))
            throw Error("data_source_acquisition_contract_required",
                $"Flow '{flow.Namespace}/{flow.Name}:{flow.Version}' must declare contract '{DataSourceFlowContracts.Acquisition}'.");
        RequireProperties(flow.InputSchema,
            ["dataSourceId", "dataSourceUid", "dataSourceGeneration", "profile", "sourceConfiguration",
                "parameters", "caller", "correlationId", "acquisitionId"], "input");
        RequireProperties(flow.OutputSchema, ["artifacts"], "output");
    }

    private static void RequireProperties(JsonElement? schema, IReadOnlyList<string> names, string direction)
    {
        if (schema is not { ValueKind: JsonValueKind.Object }
            || !schema.Value.TryGetProperty("type", out var type) || type.GetString() != "object"
            || !schema.Value.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object
            || !schema.Value.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
            throw Error("data_source_acquisition_contract_invalid",
                $"The {DataSourceFlowContracts.Acquisition} {direction} schema must be an object with properties and required arrays.");
        var requiredNames = required.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var name in names)
            if (!properties.TryGetProperty(name, out _) || !requiredNames.Contains(name))
                throw Error("data_source_acquisition_contract_invalid",
                    $"The {DataSourceFlowContracts.Acquisition} {direction} schema must require property '{name}'.");
    }

    private static void ValidateRequest(JsonElement parameters, string? idempotencyKey)
    {
        if (parameters.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
            throw Error("data_source_acquisition_parameters_invalid", "Acquisition parameters must be a JSON object.");
        if (Encoding.UTF8.GetByteCount(parameters.GetRawText()) > MaximumParametersBytes)
            throw Error("data_source_acquisition_parameters_too_large",
                $"Acquisition parameters cannot exceed {MaximumParametersBytes} bytes.");
        if (idempotencyKey?.Length > MaximumIdempotencyKeyLength)
            throw Error("data_source_acquisition_idempotency_key_too_long",
                $"Idempotency keys cannot exceed {MaximumIdempotencyKeyLength} characters.");
    }

    private Task AuditAsync(string action, string reason, RequestContext context, CancellationToken cancellationToken) =>
        audit.WriteAsync(new(action, ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId,
            WorkspaceId: context.WorkspaceId, ReasonCode: reason), cancellationToken);
    private RequestContext RequireContext() => requestContext.IsInitialized
        ? requestContext.Current
        : throw Error("data_source_acquisition_context_required", "A Workspace request context is required.");
    private static bool IsTerminal(DataSourceAcquisitionState state) => state is DataSourceAcquisitionState.Succeeded
        or DataSourceAcquisitionState.Failed or DataSourceAcquisitionState.Cancelled or DataSourceAcquisitionState.TimedOut;
    private static string? OptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string Hash(JsonElement value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));
    private static string HashText(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static DataSourceAcquisitionException Error(string code, string message) => new(code, message);
}
