using System.Text.Json;
using Agentstration.Tools;

namespace Agentstration.Tools.Tests;

[TestClass]
public sealed class ToolInputSchemaUnionTests
{
    private const string FixedBindingsSchema = """
        {"type":"object","properties":{"fixedBindings":{
          "type":["object","null"],"default":null,
          "properties":{"region":{"type":"string","enum":["eu","us"]},"count":{"type":"integer","minimum":1,"maximum":4}},
          "required":["region"],"additionalProperties":false
        }},"required":["fixedBindings"],"additionalProperties":false}
        """;

    [TestMethod]
    [DataRow("null")]
    [DataRow("{\"region\":\"eu\",\"count\":2}")]
    public void NullableFixedBindingsAcceptsNullAndAValidObject(string value) =>
        Validate(FixedBindingsSchema, $"{{\"fixedBindings\":{value}}}");

    [TestMethod]
    [DataRow("\"invalid\"", "tool_argument_type_invalid", "$.fixedBindings")]
    [DataRow("42", "tool_argument_type_invalid", "$.fixedBindings")]
    [DataRow("{}", "tool_argument_required", "$.fixedBindings.region")]
    [DataRow("{\"region\":\"eu\",\"extra\":true}", "tool_argument_unknown", "$.fixedBindings.extra")]
    [DataRow("{\"region\":\"other\"}", "tool_argument_enum_invalid", "$.fixedBindings.region")]
    [DataRow("{\"region\":\"eu\",\"count\":0}", "tool_argument_minimum", "$.fixedBindings.count")]
    [DataRow("{\"region\":\"eu\",\"count\":5}", "tool_argument_maximum", "$.fixedBindings.count")]
    public void NullableFixedBindingsRetainsObjectValidation(string value, string code, string path) =>
        Reject(FixedBindingsSchema, $"{{\"fixedBindings\":{value}}}", code, path);

    [TestMethod]
    public void OptionalObjectIsNotImplicitlyNullable()
    {
        const string schema = """{"type":"object","properties":{"fixedBindings":{"type":"object"}}}""";
        Validate(schema, "{}");
        Reject(schema, """{"fixedBindings":null}""", "tool_argument_type_invalid", "$.fixedBindings");
        Reject(FixedBindingsSchema, "{}", "tool_argument_required", "$.fixedBindings");
    }

    [TestMethod]
    public void PropertiesInferAnObjectOnlyWhenTypeIsAbsent()
    {
        Reject("""{"properties":{}}""", "null", "tool_argument_type_invalid");
        Validate("""{"type":["string","null"],"properties":{},"minLength":2}""", "null");
        Validate("""{"type":["string","null"],"properties":{},"minLength":2}""", "\"ok\"");
        Reject("""{"type":["string","null"],"properties":{},"minLength":2}""", "\"x\"", "tool_argument_minimum_length");
        Reject("""{"type":["string","null"],"properties":{}}""", "{}", "tool_argument_type_invalid");
        Reject("""{"type":"object"}""", "null", "tool_argument_type_invalid");
    }

    [TestMethod]
    [DataRow("\"ok\"")]
    [DataRow("2")]
    public void ScalarUnionsAcceptEitherDeclaredType(string value) =>
        Validate("""{"type":["string","integer"],"minLength":2,"minimum":1,"maximum":3}""", value);

    [TestMethod]
    [DataRow("null", "tool_argument_type_invalid")]
    [DataRow("true", "tool_argument_type_invalid")]
    [DataRow("{}", "tool_argument_type_invalid")]
    [DataRow("[]", "tool_argument_type_invalid")]
    [DataRow("1.5", "tool_argument_type_invalid")]
    [DataRow("\"x\"", "tool_argument_minimum_length")]
    [DataRow("0", "tool_argument_minimum")]
    [DataRow("4", "tool_argument_maximum")]
    public void ScalarUnionsRejectOtherTypesAndEnforceTheEffectiveTypeConstraints(string value, string code) =>
        Reject("""{"type":["string","integer"],"minLength":2,"minimum":1,"maximum":3}""", value, code);

    [TestMethod]
    public void NullableArraysRetainItemAndLengthValidation()
    {
        const string schema = """{"type":["array","null"],"minItems":1,"maxItems":2,"items":{"type":["integer","null"],"minimum":0}}""";
        Validate(schema, "null");
        Validate(schema, "[0,null]");
        Reject(schema, "{}", "tool_argument_type_invalid");
        Reject(schema, "[]", "tool_argument_minimum_length");
        Reject(schema, "[0,1,2]", "tool_argument_maximum_length");
        Reject(schema, "[-1]", "tool_argument_minimum", "$[0]");
        Reject(schema, "[\"invalid\"]", "tool_argument_type_invalid", "$[0]");
        Reject("""{"type":["object","null"],"enum":[{}]}""", "null", "tool_argument_enum_invalid");
        Validate("""{"type":["boolean","number"]}""", "true");
        Validate("""{"type":["boolean","number"]}""", "1.5");
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("false")]
    [DataRow("42")]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("\"\"")]
    [DataRow("\"Object\"")]
    [DataRow("\"unsupported\"")]
    [DataRow("[\"string\",null]")]
    [DataRow("[\"string\",42]")]
    [DataRow("[\"string\",[\"null\"]]")]
    [DataRow("[\"string\",\"unsupported\"]")]
    [DataRow("[\"string\",\"string\"]")]
    public void MalformedTypeDeclarationsAreSchemaErrorsEvenIfTheFirstMemberMatches(string declaration) =>
        Reject($"{{\"type\":{declaration},\"properties\":{{}}}}", "\"ok\"", "tool_input_schema_invalid");

    [TestMethod]
    public void UnionValidationKeepsDepthAndPayloadBounds()
    {
        var schema = Json("""{"type":"integer"}""");
        var value = Json("1");
        for (var index = 0; index < 13; index++)
        {
            schema = JsonSerializer.SerializeToElement(new { type = new[] { "object", "null" }, properties = new { child = schema } });
            value = JsonSerializer.SerializeToElement(new { child = value });
        }
        var depth = Assert.ThrowsExactly<ToolInputValidationException>(() => ToolInputSchemaValidator.Validate(schema, value));
        Assert.AreEqual("tool_schema_too_deep", depth.Code);
        var oversized = JsonSerializer.SerializeToElement(new { fixedBindings = new string('x', 2 * 1024 * 1024) });
        var payload = Assert.ThrowsExactly<ToolInputValidationException>(() => ToolInputSchemaValidator.Validate(Json(FixedBindingsSchema), oversized));
        Assert.AreEqual("tool_arguments_too_large", payload.Code);
    }

    private static void Validate(string schema, string value) => ToolInputSchemaValidator.Validate(Json(schema), Json(value));
    private static void Reject(string schema, string value, string code, string path = "$")
    {
        var exception = Assert.ThrowsExactly<ToolInputValidationException>(() => Validate(schema, value));
        Assert.AreEqual(code, exception.Code);
        Assert.AreEqual(path, exception.Path);
    }
    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
