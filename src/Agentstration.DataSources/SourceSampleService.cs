using System.Text.Json;
using System.Threading.Channels;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;

namespace Agentstration.DataSources;

public interface ISourceSampleStore
{
    void Put(SourceSample sample);
    SourceSample? Get(Guid workspaceId, string id);
    void Delete(Guid workspaceId, string id);
}
public interface ISourceOperationSampleExecutor
{
    Task<JsonElement> ExecuteAsync(DataSourceOperationAcquisitionBinding acquisition, ResourceScopeRef consumer,
        string correlationId, CancellationToken ct);
}

/// <summary>Explicit authoring commands. Raw content uses the existing bounded ephemeral payload store.</summary>
public sealed class SourceSampleService(ISourceSampleStore store, ISourceOperationSampleExecutor executor,
    ConnectedServiceOperationService operations, ICurrentRequestContext current, IRequestContextScopeFactory scopes,
    IAuthorizationService authorization, ISchemaFormatRegistry formats, ISourceSequenceService sequences, TimeProvider clock,
    DataSourceManagementService sources, DataSourceDatasetAcquisitionService datasets, Agentstration.ResourceManagement.IResourceStore resources, Agentstration.Schemas.Contracts.ISchemaBindingResolver schemaBindings, DataSourceDatasetCompositionService compositions) : ISourceAuthoringSamples
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public const int MaximumSampleBytes = 1024 * 1024;
    private readonly Channel<SourceSample> queue = Channel.CreateBounded<SourceSample>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait });
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> active = new();
    private readonly DataSourceExecutionContext execution = new(current, scopes);

    public async Task<SourceSample> AcquireAsync(AcquireSourceSampleRequest request, CancellationToken ct)
    {
        var context = execution.RequireContext("source_sample_context_required");
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, ct);
        _ = await ValidateOperationAsync(request.Acquisition, execution.ConsumerScope, ct);
        var rawSchema = request.Acquisition.OutputSchemaBinding is { } binding
            ? await schemaBindings.ResolveAsync("$.sample.acquisition.outputSchema", binding,
                new(execution.ConsumerScope, request.Namespace ?? request.Acquisition.Operation.Operation.Namespace, SchemaFormatIds.JsonSchema), ct) : null;
        if (rawSchema is not null) _ = DataContracts.Declared(JsonSchemaSelection.Select(rawSchema.Content), rawSchema.Path);
        var sample = new SourceSample(Guid.NewGuid().ToString("N"), context.PrincipalId, context.TenantId, context.WorkspaceId,
            execution.ConsumerScope, request.Acquisition with { OutputSchemaSnapshot = rawSchema }, SourceSampleState.Pending,
            clock.GetUtcNow(), clock.GetUtcNow() + Lifetime)
        { Restriction = context.Restriction };
        store.Put(sample);
        if (!queue.Writer.TryWrite(sample))
        {
            store.Delete(context.WorkspaceId, sample.Id);
            throw Error("source_sample_capacity");
        }
        return sample;
    }

    async Task<SourceSample> ISourceAuthoringSamples.GetAsync(string id, CancellationToken ct)
    {
        try { return await GetAsync(id, ct); }
        catch (DataSourceAcquisitionException error) { throw new SourceSampleUnavailableException(error.Code); }
    }

    public async Task<SourceSample> GetAsync(string id, CancellationToken ct)
    {
        var sample = await GetOwnedAsync(id, ct);
        if (sample.InputSamples is { } inputs)
        {
            foreach (var input in inputs.Values) _ = await GetAsync(input, ct);
        }
        else if (sample.PreparedInput is { } prepared)
        {
            _ = await GetAsync(prepared.RawSampleId, ct);
            var source = await sources.ResolveForUseAsync(prepared.Source.Namespace ?? ResourceNamespace.Default, prepared.Source.Name,
                prepared.Source.ScopeRef, sample.Consumer, ct);
            if (source.Value.Uid != prepared.SourceUid || source.Value.Generation != prepared.SourceGeneration || !source.Value.Definition.Enabled)
                throw Error("source_sample_consumed_unavailable");
        }
        else if (sample.ConsumedInput is { } consumed)
        {
            var acquired = await ConsumedAcquisitionAsync(consumed.Source, consumed.AcquisitionNamespace, consumed.AcquisitionId, ct);
            if (acquired.Uid != consumed.AcquisitionUid || acquired.Content!.Sha256 != consumed.Sha256) throw Error("source_sample_consumed_unavailable");
        }
        else _ = await ValidateOperationAsync(sample.Acquisition, sample.Consumer, ct);
        if (sample.Acquisition.OutputSchemaSnapshot is { Source: { } reference } frozen)
        {
            var checkedSchema = await schemaBindings.ResolveAsync(frozen.Path, new() { Reference = reference }, new(sample.Consumer, reference.Namespace ?? sample.Acquisition.Operation.Operation.Namespace, frozen.Format), ct);
            if (checkedSchema.SchemaUid != frozen.SchemaUid || checkedSchema.DefinitionHash != frozen.DefinitionHash) throw Error("source_sample_contract_changed");
        }
        return sample;
    }

    public async Task<IReadOnlyList<ConsumedSampleCandidate>> CandidatesAsync(ResourceReference reference, CancellationToken ct)
    {
        var context = execution.RequireContext("source_sample_context_required");
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, ct);
        var source = await sources.ResolveForUseAsync(reference.Namespace ?? ResourceNamespace.Default, reference.Name, reference.ScopeRef, execution.ConsumerScope, ct);
        var records = await execution.InWorkspaceAsync(() => resources.ListExactAsync<DataSourceAcquisitionResource>(ResourceScopeRef.Workspace(context.WorkspaceId), DataSourceResourceKinds.DataSourceAcquisition, 0, 1000, ct));
        return records.Where(r => Compatible(r.Value, source.Value, context)).OrderByDescending(r => r.Value.CompletedAt)
            .Take(100).Select(r => new ConsumedSampleCandidate(r.Value.Name, r.Value.Uid, r.Value.Namespace, r.Value.CompletedAt)).ToArray();
    }

    public async Task<SourceSample> PrepareConsumedAsync(PrepareConsumedSampleRequest request, CancellationToken ct)
    {
        var context = execution.RequireContext("source_sample_context_required");
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, ct);
        var acquisition = await ConsumedAcquisitionAsync(request.Source, request.AcquisitionNamespace, request.AcquisitionId, ct);
        var content = acquisition.Content!;
        if (content.Length > MaximumSampleBytes) throw Error("source_sample_too_large");
        using var buffer = new MemoryStream();
        var chunkSize = Math.Min(32768, acquisition.Dataset!.Definition.Rule.MaximumReadBytes);
        while (buffer.Length < content.Length)
        {
            var chunk = await datasets.ReadAsync(acquisition, buffer.Length, (int)Math.Min(chunkSize, content.Length - buffer.Length), ct);
            var bytes = Convert.FromBase64String(chunk.ContentBase64);
            if (bytes.Length == 0 || buffer.Length + bytes.Length > content.Length) throw Error("source_sample_content_incomplete");
            await buffer.WriteAsync(bytes, ct);
        }
        var manifest = JsonSerializer.SerializeToElement(new
        {
            artifacts = new[] { new
        {
            artifactId = acquisition.Manifest?.Artifacts.SingleOrDefault()?.ArtifactId ?? acquisition.Name,
            kind = "inline", disposition = "publishable", mediaType = "application/json", digest = content.Sha256,
            dataContract = acquisition.RecordContract is { } record ? JsonRecordProjection.Collection(record) : null,
            recordContract = acquisition.RecordContract, contentBase64 = Convert.ToBase64String(buffer.ToArray())
        }}
        }, JsonSerializerOptions.Web);
        var consumed = new ConsumedSampleReference(request.Source, acquisition.DataSourceUid, acquisition.DataSourceGeneration,
            acquisition.Name, acquisition.Uid, acquisition.Namespace, content.Sha256!, acquisition.Target?.Name);
        var sample = new SourceSample(Guid.NewGuid().ToString("N"), context.PrincipalId, context.TenantId, context.WorkspaceId,
            execution.ConsumerScope, acquisition.Dataset.Definition.Acquisition, SourceSampleState.Ready, clock.GetUtcNow(), clock.GetUtcNow() + Lifetime,
            manifest, ManifestContract("data-source-consumed/" + acquisition.Name))
        { Restriction = context.Restriction, IsRawOperationResponse = false, ConsumedInput = consumed, Provenance = acquisition.RequestHash };
        store.Put(sample);
        return sample;
    }

    public async Task<SourceSample> PrepareDataSourceAsync(PrepareDataSourceSampleRequest request, CancellationToken ct)
    {
        var raw = await GetAsync(request.RawSampleId, ct);
        if (!raw.IsRawOperationResponse || raw.State != SourceSampleState.Ready || raw.Value is not { } value) throw Error("source_sample_not_conformant");
        var source = await sources.ResolveForUseAsync(request.Source.Namespace ?? ResourceNamespace.Default, request.Source.Name,
            request.Source.ScopeRef, execution.ConsumerScope, ct);
        var definition = source.Value.Definition.Dataset;
        if (!source.Value.Definition.Enabled || definition is null || definition.Orchestration is not null
            || definition.Compositions.Keys.Any(k => k != "transformations")) throw Error("source_sample_composition_unsupported");
        if (definition.Acquisition.Operation != raw.Acquisition.Operation
            || !JsonElement.DeepEquals(definition.Acquisition.Parameters, raw.Acquisition.Parameters)
            || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(definition.Acquisition.OutputSchemaBinding), JsonSerializer.SerializeToElement(raw.Acquisition.OutputSchemaBinding)))
            throw Error("source_sample_configuration_changed");
        var resolved = await compositions.ResolveAsync(source.Value, execution.ConsumerScope, ct);
        var calls = definition.Transformations.Count > 0 ? definition.Transformations : definition.Compositions.GetValueOrDefault("transformations") ?? [];
        var replay = await sequences.ReplayAsync(new(raw.Consumer, source.Value.Namespace, calls, raw.Contract, true, raw.ContentContract), value, raw.Id, ct);
        if (replay.ErrorCode is { } code) throw Error(code);
        var result = replay.Values[^1];
        var projection = JsonRecordProjection.Parse(result);
        if (definition.OutputSchemaSnapshot is not null)
            DataContracts.Validate(DataSourceDatasetCompositionService.Contract(resolved, "data-source-prepared-sample"), projection?.Value ?? result, "sample-dataset-output");
        else DataSourceDatasetCompositionService.ValidateOutput(resolved.OutputSchema, result, MaximumSampleBytes);
        var records = projection ?? JsonRecordProjection.Normalize(result, DataSourceDatasetCompositionService.Contract(resolved, "data-source-prepared-sample"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(records.Value);
        if (bytes.Length > MaximumSampleBytes) throw Error("source_sample_too_large");
        var prepared = raw with
        {
            Id = Guid.NewGuid().ToString("N"),
            IsRawOperationResponse = false,
            Contract = ManifestContract("data-source-prepared/" + source.Value.Name),
            ContentContract = null,
            PreparedInput = new(request.Source, source.Value.Uid, source.Value.Generation, raw.Id, replay.Fingerprint),
            Provenance = replay.Fingerprint,
            Value = JsonSerializer.SerializeToElement(new
            {
                artifacts = new[] { new
            {
                artifactId = "sample-source-" + raw.Id, kind = "inline", disposition = "publishable", mediaType = "application/json",
                digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                dataContract = records.CollectionContract, recordContract = records.RecordContract, contentBase64 = Convert.ToBase64String(bytes)
            }}
            }, JsonSerializerOptions.Web)
        };
        store.Put(prepared);
        return prepared;
    }

    public async Task<SourceSample> CapturePreparedAsync(IReadOnlyDictionary<string, string> inputSamples, JsonElement value,
        string draftFingerprint, JsonElement evidence, CancellationToken ct)
    {
        if (inputSamples.Count is < 1 or > 32 || JsonSerializer.SerializeToUtf8Bytes(value).Length > MaximumSampleBytes) throw Error("source_sample_too_large");
        var parents = new List<SourceSample>();
        foreach (var id in inputSamples.Values) parents.Add(await GetAsync(id, ct));
        if (parents.Any(p => p.IsRawOperationResponse || p.InputSamples is not null || p.State != SourceSampleState.Ready)) throw Error("source_sample_consumed_unavailable");
        var first = parents[0];
        var sample = first with
        {
            Id = Guid.NewGuid().ToString("N"),
            Value = value.Clone(),
            ConsumedInput = null,
            PreparedInput = null,
            ContentContract = null,
            InputSamples = inputSamples.ToDictionary(),
            ContextEvidence = evidence.Clone(),
            Contract = ManifestContract("knowledge.postAggregate"),
            Provenance = draftFingerprint,
            CreatedAt = clock.GetUtcNow(),
            ExpiresAt = parents.Min(p => p.ExpiresAt)
        };
        store.Put(sample);
        return sample;
    }

    public static Agentstration.Schemas.Contracts.SchemaBindingSnapshot ManifestContract(string origin)
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { artifacts = new { type = "array" } }, required = new[] { "artifacts" } });
        return new(origin, SchemaFormatIds.JsonSchema, null, schema, JsonSchemaSelection.DefinitionHash(SchemaFormatIds.JsonSchema, null, schema));
    }

    private async Task<DataSourceAcquisitionResource> ConsumedAcquisitionAsync(ResourceReference reference, ResourceNamespace ns, string id, CancellationToken ct)
    {
        var context = execution.RequireContext("source_sample_context_required");
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, ct);
        var source = await sources.ResolveForUseAsync(reference.Namespace ?? ResourceNamespace.Default, reference.Name, reference.ScopeRef, execution.ConsumerScope, ct);
        var stored = await execution.InWorkspaceAsync(() => resources.GetExactAsync<DataSourceAcquisitionResource>(new(ResourceScopeRef.Workspace(context.WorkspaceId), ns, DataSourceResourceKinds.DataSourceAcquisition, id), ct));
        if (stored is null || !Compatible(stored.Value, source.Value, context)) throw Error("source_sample_consumed_unavailable");
        _ = await datasets.ReadAsync(stored.Value, 0, 1, ct);
        return stored.Value;
    }
    private static bool Compatible(DataSourceAcquisitionResource acquisition, DataSourceResource source, RequestContext context) =>
        source.Definition.Enabled && acquisition.Dataset is not null && acquisition.State == DataSourceAcquisitionState.Succeeded
        && acquisition.Content is not null && acquisition.ContentRetiredAt is null && acquisition.DataSourceUid == source.Uid
        && acquisition.DataSourceGeneration == source.Generation && acquisition.DataSourceScopeRef == source.ScopeRef
        && acquisition.TenantId == context.TenantId && acquisition.WorkspaceId == context.WorkspaceId
        && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(acquisition.Dataset.Definition), JsonSerializer.SerializeToElement(source.Definition.Dataset));

    private async Task<SourceSample> GetOwnedAsync(string id, CancellationToken ct)
    {
        var context = execution.RequireContext("source_sample_context_required");
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, ct);
        var sample = store.Get(context.WorkspaceId, id);
        if (sample is null || sample.ExpiresAt <= clock.GetUtcNow() || sample.PrincipalId != context.PrincipalId
            || sample.TenantId != context.TenantId || sample.Consumer != execution.ConsumerScope) throw Error("source_sample_unavailable");
        return sample;
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var sample = await GetOwnedAsync(id, ct);
        if (active.TryGetValue(id, out var running)) await running.CancelAsync();
        store.Delete(sample.WorkspaceId, id);
    }

    public async Task CancelAsync(string id, CancellationToken ct)
    {
        var sample = await GetOwnedAsync(id, ct);
        store.Put(sample with { State = SourceSampleState.Cancelled, Value = null });
        if (active.TryGetValue(id, out var running)) await running.CancelAsync();
    }

    public async Task<SourceSequencePreview> ReplayAsync(string id, ReplaySourceSampleRequest request, CancellationToken ct)
    {
        var sample = await GetAsync(id, ct);
        if (sample.State != SourceSampleState.Ready || sample.Value is not { } value) throw Error("source_sample_not_conformant");
        return await sequences.ReplayAsync(new(sample.Consumer, request.Namespace, request.Calls, sample.Contract,
            sample.IsRawOperationResponse, sample.ContentContract), value, id, ct);
    }

    // Called only by the bounded hosted worker; observation never enters this method.
    public async Task ProcessNextAsync(CancellationToken waitingToken, CancellationToken? executionToken = null)
    {
        var queued = await queue.Reader.ReadAsync(waitingToken);
        var stoppingToken = executionToken ?? waitingToken;
        var context = new RequestContext(queued.PrincipalId, queued.TenantId, queued.WorkspaceId, queued.Restriction);
        using var owner = queued.Consumer.Kind switch
        {
            ResourceScopeKind.Instance => scopes.PushInstance(context),
            ResourceScopeKind.Tenant => scopes.PushTenantAdministration(context),
            _ => scopes.Push(context)
        };
        var sample = store.Get(queued.WorkspaceId, queued.Id);
        if (sample is null || sample.State != SourceSampleState.Pending) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        active[sample.Id] = deadline;
        try
        {
            await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, deadline.Token);
            var (operation, type) = await ValidateOperationAsync(sample.Acquisition, sample.Consumer, deadline.Token);
            store.Put(sample with { State = SourceSampleState.Running });
            var value = await executor.ExecuteAsync(sample.Acquisition, sample.Consumer, sample.Id, deadline.Token);
            if (JsonSerializer.SerializeToUtf8Bytes(value).Length > MaximumSampleBytes) throw Error("source_sample_too_large");
            var declared = operation.Definition.Definition.GetProperty(type.Acquisition!.OutputSchemaProperty);
            var validTransport = true;
            try
            {
                DataSourceDatasetCompositionService.ValidateValue(declared, value, MaximumSampleBytes);
                if (sample.Acquisition.OutputSchemaSnapshot is { } raw)
                    DataContracts.Validate(DataContracts.Declared(JsonSchemaSelection.Select(raw.Content), raw.Path), value, "sample-operation");
            }
            catch (DataSourceValidationException) { validTransport = false; }
            catch (DataContractException) { validTransport = false; }
            var content = await OperationContentValidation.ValidateAsync(operation.Definition.AdoptedContentContract, value, formats, deadline.Token);
            sample = sample with
            {
                ContentContract = operation.Definition.AdoptedContentContract?.Schema,
                State = validTransport && content.IsValid ? SourceSampleState.Ready : SourceSampleState.Invalid,
                Value = value.Clone(),
                Contract = sample.Acquisition.OutputSchemaSnapshot ?? new("operation:" + operation.Name, Agentstration.Schemas.SchemaFormatIds.JsonSchema,
                    BuiltinJsonSchemaFormatHandler.Draft202012, declared.Clone(),
                    JsonSchemaSelection.DefinitionHash(Agentstration.Schemas.SchemaFormatIds.JsonSchema, BuiltinJsonSchemaFormatHandler.Draft202012, declared)),
                ErrorCode = !validTransport ? "source_sample_transport_invalid" : content.Issues.FirstOrDefault()?.Code
            };
        }
        catch (OperationCanceledException) { sample = sample with { State = stoppingToken.IsCancellationRequested ? SourceSampleState.Cancelled : SourceSampleState.Failed, ErrorCode = "source_sample_timeout" }; }
        catch (ConnectedServiceOperationDiscoveryException error) { sample = sample with { State = SourceSampleState.Failed, ErrorCode = error.Code }; }
        catch (DataSourceAcquisitionException error) { sample = sample with { State = SourceSampleState.Failed, ErrorCode = error.Code }; }
        catch (Exception error) when (error is not OperationCanceledException) { sample = sample with { State = SourceSampleState.Failed, ErrorCode = "source_sample_failed" }; }
        active.TryRemove(sample.Id, out _);
        if (store.Get(queued.WorkspaceId, queued.Id) is { State: not SourceSampleState.Cancelled }) store.Put(sample);
    }

    private async Task<(ConnectedServiceOperationResource, ConnectedServiceOperationTypeVersionDescriptor)> ValidateOperationAsync(
        DataSourceOperationAcquisitionBinding binding, ResourceScopeRef consumer, CancellationToken ct)
    {
        var (operation, type) = await operations.ResolveAsync(binding.Operation, consumer, true, ct);
        if (type.Acquisition is not { } adapter || type.Interaction != ConnectedServiceOperationInteraction.RequestResponse
            || !operation.Definition.Definition.TryGetProperty(adapter.ReadOnlyProperty, out var readOnly) || readOnly.ValueKind != JsonValueKind.True)
            throw Error("source_sample_operation_not_read_only");
        DataSourceDatasetCompositionService.ValidateValue(operation.Definition.Definition.GetProperty(adapter.InputSchemaProperty), binding.Parameters, MaximumSampleBytes);
        return (operation, type);
    }
    private static DataSourceAcquisitionException Error(string code) => new(code, "The authorized authoring sample is unavailable or exceeds its bounds.");
}
