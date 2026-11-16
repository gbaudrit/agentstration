using System.Text.Json;
using Agentstration.Functions.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;

namespace Agentstration.Flows.Application;

/// <summary>Resolves literal schema arguments once; accepted receipts reuse their server-owned snapshots.</summary>
internal sealed class FunctionSchemaBindingSnapshotter(ISchemaBindingResolver resolver)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    internal async Task<IReadOnlyDictionary<string, SchemaBindingSnapshot>?> ResolveAsync(
        JsonElement inputSchema, JsonElement parameters, ResourceScopeRef consumer, ResourceNamespace ns,
        string path, CancellationToken ct, IReadOnlyDictionary<string, SchemaBindingSnapshot>? accepted = null,
        bool reuseAccepted = false)
    {
        var names = FunctionInvocationSchemas.SchemaParameters(inputSchema);
        var snapshots = new Dictionary<string, SchemaBindingSnapshot>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!parameters.TryGetProperty(name, out var value)) continue;
            SchemaBinding binding;
            try { binding = value.Deserialize<SchemaBinding>(Json) ?? throw new JsonException(); }
            catch (JsonException) { throw new FlowValidationException("function_schema_binding_invalid", "A schema parameter requires a literal inline or exact shared Schema binding."); }
            if (reuseAccepted)
            {
                if (accepted?.TryGetValue(name, out var frozen) != true)
                    throw new FlowValidationException("function_schema_snapshot_missing", "The accepted function schema snapshot is missing.");
                snapshots.Add(name, frozen!);
                continue;
            }
            try
            {
                var snapshot = await resolver.ResolveAsync(path + "." + name, binding, new(consumer, ns, SchemaFormatIds.JsonSchema), ct);
                if (snapshot.Dialect is { } dialect && dialect != BuiltinJsonSchemaFormatHandler.Draft202012)
                    throw new FlowValidationException("schema_dialect_incompatible", "The function validator does not support the selected dialect.");
                _ = DataContracts.Declared(JsonSchemaSelection.Select(snapshot.Content), path + "." + name, "application/json");
                snapshots.Add(name, snapshot);
            }
            catch (SchemaBindingResolutionException error) { throw new FlowValidationException(error.Code, error.Message); }
            catch (SchemaSelectionException error) { throw new FlowValidationException(error.Code, "The function schema cannot be selected."); }
            catch (DataContractException) { throw new FlowValidationException("function_schema_unsupported", "The selected schema is unsupported by the function validator."); }
        }
        if (reuseAccepted && accepted is not null && accepted.Keys.Any(name => !snapshots.ContainsKey(name)))
            throw new FlowValidationException("function_schema_snapshot_invalid", "The accepted function schema arguments do not match their snapshots.");
        return snapshots.Count == 0 ? null : snapshots;
    }
}
