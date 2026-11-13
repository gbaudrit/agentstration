using Agentstration.Schemas;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentstration.Flows.Application;

/// <summary>Conservative compatibility for the bounded JSON contracts used by sequential composition.</summary>
public static class FlowCompositionSchema
{
    public const int MaximumParametersLength = 65_536;
    public const int MaximumValueLength = 1_048_576;

    public static bool IsAssignable(JsonElement output, JsonElement input, int depth = 0) => FlowSchemaCompatibility.IsAssignable(output, input, depth);
    public static void ValidateValue(JsonElement schema, JsonElement value, int depth = 0, Func<JsonElement, bool>? isProtectedString = null)
    {
        if (depth > 12 || !IsSupported(schema)) throw Invalid();
        if (value.GetRawText().Length > MaximumValueLength)
            throw new FlowValidationException("flow_composition_value_too_large", "The composition value exceeds its bounded payload budget.")
            { ContractFailure = new("composition-input", "composition", "$", "value_size", "bounded value", "oversized", Code: "flow_composition_value_too_large") };
        try { DataContracts.Validate(DataContracts.Declared(schema, "composition"), value, "composition-input", isProtectedString: isProtectedString); }
        catch (DataContractException exception)
        { throw new FlowValidationException("flow_composition_value_invalid", exception.Message) { ContractFailure = exception.Failure }; }
    }
    public static bool IsSupported(JsonElement schema, int depth = 0) => FlowSchemaCompatibility.IsSupported(schema, depth);

    private static FlowValidationException Invalid() => new("flow_composition_value_invalid", "A composition value does not satisfy its bounded contract.");
}
