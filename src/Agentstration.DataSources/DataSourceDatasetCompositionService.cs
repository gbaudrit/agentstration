using System.Text.Json;
using Agentstration.ConnectedServices;
using Agentstration.DataSources.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;

namespace Agentstration.DataSources;

public sealed class DataSourceDatasetCompositionService(
    ConnectedServiceOperationService operations,
    IStorageResolver storage,
    IStorageContentGateway content,
    IDataSourceAcquisitionCompositionResolver flows)
{
    public const string FlowContract = "connected-service-operation-acquisition/v1";
    internal const int MaximumValueBytes = 64 * 1024;

    public async Task<ResolvedDataSourceDataset> ResolveAsync(DataSourceResource source,
        ResourceScopeRef consumer, CancellationToken cancellationToken)
    {
        var definition = source.Definition.Dataset
            ?? throw Invalid("data_source_dataset_required", "A dataset composition is required.");
        var (operation, type) = await operations.ResolveAsync(definition.Acquisition.Operation, consumer, true, cancellationToken);
        var adapter = type.Acquisition
            ?? throw Invalid("data_source_operation_adapter_unavailable", "This operation type has no acquisition adapter.");
        if (operation.Definition.Definition.ValueKind != JsonValueKind.Object
            || !operation.Definition.Definition.TryGetProperty(adapter.ReadOnlyProperty, out var readOnly)
            || readOnly.ValueKind != JsonValueKind.True)
            throw Invalid("data_source_operation_not_read_only", "Actions are not eligible for acquisition.");
        var input = Schema(operation.Definition.Definition, adapter.InputSchemaProperty);
        var output = Schema(operation.Definition.Definition, adapter.OutputSchemaProperty);
        ValidateValue(input, definition.Acquisition.Parameters);
        var consumerContext = new StorageResolutionContext(consumer, source.Address);
        var target = await storage.ResolveAsync(definition.Storage, consumerContext, cancellationToken);
        if (!target.Capabilities.List || !target.Capabilities.Read || !target.Capabilities.Write
            || !definition.Rule.List || !definition.Rule.Read || !definition.Rule.Write)
            throw Invalid("data_source_storage_capability_required", "Acquisition requires list, read and write capabilities.");
        var partition = Partition(source);
        var effective = definition.Rule with { Prefix = partition };
        await content.ValidateAsync(new(definition.Storage, consumerContext, effective), cancellationToken);
        var flow = await flows.ResolveOperationAsync(consumer, adapter.Flow, cancellationToken);
        return new(definition, operation, type, input, output, partition, flow);
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
        return new(dataset.Definition.Storage,
            new(consumer, ResourceAddress.Create(acquisition.DataSourceNamespace, DataSourceResourceKinds.DataSource, acquisition.DataSourceName)),
            dataset.Definition.Rule with { Prefix = dataset.Partition });
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
