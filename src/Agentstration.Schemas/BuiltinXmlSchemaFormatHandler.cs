using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Agentstration.Schemas;

/// <summary>One local XSD 1.0 document. Neither schemas nor instances can retrieve resources.</summary>
public sealed class BuiltinXmlSchemaFormatHandler : ISchemaFormatHandler
{
    public const string Format = "xml-xsd";
    public const string Dialect = "http://www.w3.org/2001/XMLSchema";
    public SchemaFormatDescriptor Descriptor { get; } = new(Format, "XML Schema (XSD 1.0)",
        new("builtin/xml-xsd", "1.0"), [Dialect],
        SchemaFormatCapabilities.ValidateDefinition | SchemaFormatCapabilities.ValidateInstance, true);

    public ValueTask<SchemaFormatValidationResult> ValidateDefinitionAsync(JsonElement definition, string? dialect, CancellationToken cancellationToken) =>
        Validate(definition, null, dialect, cancellationToken);

    public ValueTask<SchemaFormatValidationResult> ValidateInstanceAsync(JsonElement definition, JsonElement instance, string? dialect, CancellationToken cancellationToken) =>
        Validate(definition, instance, dialect, cancellationToken);

    private static ValueTask<SchemaFormatValidationResult> Validate(JsonElement definition, JsonElement? instance, string? dialect, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (dialect is not null && dialect != Dialect) throw new XmlException("Unsupported XSD dialect.");
            var document = BoundedXml.Parse(Text(definition), ct);
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
                var content = BoundedXml.Parse(Text(value), ct);
                content.Validate(schemas, Invalid, false);
            }
            return ValueTask.FromResult(new SchemaFormatValidationResult(issues));
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or ArgumentException)
        {
            return ValueTask.FromResult(new SchemaFormatValidationResult([new("$", "xml_invalid", "Invalid or unsupported bounded XML/XSD document.")]));
        }
    }

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()!
        : throw new XmlException("XML native content must be a string.");
}

public static class BoundedXml
{
    public const int MaximumBytes = 65_536;
    public static XDocument Parse(string value, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaximumBytes) throw new XmlException("XML size limit exceeded.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes,
            MaxCharactersFromEntities = 1024
        };
        using var reader = XmlReader.Create(new StringReader(value), settings);
        var nodes = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.Depth > 32 || ++nodes > 8192 || reader.AttributeCount > 64)
                throw new XmlException("XML structural limit exceeded.");
        }
        using var content = XmlReader.Create(new StringReader(value), settings);
        return XDocument.Load(content, LoadOptions.PreserveWhitespace);
    }
}
