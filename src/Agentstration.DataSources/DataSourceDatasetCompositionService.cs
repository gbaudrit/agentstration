using System.Text.Json;
using Agentstration.Flows;
using Agentstration.ConnectedServices;
using Agentstration.DataSources.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;

namespace Agentstration.DataSources;

public sealed class DataSourceDatasetCompositionService(
    ConnectedServiceOperationService operations,
    IStorageResolver storage,
    IStorageContentGateway content,
    IDataSourceAcquisitionCompositionResolver flows, IFlowCompositionResolver? sequences = null,
    DataSourceDocumentStorage? documentStorage = null, ISchemaBindingResolver? schemaBindings = null)
{
    public const string FlowContract = "connected-service-operation-acquisition/v1";
    internal const int MaximumValueBytes = 64 * 1024;

    public async Task<ResolvedDataSourceDataset> ResolveAsync(DataSourceResource source,
        ResourceScopeRef consumer, CancellationToken cancellationToken, ResolvedDataSourceDataset? accepted = null)
    {
        var definition = source.Definition.Dataset
            ?? throw Invalid("data_source_dataset_required", "A dataset composition is required.");
        ValidateLifecycle(definition);
        var (operation, type) = await operations.ResolveAsync(definition.Acquisition.Operation, consumer, true, cancellationToken);
        var adapter = type.Acquisition
            ?? throw Invalid("data_source_operation_adapter_unavailable", "This operation type has no acquisition adapter.");
        if (operation.Definition.Definition.ValueKind != JsonValueKind.Object
            || !operation.Definition.Definition.TryGetProperty(adapter.ReadOnlyProperty, out var readOnly)
            || readOnly.ValueKind != JsonValueKind.True)
            throw Invalid("data_source_operation_not_read_only", "Actions are not eligible for acquisition.");
        var input = Schema(operation.Definition.Definition, adapter.InputSchemaProperty);
        var declaredOutput = Schema(operation.Definition.Definition, adapter.OutputSchemaProperty);
        var operationOutput = definition.Acquisition.OutputSchemaSnapshot?.Content ?? declaredOutput;
        var rawOutput = JsonSchemaSelection.Select(operationOutput);
        ValidateSchema(rawOutput);
        var finalSnapshot = definition.OutputSchemaSnapshot;
        var output = finalSnapshot is null ? rawOutput : JsonSchemaSelection.Select(finalSnapshot.Content);
        ValidateSchema(output);
        ValidateValue(input, definition.Acquisition.Parameters);
        var partition = Partition(source);
        if (definition.Storage is { } inputStorage)
        {
            var consumerContext = new StorageResolutionContext(consumer, source.Address);
            var target = await storage.ResolveAsync(inputStorage, consumerContext, cancellationToken);
            if (definition.DocumentCollectionUid is not null)
            {
                if (documentStorage is null) throw Invalid("data_source_document_storage_unavailable", "Structured acquisition storage is unavailable.");
                await documentStorage.ValidateAsync(inputStorage, consumerContext, definition, cancellationToken);
            }
            else
            {
                if (!target.Capabilities.List || !target.Capabilities.Read || !target.Capabilities.Write
                    || !definition.Rule.List || !definition.Rule.Read || !definition.Rule.Write)
                    throw Invalid("data_source_storage_capability_required", "Acquisition requires list, read and write capabilities.");
                var effective = definition.Rule with { Prefix = partition };
                await content.ValidateAsync(new(inputStorage, consumerContext, effective), cancellationToken);
            }
        }
        var flow = await flows.ResolveOperationAsync(consumer, adapter.Flow, cancellationToken);
        if (definition.Orchestration is null && definition.Transformations.Count == 0 && definition.Compositions.Count == 0 && accepted?.Composition is null)
            return new(definition, operation, type, input, output, partition, flow)
            { OperationOutputSchema = rawOutput, OutputSchemaSnapshot = finalSnapshot ?? definition.Acquisition.OutputSchemaSnapshot };
        if (sequences is null || definition.Compositions.ContainsKey("acquisition"))
            throw Invalid("data_source_composition_invalid", "The acquisition point is owned by the Data Source.");
        var bindings = definition.Compositions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (definition.Transformations.Count > 0)
        {
            if (bindings.ContainsKey("transformations")) throw Invalid("data_source_composition_ambiguous", "Configure transformations once.");
            bindings.Add("transformations", definition.Transformations);
        }
        bindings.Add("acquisition", [new() { Flow = new(flow.Name, FlowCallVersionStrategy.Exact, flow.Version, flow.Namespace, flow.ScopeRef)
            { DefinitionHash = flow.DefinitionHash }, PassInputDirectly = true }]);
        var root = definition.Orchestration ?? new FlowCallReference("data-source-orchestration-builtin", ScopeRef: ResourceScopeRef.Instance);
        if (accepted?.Composition is { } pinned)
        { root = pinned.Orchestration; bindings = pinned.Receipt!.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal); }
        try
        {
            var composed = await sequences.ResolveAsync(consumer, source.Namespace, root, bindings, cancellationToken);
            var targetFlow = new ResolvedDataSourceFlowBinding(composed.Orchestration.ResourceId,
                composed.Orchestration.Namespace ?? source.Namespace, composed.Orchestration.Version!, false,
                composed.InputSchema, composed.OutputSchema, "datasource.orchestration/v1", composed.Orchestration.DefinitionHash ?? throw Invalid("data_source_composition_invalid", "The orchestration has no immutable definition hash."))
            { ScopeRef = composed.Orchestration.ScopeRef };
            return new(definition, operation, type, input, finalSnapshot is not null ? output : composed.OutputSchema ?? output, partition, targetFlow)
            { OperationFlow = flow, Composition = composed, OperationOutputSchema = rawOutput,
                OutputSchemaSnapshot = finalSnapshot ?? (composed.OutputSchema is null ? definition.Acquisition.OutputSchemaSnapshot : null) };
        }
        catch (Exception exception) when (exception is Agentstration.Flows.FlowValidationException or KeyNotFoundException)
        { throw Invalid("data_source_composition_invalid", "The ordered Flow composition is unavailable or incompatible."); }
    }

    public async Task<DataSourceResource> AdoptSchemasAsync(DataSourceResource resource, CancellationToken token, DataSourceResource? previous = null)
    {
        if (resource.Definition.Dataset is not { } dataset) return resource;
        var prior = previous?.Definition.Dataset;
        var raw = await ResolveBindingAsync(dataset.Acquisition.OutputSchemaBinding, "$.dataset.acquisition.outputSchema", resource,
            prior?.Acquisition.OutputSchemaBinding, prior?.Acquisition.OutputSchemaSnapshot, token);
        var final = await ResolveBindingAsync(dataset.OutputSchemaBinding, "$.dataset.outputSchema", resource,
            prior?.OutputSchemaBinding, prior?.OutputSchemaSnapshot, token);
        return resource with { Definition = resource.Definition with { Dataset = dataset with
        {
            Acquisition = dataset.Acquisition with { OutputSchemaSnapshot = raw }, OutputSchemaSnapshot = final
        } } };
    }

    private async Task<SchemaBindingSnapshot?> ResolveBindingAsync(SchemaBinding? binding, string path,
        DataSourceResource source, SchemaBinding? previousBinding, SchemaBindingSnapshot? previousSnapshot, CancellationToken token)
    {
        if (binding is null) return null;
        if (previousSnapshot is not null && previousBinding is not null
            && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(binding), JsonSerializer.SerializeToElement(previousBinding)))
            return previousSnapshot;
        if (schemaBindings is null) throw Invalid("schema_resolver_unavailable", "Schema binding resolution is unavailable.");
        try
        {
            var snapshot = await schemaBindings.ResolveAsync(path, binding,
                new(source.ScopeRef!.Value, source.Namespace, SchemaFormatIds.JsonSchema), token);
            if (snapshot.Dialect is { } dialect && dialect != BuiltinJsonSchemaFormatHandler.Draft202012)
                throw Invalid("schema_dialect_incompatible", "The acquisition validator does not support the selected dialect.");
            ValidateSchema(JsonSchemaSelection.Select(snapshot.Content));
            _ = DataContracts.Declared(JsonSchemaSelection.Select(snapshot.Content), path, "application/json");
            return snapshot;
        }
        catch (SchemaBindingResolutionException exception) { throw Invalid(exception.Code, exception.Message); }
        catch (SchemaSelectionException exception) { throw Invalid(exception.Code, "The selected schema is unsupported."); }
    }

    public static void ValidateOperationOutput(ResolvedDataSourceDataset dataset, JsonElement result, string? runId = null)
    {
        var schema = dataset.OperationOutputSchema ?? dataset.OutputSchema;
        if (dataset.Definition.Acquisition.OutputSchemaSnapshot is { } snapshot)
            DataContracts.Validate(DataContracts.Declared(schema, snapshot.Path, "application/json"), result,
                "acquisition-operation", runId);
        else ValidateValue(schema, result);
    }

    public static ResolvedDataContract Contract(ResolvedDataSourceDataset dataset, string origin)
    {
        var contract = DataContracts.Declared(dataset.OutputSchema, origin, "application/json")!;
        var snapshot = dataset.OutputSchemaSnapshot;
        if (snapshot?.Source is not { ScopeRef: { } scope, Namespace: { } ns } source
            || snapshot.SchemaUid is not { } uid || snapshot.SchemaGeneration is not { } generation) return contract;
        return contract with { SchemaEvidence = new(source.Name, source.Version, scope, ns, uid, generation,
            snapshot.ParentDefinitionHash ?? snapshot.DefinitionHash, source.SchemaPath, snapshot.DefinitionHash) };
    }

    public static void ValidateLifecycle(DataSourceDatasetProperties definition)
    {
        if (definition.DocumentCollectionUid is { } collection && (collection == Guid.Empty || definition.Storage is null))
            throw Invalid("data_source_document_collection_required", "Structured acquisition requires an exact Storage and collection.");
        var policy = definition.Lifecycle;
        if (definition.Storage is null && policy?.AllowDirect != true)
            throw Invalid("data_source_direct_permission_required", "Direct acquisition requires explicit permission.");
        if (policy is null) return;
        if (policy.TimeToLive is { } ttl && (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromDays(3650))
            || policy.HistoryRetention is { } retention && (retention <= TimeSpan.Zero || retention > TimeSpan.FromDays(3650))
            || policy.KeepHistory && policy.HistoryRetention is null || !policy.KeepHistory && policy.HistoryRetention is not null)
            throw Invalid("data_source_lifecycle_invalid", "Lifecycle durations must be positive, bounded, and consistent with the history setting.");
        if (definition.Storage is null && (policy.TimeToLive is not null || policy.KeepHistory))
            throw Invalid("data_source_retained_storage_required", "Acquisition freshness and history require an exact-readable input Storage.");
    }

    public static string Partition(DataSourceResource source)
    {
        var prefix = source.Definition.Dataset!.Rule.Prefix;
        if (source.Uid == Guid.Empty || prefix is null || prefix.Length > 256 || prefix.Contains("..", StringComparison.Ordinal)
            || prefix.Contains('\\') || prefix.StartsWith('/') || prefix.Any(char.IsControl)
            || prefix.Split('/').Contains("datasets", StringComparer.Ordinal)
            || prefix.Length > 0 && !prefix.EndsWith('/'))
            throw Invalid("data_source_rule_invalid", "The dataset prefix must be a bounded relative directory prefix without the reserved datasets segment.");
        return $"{prefix}datasets/{source.Uid:N}/";
    }

    public static StorageContentRequest Target(DataSourceAcquisitionResource acquisition, ResourceScopeRef consumer)
    {
        var dataset = acquisition.Dataset!;
        return new(dataset.Definition.Storage ?? throw Invalid("data_source_content_unavailable", "This acquisition has no retained input Storage."),
            new(consumer, ResourceAddress.Create(acquisition.DataSourceNamespace, DataSourceResourceKinds.DataSource, acquisition.DataSourceName)),
            dataset.Definition.Rule with { Prefix = dataset.Partition })
        { DocumentCollectionUid = dataset.Definition.DocumentCollectionUid };
    }

    public static void ValidateValue(JsonElement schema, JsonElement value)
    {
        ValidateSchema(schema);
        if (value.ValueKind == JsonValueKind.Undefined || JsonSerializer.SerializeToUtf8Bytes(value).Length > MaximumValueBytes)
            throw Invalid("data_source_parameters_invalid", "Operation values must be bounded JSON.");
        try { ToolInputSchemaValidator.Validate(schema, value); }
        catch (ToolInputValidationException)
        { throw Invalid("data_source_parameters_invalid", "The value does not satisfy the effective operation schema."); }
    }

    private static JsonElement Schema(JsonElement definition, string property)
    {
        if (!definition.TryGetProperty(property, out var schema) || schema.ValueKind != JsonValueKind.Object)
            throw Invalid("data_source_operation_schema_missing", "The operation must declare its input and output schemas.");
        ValidateSchema(schema);
        return schema.Clone();
    }

    // Reuse the governed Tool validator's bounded schema profile. Unsupported validation keywords fail closed.
    private static void ValidateSchema(JsonElement schema, int depth = 0)
    {
        if (depth > 12 || schema.ValueKind != JsonValueKind.Object)
            throw Invalid("data_source_schema_unsupported", "The operation schema exceeds the supported profile.");
        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name is "properties")
            {
                if (property.Value.ValueKind != JsonValueKind.Object) throw Invalid("data_source_schema_unsupported", "Invalid properties schema.");
                foreach (var child in property.Value.EnumerateObject()) ValidateSchema(child.Value, depth + 1);
            }
            else if (property.Name is "items") ValidateSchema(property.Value, depth + 1);
            else if (property.Name is not ("type" or "required" or "additionalProperties" or "enum" or "minItems" or "maxItems"
                or "minLength" or "maxLength" or "pattern" or "format" or "minimum" or "maximum"
                or "title" or "description" or "default" or "examples" or "$schema"))
                throw Invalid("data_source_schema_unsupported", "The operation schema contains unsupported validation keywords.");
            else if (property.Name == "additionalProperties" && property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Invalid("data_source_schema_unsupported", "Additional properties must be explicitly allowed or refused.");
        }
        if (!schema.TryGetProperty("type", out _))
            throw Invalid("data_source_schema_unsupported", "Every schema node requires an explicit type.");
    }

    private static DataSourceValidationException Invalid(string code, string message) => new(code, message);
}
