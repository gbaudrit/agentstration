using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Agentstration.Schemas;

/// <summary>Bounded XML structure and schema metadata. Never contains instance values or parser messages.</summary>
public sealed record XmlContractDiagnostic(string Kind, string? NodePath = null, int? Line = null, int? Column = null,
    string? ExpectedType = null, string? ExpectedStructure = null, SchemaContractEvidence? Schema = null)
{
    [JsonIgnore]
    public bool IsValid => Kind is "syntax" or "contract" or "definition"
        && Text(NodePath, 512) && Text(ExpectedType, 128) && Text(ExpectedStructure, 512)
        && Line is null or > 0 && Column is null or > 0
        && (Schema is null || Schema.Name is { Length: > 0 and <= 128 } && Schema.Version is { Length: > 0 and <= 64 }
            && Schema.SchemaGeneration > 0 && Schema.SchemaUid != Guid.Empty
            && Text(Schema.ParentDefinitionHash, 128) && Text(Schema.DefinitionHash, 128) && Text(Schema.SchemaPath, 512));
    private static bool Text(string? text, int maximum) => text is null || text.Length <= maximum && !text.Any(char.IsControl);
}

internal static class XmlContractDiagnostics
{
    public static XmlContractDiagnostic Describe(XObject? node, XmlSchemaSet schemas, bool instance, XmlSchemaException? error = null)
    {
        var element = node as XElement ?? (node as XAttribute)?.Parent;
        var info = node switch { XElement e => e.GetSchemaInfo(), XAttribute a => a.GetSchemaInfo(), _ => null };
        var type = info?.SchemaType;
        var container = node is XAttribute ? element : element?.Parent;
        var containerType = container?.GetSchemaInfo()?.SchemaType as XmlSchemaComplexType;
        if (type is null) type = containerType;
        if (type is null && element?.Document?.Root is { } root)
            type = (schemas.GlobalElements[new XmlQualifiedName(root.Name.LocalName, root.Name.NamespaceName)] as XmlSchemaElement)?.ElementSchemaType;
        var position = node as IXmlLineInfo;
        var line = position?.HasLineInfo() == true ? position.LineNumber : error?.LineNumber;
        var column = position?.HasLineInfo() == true ? position.LinePosition : error?.LinePosition;
        var structure = type is XmlSchemaComplexType complex ? Particle(complex.ContentTypeParticle, 0)
            : type is XmlSchemaSimpleType { Content: XmlSchemaSimpleTypeRestriction restriction }
                ? string.Join(", ", restriction.Facets.OfType<XmlSchemaFacet>().Where(facet => facet is XmlSchemaLengthFacet
                    or XmlSchemaMinLengthFacet or XmlSchemaMaxLengthFacet or XmlSchemaMinInclusiveFacet or XmlSchemaMaxInclusiveFacet)
                    .Take(4).Select(facet => facet.GetType().Name.Replace("XmlSchema", "", StringComparison.Ordinal).Replace("Facet", "", StringComparison.Ordinal)
                        + "=" + Cut(facet.Value, 48))) : null;
        if (type is XmlSchemaComplexType attributed)
        {
            var attributes = attributed.AttributeUses.Values.OfType<XmlSchemaAttribute>().Where(attribute => attribute.Use == XmlSchemaUse.Required)
                .Take(4).Select(attribute => "@" + QName(attribute.QualifiedName)).ToArray();
            if (attributes.Length > 0) structure += "; required(" + string.Join(", ", attributes) + ")";
        }
        if (containerType is not null && !ReferenceEquals(containerType, type))
            structure = (string.IsNullOrEmpty(structure) ? "" : structure + "; ") + "parent: " + Particle(containerType.ContentTypeParticle, 0);
        if (string.IsNullOrEmpty(structure) && type is null)
            structure = string.Join(", ", schemas.GlobalElements.Values.OfType<XmlSchemaElement>().Take(6).Select(e => QName(e.QualifiedName)));
        return new(instance ? "contract" : "definition", Path(node), Positive(line), Positive(column),
            type?.QualifiedName.IsEmpty == false ? Cut(QName(type.QualifiedName), 128) : null, Cut(structure, 512));
    }

    public static XmlContractDiagnostic Syntax(XmlException error, bool instance) =>
        new(instance ? "syntax" : "definition", Line: Positive(error.LineNumber), Column: Positive(error.LinePosition));

    private static int? Positive(int? value) => value is > 0 ? value : null;
    private static string? Cut(string? value, int length) => value is null ? null : value[..Math.Min(length, value.Length)];
    private static string QName(XmlQualifiedName name) => name.Namespace == BuiltinXmlSchemaFormatHandler.Dialect ? "xs:" + name.Name
        : name.IsEmpty ? "" : name.Namespace.Length == 0 ? name.Name : "{" + name.Namespace + "}" + name.Name;
    private static string? Path(XObject? node)
    {
        var element = node as XElement ?? (node as XAttribute)?.Parent;
        if (element is null) return null;
        var elements = element.AncestorsAndSelf().Reverse().Take(33);
        var path = string.Concat(elements.Select(e => "/" + Cut(e.Name.LocalName, 64) + "["
            + (e.ElementsBeforeSelf(e.Name).Count() + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "]"));
        if (node is XAttribute attribute) path += "/@" + Cut(attribute.Name.LocalName, 64);
        return Cut(path, 512);
    }
    private static string Particle(XmlSchemaParticle particle, int depth)
    {
        if (depth >= 3) return "…";
        var body = particle switch
        {
            XmlSchemaElement element => QName(element.QualifiedName.IsEmpty ? element.RefName : element.QualifiedName),
            XmlSchemaAny => "*",
            XmlSchemaGroupBase group => (group is XmlSchemaChoice ? "choice" : group is XmlSchemaAll ? "all" : "sequence")
                + "(" + string.Join(", ", group.Items.OfType<XmlSchemaParticle>().Take(6).Select(child => Particle(child, depth + 1))) + ")",
            _ => ""
        };
        return particle.MinOccurs == 1 && particle.MaxOccurs == 1 ? body : body + "["
            + particle.MinOccurs.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".."
            + (particle.MaxOccursString == "unbounded" ? "*" : particle.MaxOccurs.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "]";
    }
}
