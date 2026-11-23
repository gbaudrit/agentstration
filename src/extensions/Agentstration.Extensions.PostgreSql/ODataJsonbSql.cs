using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using Npgsql;
using NpgsqlTypes;

namespace Agentstration.Extensions.PostgreSql;

/// <summary>Only reviewed parser nodes become SQL. All paths, literals and cursor values are parameters.</summary>
internal sealed class ODataJsonbSql
{
    public List<NpgsqlParameter> Parameters { get; } = [];
    private int lambda;
    public string Parameter(object? value, NpgsqlDbType? type = null)
    {
        var name = "q" + Parameters.Count.ToString(CultureInfo.InvariantCulture);
        var parameter = new NpgsqlParameter { ParameterName = name, Value = value ?? DBNull.Value };
        if (type is { } declared) parameter.NpgsqlDbType = declared;
        Parameters.Add(parameter);
        return "@" + name;
    }
    public string Expression(QueryNode node, string json = "d.body", EdmPrimitiveTypeKind hint = EdmPrimitiveTypeKind.None)
    {
        switch (node)
        {
            case ConstantNode constant:
                var value = constant.Value;
                if (value is null) return "NULL";
                if (value is int or long or double or float) value = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return Parameter(value, value is decimal ? NpgsqlDbType.Numeric : value is bool ? NpgsqlDbType.Boolean : NpgsqlDbType.Text);
            case ConvertNode conversion:
                return Expression(conversion.Source, json, conversion.TypeReference.PrimitiveKind());
            case BinaryOperatorNode binary:
                var kind = Primitive(binary.Left);
                if (kind == EdmPrimitiveTypeKind.None) kind = Primitive(binary.Right);
                var left = Expression(binary.Left, json, kind); var right = Expression(binary.Right, json, kind);
                var op = binary.OperatorKind switch
                {
                    BinaryOperatorKind.And => "AND",
                    BinaryOperatorKind.Or => "OR",
                    BinaryOperatorKind.Equal => "IS NOT DISTINCT FROM",
                    BinaryOperatorKind.NotEqual => "IS DISTINCT FROM",
                    BinaryOperatorKind.GreaterThan => ">",
                    BinaryOperatorKind.GreaterThanOrEqual => ">=",
                    BinaryOperatorKind.LessThan => "<",
                    BinaryOperatorKind.LessThanOrEqual => "<=",
                    _ => throw Unsupported()
                };
                var expression = "(" + left + " " + op + " " + right + ")";
                if (binary.OperatorKind == BinaryOperatorKind.Equal && json == "d.body"
                    && Containment(binary.Left, binary.Right) is { } contained)
                    return "(d.body @> " + Parameter(contained, NpgsqlDbType.Jsonb) + " AND " + expression + ")";
                if (binary.OperatorKind is BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.GreaterThanOrEqual)
                    return "(" + expression + " OR (" + left + " IS NULL AND " + right + " IS NULL)) IS TRUE";
                return binary.OperatorKind is BinaryOperatorKind.LessThan or BinaryOperatorKind.GreaterThan
                    ? "COALESCE(" + expression + ", FALSE)" : expression;
            case UnaryOperatorNode unary:
                return "(NOT " + Expression(unary.Operand, json) + ")";
            case InNode inside:
                var comparisons = ((CollectionConstantNode)inside.Right).Collection.Select(c => "(" + Expression(inside.Left, json, Primitive(c))
                    + " IS NOT DISTINCT FROM " + Expression(c, json) + ")").ToArray();
                return comparisons.Length == 0 ? "FALSE" : "(" + string.Join(" OR ", comparisons) + ")";
            case AnyNode any:
                return Lambda(any.Source, any.Body, json, false);
            case AllNode all:
                return Lambda(all.Source, all.Body, json, true);
            case SingleValueFunctionCallNode function:
                var parameters = function.Parameters.Select(n => Expression(n, json, EdmPrimitiveTypeKind.String)).ToArray();
                return function.Name switch
                {
                    "contains" => "(strpos(" + parameters[0] + "," + parameters[1] + ") > 0)",
                    "startswith" => "(left(" + parameters[0] + ",length(" + parameters[1] + ")) = " + parameters[1] + ")",
                    "endswith" => "(right(" + parameters[0] + ",length(" + parameters[1] + ")) = " + parameters[1] + ")",
                    _ => throw Unsupported()
                };
            default:
                var path = ODataDocumentQuery.Path(node);
                var access = path.Length == 0 ? json : "(" + json + " #> " + Parameter(path, NpgsqlDbType.Array | NpgsqlDbType.Text) + ")";
                var type = hint == EdmPrimitiveTypeKind.None ? Primitive(node) : hint;
                var scalar = "(" + access + " #>> '{}')";
                return type switch
                {
                    EdmPrimitiveTypeKind.Decimal or EdmPrimitiveTypeKind.Int32 or EdmPrimitiveTypeKind.Int64 or EdmPrimitiveTypeKind.Double =>
                        "(CASE WHEN jsonb_typeof(" + access + ") = 'number' THEN " + scalar + "::numeric END)",
                    EdmPrimitiveTypeKind.Boolean => "(CASE WHEN jsonb_typeof(" + access + ") = 'boolean' THEN " + scalar + "::boolean END)",
                    EdmPrimitiveTypeKind.String => "((CASE WHEN jsonb_typeof(" + access + ") = 'string' THEN " + scalar + " END) COLLATE \"C\")",
                    EdmPrimitiveTypeKind.None => "NULLIF(" + access + ", 'null'::jsonb)",
                    _ => throw Unsupported()
                };
        }
    }
    private static string? Containment(QueryNode property, QueryNode literal)
    {
        while (literal is ConvertNode convert) literal = convert.Source;
        if (literal is not ConstantNode { Value: not null } constant || Primitive(property) == EdmPrimitiveTypeKind.None) return null;
        string[] path;
        try { path = ODataDocumentQuery.Path(property); } catch (AspException) { return null; }
        if (path.Length == 0) return null;
        JsonNode? node = JsonSerializer.SerializeToNode(constant.Value);
        for (var index = path.Length - 1; index >= 0; index--) node = new JsonObject { [path[index]] = node };
        return node!.ToJsonString();
    }
    private string Lambda(CollectionNode source, SingleValueNode body, string json, bool all)
    {
        var path = ODataDocumentQuery.Path(source);
        var array = "(" + json + " #> " + Parameter(path, NpgsqlDbType.Array | NpgsqlDbType.Text) + ")";
        var alias = "element" + (++lambda).ToString(CultureInfo.InvariantCulture);
        var condition = Expression(body, alias + ".value");
        return (all ? "NOT EXISTS" : "EXISTS") + " (SELECT 1 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(" + array
            + ") = 'array' THEN " + array + " ELSE '[]'::jsonb END) AS " + alias + "(value) WHERE "
            + (all ? "(" + condition + ") IS NOT TRUE" : "(" + condition + ") IS TRUE") + ")";
    }
    public static EdmPrimitiveTypeKind Primitive(QueryNode node) => node is SingleValueNode value && value.TypeReference?.IsPrimitive() == true
        ? value.TypeReference.PrimitiveKind() : EdmPrimitiveTypeKind.None;

    public string After(ODataDocumentQuery query, ODataDocumentPosition position)
    {
        AspValidation.Require(position.Values.Count == query.Sort.Count, "invalid_reference", "Invalid query position.");
        var clauses = new List<string>(); var equal = new List<string>();
        for (var i = 0; i < query.Sort.Count; i++)
        {
            var item = query.Sort[i]; var value = ODataDocumentQuery.Scalar(position.Values[i]);
            var field = Expression(item.Expression); var parameter = Parameter(value,
                value is decimal ? NpgsqlDbType.Numeric : value is bool ? NpgsqlDbType.Boolean : NpgsqlDbType.Text);
            var later = value is null ? item.Descending ? "FALSE" : field + " IS NOT NULL"
                : item.Descending ? "(" + field + " < " + parameter + " OR " + field + " IS NULL)" : field + " > " + parameter;
            clauses.Add("(" + string.Join(" AND ", equal.Append(later)) + ")");
            equal.Add(field + " IS NOT DISTINCT FROM " + parameter);
        }
        clauses.Add("(" + string.Join(" AND ", equal.Append("d.id COLLATE \"C\" > " + Parameter(position.Id) + " COLLATE \"C\"")) + ")");
        return "(" + string.Join(" OR ", clauses) + ")";
    }
    public string Order(ODataDocumentQuery query) => string.Join(",", query.Sort.Select(s => Expression(s.Expression)
        + (s.Descending ? " DESC NULLS LAST" : " ASC NULLS FIRST")).Append("d.id COLLATE \"C\" ASC"));
    private static AspException Unsupported() => new("unsupported_query", "This OData expression is not supported by the JSONB translator.");
}
