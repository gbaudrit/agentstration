using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Agentstration.Schemas;

/// <summary>One local XSD 1.0 document. Neither schemas nor instances can retrieve resources.</summary>
public sealed class BuiltinXmlSchemaFormatHandler(XmlContentOptions? options = null) : ISchemaFormatHandler
{
    private static readonly XmlContentOptions DefinitionLimits = new() { MaximumDocumentBytes = DataContracts.MaximumSchemaCharacters };
    public const string Format = "xml-xsd";
    public const string Dialect = "http://www.w3.org/2001/XMLSchema";
    public SchemaFormatDescriptor Descriptor { get; } = new(Format, "XML Schema (XSD 1.0)",
        new("builtin/xml-xsd", "1.0"), [Dialect],
        SchemaFormatCapabilities.ValidateDefinition | SchemaFormatCapabilities.ValidateInstance, true);

    public ValueTask<SchemaFormatValidationResult> ValidateDefinitionAsync(JsonElement definition, string? dialect, CancellationToken cancellationToken) =>
        Validate(definition, null, dialect, cancellationToken);

    public ValueTask<SchemaFormatValidationResult> ValidateInstanceAsync(JsonElement definition, JsonElement instance, string? dialect, CancellationToken cancellationToken) =>
        Validate(definition, instance, dialect, cancellationToken);

    private ValueTask<SchemaFormatValidationResult> Validate(JsonElement definition, JsonElement? instance, string? dialect, CancellationToken ct)
    {
        var validatingInstance = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (dialect is not null && dialect != Dialect) throw new XmlException("Unsupported XSD dialect.");
            var document = BoundedXml.Parse(Text(definition), ct, DefinitionLimits);
            XNamespace xs = Dialect;
            if (document.Root?.Name != xs + "schema" || document.Descendants().Any(e =>
                e.Name == xs + "import" || e.Name == xs + "include" || e.Name == xs + "redefine"
                || e.Name == xs + "pattern"))
                throw new XmlException("A standalone XSD is required; imports, includes, redefinitions and patterns are unsupported.");
            var issues = new List<SchemaFormatValidationIssue>();
            void Invalid(object? sender, ValidationEventArgs args)
            {
                if (issues.Count < 16) issues.Add(new("$", "xml_contract_invalid", "The XML does not satisfy the standalone XSD contract."));
            }
            var schemas = new XmlSchemaSet { XmlResolver = null };
            schemas.ValidationEventHandler += Invalid;
            using (var reader = document.CreateReader()) schemas.Add(XmlSchema.Read(reader, Invalid)!);
            schemas.Compile();
            if (issues.Count == 0 && instance is { } value)
            {
                validatingInstance = true;
                var content = BoundedXml.Parse(Text(value), ct, options);
                content.Validate(schemas, Invalid, false);
            }
            return ValueTask.FromResult(new SchemaFormatValidationResult(issues));
        }
        catch (XmlContentLimitException error)
        {
            return ValueTask.FromResult(new SchemaFormatValidationResult([new("$", error.Rule, "An XML processing limit was exceeded.") { Limit = validatingInstance ? error.Limit : error.Limit with { Setting = null } }]));
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or ArgumentException)
        {
            return ValueTask.FromResult(new SchemaFormatValidationResult([new("$", "xml_invalid", "Invalid or unsupported bounded XML/XSD document.")]));
        }
    }

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()!
        : throw new XmlException("XML native content must be a string.");
}
