using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Agentstration.Functions.Abstractions;
using Agentstration.Schemas;

namespace Agentstration.Functions;

/// <summary>A deliberately small child/attribute XPath subset; no expression evaluator or external I/O.</summary>
public sealed class XmlToJsonFunction(XmlContentOptions? xmlOptions = null, FunctionContentOptions? functionOptions = null) : INativeTransformationFunction
{
    public NativeFunctionDescriptor Descriptor { get; } = new("xml.to_json", "1.0.0",
        JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"value":{"type":"string"},"rules":{"type":"object"}},"required":["value","rules"],"additionalProperties":false}"""),
        JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}"""));

    public ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            var rules = input.GetProperty("rules");
            ValidateRules(rules);
            var document = BoundedXml.Parse(input.GetProperty("value").GetString()!, cancellationToken, xmlOptions);
            var budget = new MappingBudget(xmlOptions ?? new(), functionOptions ?? new(), cancellationToken);
            var namespaces = Namespaces(rules);
            var output = Map(document, rules.GetProperty("root"), namespaces, budget);
            var result = JsonSerializer.SerializeToElement(output);
            budget.CheckOutput(JsonSerializer.SerializeToUtf8Bytes(result).Length);
            return ValueTask.FromResult(result);
        }
        catch (XmlContentLimitException error)
        {
            throw DataContracts.Failure("xml_input_limit", "function-input", "xml.to_json", "$.value", error.Rule,
                "bounded XML content", "processing limit exceeded", limit: error.Limit);
        }
        catch (Exception error) when (error is XmlException or FormatException or OverflowException)
        { throw Error("xml_value_invalid"); }
    }

    public static void ValidateRules(JsonElement rules)
    {
        if (rules.ValueKind != JsonValueKind.Object || rules.EnumerateObject().Any(p => p.Name is not ("namespaces" or "root"))) throw Error("xml_rules_invalid");
        var namespaces = Namespaces(rules);
        if (!rules.TryGetProperty("root", out var root) || root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object") throw Error("xml_rules_invalid");
        var count = 0;
        ValidateNode(root, namespaces, 0, ref count);
    }

    private static void ValidateNode(JsonElement node, IReadOnlyDictionary<string, string> ns, int depth, ref int count)
    {
        if (++count > 128 || depth > 8 || node.ValueKind != JsonValueKind.Object
            || node.EnumerateObject().Any(p => p.Name is not ("path" or "type" or "optional" or "fields"))
            || !node.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
            || !node.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || node.TryGetProperty("optional", out var optional) && optional.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Error("xml_rules_invalid");
        _ = Segments(path.GetString()!, ns);
        if (type.GetString() is "object" or "array")
        {
            if (!node.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object
                || fields.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.EnumerateObject().Count()) throw Error("xml_rules_invalid");
            foreach (var field in fields.EnumerateObject())
            {
                if (field.Name.Length is < 1 or > 128) throw Error("xml_rules_invalid");
                ValidateNode(field.Value, ns, depth + 1, ref count);
            }
        }
        else if (type.GetString() is not ("string" or "integer" or "number" or "boolean" or "dateTime") || node.TryGetProperty("fields", out _)) throw Error("xml_rules_invalid");
    }

    private static Dictionary<string, string> Namespaces(JsonElement rules)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!rules.TryGetProperty("namespaces", out var value)) return result;
        if (value.ValueKind != JsonValueKind.Object) throw Error("xml_namespace_invalid");
        foreach (var property in value.EnumerateObject())
        {
            if (result.Count >= 16 || property.Name.Length is < 1 or > 128 || property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length is < 1 or > 512
                || property.Name is "xml" or "xmlns" || !result.TryAdd(property.Name, property.Value.GetString()!)) throw Error("xml_namespace_invalid");
            try { XmlConvert.VerifyNCName(property.Name); } catch (XmlException) { throw Error("xml_namespace_invalid"); }
        }
        return result;
    }

    private static string[] Segments(string path, IReadOnlyDictionary<string, string> ns)
    {
        if (path.Length is < 1 or > 512) throw Error("xml_path_invalid");
        if (path == ".") return [];
        var parts = (path.StartsWith('/') ? path[1..] : path).Split('/');
        if (parts.Length > 16) throw Error("xml_path_invalid");
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part == "text()" && index == parts.Length - 1) continue;
            if (part.StartsWith('@'))
            {
                if (index != parts.Length - 1) throw Error("xml_path_invalid");
                part = part[1..];
            }
            _ = Name(part, ns);
        }
        return parts;
    }

    private static XName Name(string value, IReadOnlyDictionary<string, string> ns)
    {
        if (string.IsNullOrEmpty(value)) throw Error("xml_path_invalid");
        var parts = value.Split(':');
        try
        {
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) throw Error("xml_path_invalid");
                XmlConvert.VerifyNCName(part);
            }
            return parts.Length == 1 ? XName.Get(value) : parts.Length == 2 && ns.TryGetValue(parts[0], out var uri)
                ? XName.Get(parts[1], uri) : throw Error("xml_namespace_invalid");
        }
        catch (XmlException) { throw Error("xml_path_invalid"); }
    }

    private static JsonNode? Map(XObject context, JsonElement rule, IReadOnlyDictionary<string, string> ns, MappingBudget budget)
    {
        var selected = Select(context, rule.GetProperty("path").GetString()!, ns, budget);
        var type = rule.GetProperty("type").GetString();
        if (type == "array")
        {
            budget.Array(selected.Count);
            budget.Output(2);
            var array = new JsonArray();
            foreach (var item in selected)
            {
                budget.Output(array.Count == 0 ? 0 : 1);
                array.Add(Fields(item, rule, ns, budget));
            }
            return array;
        }
        if (selected.Count == 0 && rule.TryGetProperty("optional", out var optional) && optional.GetBoolean()) return null;
        if (selected.Count != 1) throw Error(selected.Count == 0 ? "xml_path_missing" : "xml_path_ambiguous");
        if (type == "object") return Fields(selected[0], rule, ns, budget);
        var text = selected[0] switch
        {
            XAttribute a => a.Value,
            XText t => t.Value,
            XElement e when !e.HasElements => e.Value,
            _ => throw Error("xml_scalar_invalid")
        };
        var scalar = type switch
        {
            "string" => JsonValue.Create(text),
            "integer" => JsonValue.Create(long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)),
            "number" => JsonValue.Create(decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)),
            "boolean" => JsonValue.Create(XmlConvert.ToBoolean(text)),
            "dateTime" => JsonValue.Create(ParseDate(text)),
            _ => throw Error("xml_rules_invalid")
        };
        budget.Output(JsonSerializer.SerializeToUtf8Bytes(scalar).Length);
        return scalar;
    }

    private static string ParseDate(string text)
    {
        string[] formats = ["r", "ddd, dd MMM yyyy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm:ss zzz", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        // RFC 822 numeric offsets omit the colon.
        if (text.Length >= 5 && text[^5] is '+' or '-' && text[^3] != ':') text = text.Insert(text.Length - 2, ":");
        if (!DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) throw Error("xml_date_invalid");
        return date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
    }

    private static JsonObject Fields(XObject context, JsonElement rule, IReadOnlyDictionary<string, string> ns, MappingBudget budget)
    {
        if (context is not (XElement or XDocument)) throw Error("xml_object_invalid");
        budget.Output(2);
        var result = new JsonObject();
        foreach (var field in rule.GetProperty("fields").EnumerateObject())
        {
            budget.Spend();
            var value = Map(context, field.Value, ns, budget);
            if (value is not null)
            {
                budget.Output(JsonSerializer.SerializeToUtf8Bytes(field.Name).Length + 1 + (result.Count == 0 ? 0 : 1));
                result.Add(field.Name, value);
            }
        }
        return result;
    }

    private static List<XObject> Select(XObject context, string path, IReadOnlyDictionary<string, string> ns, MappingBudget budget)
    {
        List<XObject> current = [path.StartsWith('/') ? context.Document ?? context : context];
        foreach (var part in Segments(path, ns))
        {
            var next = new List<XObject>();
            var text = part == "text()";
            var attribute = part.StartsWith('@');
            var name = text ? null : Name(attribute ? part[1..] : part, ns);
            foreach (var node in current)
            {
                IEnumerable<XObject> candidates = attribute ? (node as XElement)?.Attributes() ?? []
                    : (node as XContainer)?.Nodes() ?? [];
                foreach (var candidate in candidates)
                {
                    budget.Spend();
                    if (!(text ? candidate is XText : attribute ? candidate is XAttribute a && a.Name == name
                        : candidate is XElement e && e.Name == name)) continue;
                    budget.Selection((long)next.Count + 1);
                    next.Add(candidate);
                }
            }
            current = next;
        }
        return current;
    }

    private sealed class MappingBudget
    {
        private readonly CancellationToken token;
        private readonly XmlContentOptions xml;
        private readonly FunctionContentOptions functions;
        private long operations, outputBytes;
        public MappingBudget(XmlContentOptions xmlOptions, FunctionContentOptions functionOptions, CancellationToken ct)
        {
            xmlOptions.Validate();
            functionOptions.Validate();
            (token, xml, functions) = (ct, xmlOptions, functionOptions);
        }
        public void Output(int bytes) { outputBytes += bytes; CheckOutput(outputBytes); }
        public void CheckOutput(long bytes) => Check(bytes, functions.MaximumValueBytes, "jsonBytes",
            FunctionContentOptions.SectionName + ":MaximumValueBytes", "xml_output_limit", "function-output");
        public void Selection(long count) => Check(count, xml.MaximumSelectedNodes, "selectedNodes",
            XmlContentOptions.SectionName + ":MaximumSelectedNodes", "xml_selection_limit");
        public void Array(long count) => Check(count, xml.MaximumArrayItems, "arrayItems",
            XmlContentOptions.SectionName + ":MaximumArrayItems", "xml_array_limit");
        public void Spend()
        {
            token.ThrowIfCancellationRequested();
            Check(++operations, xml.MaximumMappingOperations, "mappingOperations",
                XmlContentOptions.SectionName + ":MaximumMappingOperations", "xml_mapping_limit");
        }
        private static void Check(long actual, int maximum, string unit, string setting, string rule, string boundary = "function-input")
        {
            if (actual > maximum) throw DataContracts.Failure(rule, boundary, "xml.to_json", boundary == "function-input" ? "$.value" : "$",
                rule, "bounded value", "processing limit exceeded", limit: new(unit, actual, Maximum: maximum, Setting: setting));
        }
    }
    private static TransformationFunctionException Error(string code) => new(code, "The bounded XML mapping could not be validated or executed.");
}
