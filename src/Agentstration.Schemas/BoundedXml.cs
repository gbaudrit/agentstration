using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Agentstration.Schemas;

public sealed class XmlContentLimitException(string rule, DataContractLimit limit) : XmlException("An XML processing limit was exceeded.")
{
    public string Rule { get; } = rule;
    public DataContractLimit Limit { get; } = limit;
}

public static class BoundedXml
{
    public static XDocument Parse(string value, CancellationToken ct, XmlContentOptions? options = null)
    {
        var limits = options ?? new();
        limits.Validate();
        ct.ThrowIfCancellationRequested();
        Check(Encoding.UTF8.GetByteCount(value), limits.MaximumDocumentBytes, "utf8Bytes", nameof(limits.MaximumDocumentBytes));
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = limits.MaximumDocumentBytes,
            MaxCharactersFromEntities = 1024
        };
        using var reader = XmlReader.Create(new StringReader(value), settings);
        var nodes = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            Check(reader.Depth, limits.MaximumDepth, "xmlDepth", nameof(limits.MaximumDepth));
            Check(++nodes, limits.MaximumNodes, "xmlNodes", nameof(limits.MaximumNodes));
            Check(reader.AttributeCount, limits.MaximumAttributesPerElement, "xmlAttributes", nameof(limits.MaximumAttributesPerElement));
        }
        using var content = XmlReader.Create(new StringReader(value), settings);
        return XDocument.Load(content, LoadOptions.PreserveWhitespace);
    }

    private static void Check(long actual, int maximum, string unit, string setting)
    {
        if (actual > maximum) throw new XmlContentLimitException("xml_" + unit + "_limit",
            new(unit, actual, Maximum: maximum, Setting: XmlContentOptions.SectionName + ":" + setting));
    }
}
