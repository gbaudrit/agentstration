using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Asp.Abstractions;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using Microsoft.OData.UriParser.Aggregation;

namespace Agentstration.Asp.OData;

/// <summary>The standard parser owns grammar and binding; this adapter rejects unsupported constructs.</summary>
public sealed class ODataDocumentProfile
{
    public const string Id = "odata/4.01";
    public const string Version = "1.0";
    public const string Encoding = "odata-options+json";
    private readonly EdmModel model = new();
    private readonly EdmComplexType root;
    public JsonElement Schema { get; }
    public AspQueryProfileDescriptor Descriptor { get; }

    public ODataDocumentProfile(JsonElement schema)
    {
        DocumentModel.ValidateSchema(schema);
        Schema = schema.Clone();
        root = BuildType(schema, "Document", 0);
        Descriptor = new(new(Id, Version, Encoding, AspDocumentValidation.Digest(schema)),
            "OData 4.01 URL conventions; Data Aggregation 4.0",
            ["filter", "orderby", "select", "any", "all", "in", "count", "sum", "min", "max", "groupby", "unicode-codepoint-order"],
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                additionalProperties = false,
                properties = new Dictionary<string, object>
                {
                    ["$filter"] = new { type = "string", maxLength = 4096 },
                    ["$orderby"] = new { type = "string", maxLength = 1024 },
                    ["$select"] = new { type = "string", maxLength = 1024 },
                    ["$apply"] = new { type = "string", maxLength = 2048 }
                }
            }));
    }

    public ODataDocumentQuery Parse(JsonElement value)
    {
        AspValidation.Require(value.ValueKind == JsonValueKind.Object && value.GetRawText().Length <= 8192,
            "invalid_query", "OData options must be a bounded object.");
        AspDocumentValidation.ValidateJson(value);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            AspValidation.Require(property.Name is "$filter" or "$orderby" or "$select" or "$apply"
                && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.Length <= 4096,
                "unsupported_query", "The requested OData option is unsupported.");
            options.Add(property.Name, property.Value.GetString()!);
        }
        try
        {
            var parser = new ODataQueryOptionParser(model, root, null, options);
            parser.Settings.MaximumExpansionDepth = 0;
            parser.Settings.MaximumExpansionCount = 0;
            var filter = parser.ParseFilter()?.Expression;
            var sort = new List<ODataSort>();
            for (var order = parser.ParseOrderBy(); order is not null; order = order.ThenBy)
            {
                AspValidation.Require(sort.Count < 4, "limit_exceeded", "At most four sort keys are supported.");
                sort.Add(new(order.Expression, order.Direction == OrderByDirection.Descending));
            }
            var selected = new List<string[]>();
            var select = parser.ParseSelectAndExpand();
            if (select is not null)
                foreach (var item in select.SelectedItems)
                {
                    AspValidation.Require(item is PathSelectItem, "unsupported_query", "Only explicit property selection is supported.");
                    var path = ((PathSelectItem)item).SelectedPath.Select(segment => segment.Identifier).ToArray();
                    AspValidation.Require(selected.Count < 16 && path.Length <= 8, "limit_exceeded", "Selection exceeds its limits.");
                    selected.Add(path);
                }
            var groups = new List<ODataGroup>(); var aggregates = new List<ODataAggregate>();
            var apply = parser.ParseApply();
            if (apply is not null)
            {
                AspValidation.Require(sort.Count == 0 && selected.Count == 0, "unsupported_query", "Aggregate ordering/selection is not implemented.");
                var transforms = apply.Transformations.ToArray();
                AspValidation.Require(transforms.Length == 1, "unsupported_query", "Only one aggregate or groupby transformation is supported.");
                AggregateTransformationNode? aggregate;
                if (transforms[0] is GroupByTransformationNode grouped)
                {
                    foreach (var group in grouped.GroupingProperties) AddGroups(group, [], groups);
                    aggregate = grouped.ChildTransformations as AggregateTransformationNode;
                }
                else aggregate = transforms[0] as AggregateTransformationNode;
                AspValidation.Require(aggregate is not null && groups.Count <= 4, "unsupported_query", "A bounded aggregate is required.");
                foreach (var expression in aggregate.AggregateExpressions)
                {
                    AspValidation.Require(expression is AggregateExpression, "unsupported_query", "Unsupported aggregate expression.");
                    var expressionValue = (AggregateExpression)expression;
                    AspValidation.Require(expressionValue.Method is AggregationMethod.VirtualPropertyCount or AggregationMethod.Sum
                        or AggregationMethod.Min or AggregationMethod.Max, "unsupported_query", "Unsupported aggregation method.");
                    AspValidation.Require(aggregates.Count < 8 && expressionValue.Alias.Length <= 64,
                        "limit_exceeded", "Aggregate count/alias exceeds its bound.");
                    aggregates.Add(new(expressionValue.Alias, expressionValue.Method.ToString(), expressionValue.Expression));
                }
            }
            var result = new ODataDocumentQuery(filter, sort, selected, groups, aggregates);
            result.Validate();
            return result;
        }
        catch (ODataException) { throw new AspException("invalid_query", "The query is invalid for the declared OData model/profile."); }
    }

    private static void AddGroups(GroupByPropertyNode node, string[] parent, List<ODataGroup> groups)
    {
        var path = parent.Append(node.Name).ToArray();
        if (node.ChildTransformations.Count > 0)
            foreach (var child in node.ChildTransformations) AddGroups(child, path, groups);
        else groups.Add(new(path, node.Expression));
    }

    private EdmComplexType BuildType(JsonElement schema, string name, int depth)
    {
        AspValidation.Require(depth <= 8 && schema.ValueKind == JsonValueKind.Object, "invalid_schema", "The query schema exceeds its supported shape.");
        var type = new EdmComplexType("Asp", name, null, false, true); model.AddElement(type);
        if (schema.TryGetProperty("properties", out var properties))
        {
            AspValidation.Require(properties.ValueKind == JsonValueKind.Object && properties.EnumerateObject().Count() <= 32,
                "invalid_schema", "Too many queryable properties.");
            foreach (var property in properties.EnumerateObject())
            {
                AspValidation.Require(property.Name.Length <= 64 && property.Name.Length > 0
                    && (char.IsAsciiLetter(property.Name[0]) || property.Name[0] == '_')
                    && property.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'),
                    "invalid_schema", "OData declared property names must be identifiers.");
                type.AddStructuralProperty(property.Name, TypeReference(property.Value, name + "_" + property.Name, depth + 1));
            }
        }
        return type;
    }
    private IEdmTypeReference TypeReference(JsonElement schema, string name, int depth)
    {
        var kind = DocumentModel.Type(schema);
        return kind switch
        {
            "object" => new EdmComplexTypeReference(BuildType(schema, name, depth), true),
            "array" when schema.TryGetProperty("items", out var item) => new EdmCollectionTypeReference(new EdmCollectionType(TypeReference(item, name + "Item", depth + 1))),
            "number" or "integer" => EdmCoreModel.Instance.GetDecimal(true),
            "boolean" => EdmCoreModel.Instance.GetBoolean(true),
            "string" => EdmCoreModel.Instance.GetString(true),
            _ => throw new AspException("invalid_schema", "Every queryable schema node requires a supported type.")
        };
    }
}

public sealed record ODataSort(SingleValueNode Expression, bool Descending);
public sealed record ODataGroup(string[] Path, SingleValueNode Expression);
public sealed record ODataAggregate(string Alias, string Method, SingleValueNode Expression);
public sealed record ODataDocumentPosition(string Id, IReadOnlyList<JsonElement> Values);

/// <summary>A profile-specific bound parser tree; this is not a generic ASP wire AST.</summary>
public sealed record ODataDocumentQuery(SingleValueNode? Filter, IReadOnlyList<ODataSort> Sort,
    IReadOnlyList<string[]> Select, IReadOnlyList<ODataGroup> GroupBy, IReadOnlyList<ODataAggregate> Aggregates)
{
    public bool IsAggregate => Aggregates.Count > 0;
    public void Validate()
    {
        var nodes = 0;
        string? activeRange = null;
        void Visit(QueryNode node, int depth)
        {
            AspValidation.Require(++nodes <= 64 && depth <= 12, "limit_exceeded", "The query expression exceeds its complexity bound.");
            switch (node)
            {
                case ConstantNode constant:
                    AspValidation.Require(constant.Value is null or string or bool or decimal or int or long or double or float,
                        "unsupported_query", "Unsupported literal type."); break;
                case ConvertNode convert:
                    AspValidation.Require(convert.TypeReference.IsPrimitive(), "unsupported_query", "Unsupported conversion.");
                    Visit(convert.Source, depth + 1); break;
                case BinaryOperatorNode binary:
                    AspValidation.Require(binary.OperatorKind is BinaryOperatorKind.And or BinaryOperatorKind.Or
                        or BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual or BinaryOperatorKind.GreaterThan
                        or BinaryOperatorKind.GreaterThanOrEqual or BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual,
                        "unsupported_query", "Unsupported binary operator.");
                    Visit(binary.Left, depth + 1); Visit(binary.Right, depth + 1); break;
                case UnaryOperatorNode unary when unary.OperatorKind == UnaryOperatorKind.Not:
                    Visit(unary.Operand, depth + 1); break;
                case SingleValuePropertyAccessNode property: Visit(property.Source, depth + 1); break;
                case SingleValueOpenPropertyAccessNode property: Visit(property.Source, depth + 1); break;
                case SingleComplexNode complex: Visit(complex.Source, depth + 1); break;
                case CollectionPropertyAccessNode collection: Visit(collection.Source, depth + 1); break;
                case CollectionComplexNode collection: Visit(collection.Source, depth + 1); break;
                case ResourceRangeVariableReferenceNode reference:
                    AspValidation.Require(activeRange is null || reference.Name == activeRange, "unsupported_query", "Outer lambda captures are unsupported."); break;
                case NonResourceRangeVariableReferenceNode reference:
                    AspValidation.Require(activeRange is null || reference.Name == activeRange, "unsupported_query", "Outer lambda captures are unsupported."); break;
                case AnyNode any:
                    AspValidation.Require(activeRange is null, "unsupported_query", "Nested lambdas are unsupported.");
                    Visit(any.Source, depth + 1); activeRange = any.CurrentRangeVariable?.Name ?? "$empty";
                    Visit(any.Body, depth + 1); activeRange = null; break;
                case AllNode all:
                    AspValidation.Require(activeRange is null, "unsupported_query", "Nested lambdas are unsupported.");
                    Visit(all.Source, depth + 1); activeRange = all.CurrentRangeVariable?.Name ?? "$empty";
                    Visit(all.Body, depth + 1); activeRange = null; break;
                case SingleValueFunctionCallNode function when function.Name is "contains" or "startswith" or "endswith":
                    AspValidation.Require(function.Parameters.Count() == 2, "unsupported_query", "Unsupported function arity.");
                    foreach (var parameter in function.Parameters) Visit(parameter, depth + 1); break;
                case InNode inside: Visit(inside.Left, depth + 1); Visit(inside.Right, depth + 1); break;
                case CollectionConstantNode constants:
                    AspValidation.Require(constants.Collection.Count <= 32, "limit_exceeded", "The in-list exceeds its bound.");
                    foreach (var constant in constants.Collection) Visit(constant, depth + 1); break;
                default: throw new AspException("unsupported_query", "The OData construct is not implemented by this profile.");
            }
        }
        if (Filter is not null) Visit(Filter, 0);
        static bool ScalarType(SingleValueNode node) => node.TypeReference?.IsPrimitive() == true
            && node.TypeReference.PrimitiveKind() is EdmPrimitiveTypeKind.String or EdmPrimitiveTypeKind.Decimal or EdmPrimitiveTypeKind.Boolean;
        foreach (var sort in Sort) { Visit(sort.Expression, 0); _ = Path(sort.Expression); AspValidation.Require(ScalarType(sort.Expression), "unsupported_query", "Sorting requires a declared scalar field."); }
        foreach (var group in GroupBy) { Visit(group.Expression, 0); _ = Path(group.Expression); AspValidation.Require(ScalarType(group.Expression), "unsupported_query", "Grouping requires a declared scalar field."); }
        foreach (var aggregate in Aggregates)
            if (aggregate.Method != nameof(AggregationMethod.VirtualPropertyCount))
            {
                Visit(aggregate.Expression, 0); _ = Path(aggregate.Expression);
                AspValidation.Require(ScalarType(aggregate.Expression) && (aggregate.Method != nameof(AggregationMethod.Sum)
                    || aggregate.Expression.TypeReference.PrimitiveKind() == EdmPrimitiveTypeKind.Decimal), "unsupported_query", "Aggregates require a declared scalar; sum requires a number.");
            }
        AspValidation.Require(!GroupBy.Any(g => Aggregates.Any(a => a.Alias == g.Path[0])), "invalid_query", "Aggregate aliases must not collide with group roots.");
        AspValidation.Require(GroupBy.Select(g => string.Join('/', g.Path)).Concat(Aggregates.Select(a => a.Alias)).Distinct(StringComparer.Ordinal).Count()
            == GroupBy.Count + Aggregates.Count, "invalid_query", "Group and aggregate names collide.");
    }

    public static string[] Path(QueryNode node) => node switch
    {
        ConvertNode convert => Path(convert.Source),
        SingleValuePropertyAccessNode p => Path(p.Source).Append(p.Property.Name).ToArray(),
        SingleValueOpenPropertyAccessNode p => Path(p.Source).Append(p.Name).ToArray(),
        SingleComplexNode p => Path(p.Source).Append(p.Property.Name).ToArray(),
        CollectionPropertyAccessNode p => Path(p.Source).Append(p.Property.Name).ToArray(),
        CollectionComplexNode p => Path(p.Source).Append(p.Property.Name).ToArray(),
        ResourceRangeVariableReferenceNode or NonResourceRangeVariableReferenceNode => [],
        _ => throw new AspException("unsupported_query", "An explicit property path is required.")
    };

    public static object? Evaluate(QueryNode node, JsonElement document)
    {
        object? Eval(QueryNode child) => Evaluate(child, document);
        switch (node)
        {
            case ConstantNode value: return Number(value.Value);
            case ConvertNode convert: return Coerce(Eval(convert.Source), convert.TypeReference.PrimitiveKind());
            case ResourceRangeVariableReferenceNode: case NonResourceRangeVariableReferenceNode: return Scalar(document);
            case SingleValuePropertyAccessNode property: return Property(Eval(property.Source), property.Property.Name);
            case SingleValueOpenPropertyAccessNode property: return Property(Eval(property.Source), property.Name);
            case SingleComplexNode property: return Property(Eval(property.Source), property.Property.Name);
            case CollectionPropertyAccessNode property: return Property(Eval(property.Source), property.Property.Name);
            case CollectionComplexNode property: return Property(Eval(property.Source), property.Property.Name);
            case UnaryOperatorNode unary: return Eval(unary.Operand) is bool flag ? !flag : null;
            case BinaryOperatorNode binary:
                var left = Eval(binary.Left); var right = Eval(binary.Right);
                var hint = binary.Left.TypeReference?.PrimitiveKind() ?? binary.Right.TypeReference?.PrimitiveKind() ?? EdmPrimitiveTypeKind.None;
                if (hint != EdmPrimitiveTypeKind.None) { left = Coerce(left, hint); right = Coerce(right, hint); }
                if (binary.OperatorKind == BinaryOperatorKind.And)
                    return left is false || right is false ? false : left is true && right is true ? true : null;
                if (binary.OperatorKind == BinaryOperatorKind.Or)
                    return left is true || right is true ? true : left is false && right is false ? false : null;
                if (binary.OperatorKind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual)
                {
                    var equal = left is null || right is null ? left is null && right is null : Compare(left, right) == 0;
                    return binary.OperatorKind == BinaryOperatorKind.Equal ? equal : !equal;
                }
                if (left is null || right is null) return left is null && right is null
                    && binary.OperatorKind is BinaryOperatorKind.GreaterThanOrEqual or BinaryOperatorKind.LessThanOrEqual;
                var comparison = Compare(left, right);
                return binary.OperatorKind switch
                {
                    BinaryOperatorKind.GreaterThan => comparison > 0,
                    BinaryOperatorKind.GreaterThanOrEqual => comparison >= 0,
                    BinaryOperatorKind.LessThan => comparison < 0,
                    BinaryOperatorKind.LessThanOrEqual => comparison <= 0,
                    _ => throw new AspException("unsupported_query", "Unsupported comparison.")
                };
            case AnyNode any:
                return Array(Eval(any.Source)).Any(item => Evaluate(any.Body, item) is true);
            case AllNode all:
                return Array(Eval(all.Source)).All(item => Evaluate(all.Body, item) is true);
            case InNode inside:
                return ((CollectionConstantNode)inside.Right).Collection.Any(item => Equals(Eval(inside.Left), Eval(item)));
            case SingleValueFunctionCallNode function:
                var args = function.Parameters.Select(Eval).ToArray();
                if (args[0] is not string text || args[1] is not string part) return null;
                return function.Name switch
                {
                    "contains" => text.Contains(part, StringComparison.Ordinal),
                    "startswith" => text.StartsWith(part, StringComparison.Ordinal),
                    "endswith" => text.EndsWith(part, StringComparison.Ordinal),
                    _ => throw new AspException("unsupported_query", "Unsupported function.")
                };
            default: throw new AspException("unsupported_query", "Unsupported evaluation.");
        }
    }
    private static IEnumerable<JsonElement> Array(object? value) => value is JsonElement { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
    private static object? Property(object? source, string name) => source is JsonElement { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value) ? Scalar(value) : null;
    public static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => value
    };
    private static object? Coerce(object? value, EdmPrimitiveTypeKind kind) => kind switch
    {
        EdmPrimitiveTypeKind.String => value is string ? value : null,
        EdmPrimitiveTypeKind.Decimal or EdmPrimitiveTypeKind.Int32 or EdmPrimitiveTypeKind.Int64 or EdmPrimitiveTypeKind.Double => value is decimal ? value : null,
        EdmPrimitiveTypeKind.Boolean => value is bool ? value : null,
        _ => value
    };
    private static object? Number(object? value) => value is int or long or double or float ? Convert.ToDecimal(value, CultureInfo.InvariantCulture) : value;
    public static int Compare(object? left, object? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        if (left is decimal a && right is decimal b) return a.CompareTo(b);
        if (left is bool first && right is bool second) return first.CompareTo(second);
        if (left is string x && right is string y)
            return x.EnumerateRunes().Select(r => r.Value).ToArray().AsSpan().SequenceCompareTo(y.EnumerateRunes().Select(r => r.Value).ToArray());
        throw new AspException("type_mismatch", "Values are not comparable in this profile.");
    }
    public int CompareDocuments(AspDocument a, AspDocument b)
    {
        foreach (var sort in Sort)
        {
            var result = Compare(Evaluate(sort.Expression, a.Value), Evaluate(sort.Expression, b.Value));
            if (result != 0) return sort.Descending ? -result : result;
        }
        return string.CompareOrdinal(a.Id, b.Id);
    }
    public AspDocument Project(AspDocument document)
    {
        if (Select.Count == 0) return document;
        var projected = new JsonObject();
        foreach (var path in Select)
        {
            var value = document.Value; var found = true;
            foreach (var segment in path)
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) { found = false; break; }
            if (found) Set(projected, path, JsonNode.Parse(value.GetRawText()));
        }
        return document with { Value = JsonSerializer.SerializeToElement(projected) };
    }
    public static void Set(JsonObject target, IReadOnlyList<string> path, JsonNode? value)
    {
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (target[path[i]] is not JsonObject nested) { nested = new(); target[path[i]] = nested; }
            target = nested;
        }
        target[path[^1]] = value;
    }
}
