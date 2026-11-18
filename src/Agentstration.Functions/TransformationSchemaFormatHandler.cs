using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Functions.Abstractions;
using Agentstration.Functions.Contracts;
using Agentstration.Schemas;

namespace Agentstration.Functions;

public sealed class TransformationSchemaFormatHandler : ISchemaFormatHandler
{
    public const string Format = "xml-json-transformation";
    public const string Dialect = "agentstration.io/xml-json-transformation/v1";
    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public SchemaFormatDescriptor Descriptor { get; } = new(Format, "XML → JSON transformation",
        new("builtin/xml-json-transformation", "1.0"), [Dialect], SchemaFormatCapabilities.ValidateDefinition, true);

    public ValueTask<SchemaFormatValidationResult> ValidateDefinitionAsync(JsonElement definition, string? dialect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (dialect is not null && dialect != Dialect) throw new JsonException();
            _ = Parse(definition);
            return ValueTask.FromResult(SchemaFormatValidationResult.Valid);
        }
        catch (Exception error) when (error is JsonException or TransformationFunctionException or InvalidOperationException or ArgumentException)
        { return ValueTask.FromResult(new SchemaFormatValidationResult([new("$", "transformation_definition_invalid", "An exact engine, input/output Schema versions and valid bounded XML mapping rules are required.")])); }
    }

    internal static TransformationSchemaDeclaration Parse(JsonElement content)
    {
        var value = content.Deserialize<TransformationSchemaDeclaration>(Json) ?? throw new JsonException();
        if (value.Engine is null || string.IsNullOrWhiteSpace(value.Engine.Name) || string.IsNullOrWhiteSpace(value.Engine.Version)
            || value.Input is null || value.Output is null || string.IsNullOrWhiteSpace(value.Input.Name) || string.IsNullOrWhiteSpace(value.Input.Version)
            || string.IsNullOrWhiteSpace(value.Output.Name) || string.IsNullOrWhiteSpace(value.Output.Version)
            || !string.IsNullOrEmpty(value.Input.SchemaPath)) throw new JsonException();
        XmlToJsonFunction.ValidateRules(value.Rules);
        return value;
    }

    public ValueTask<SchemaFormatValidationResult> ValidateInstanceAsync(JsonElement definition, JsonElement instance, string? dialect, CancellationToken cancellationToken) =>
        throw new SchemaFormatResolutionException("schema_format_capability_unavailable", "A transformation declaration is exposed explicitly as a function.");
}
