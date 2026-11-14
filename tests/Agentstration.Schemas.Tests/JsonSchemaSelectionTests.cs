using System.Text.Json;
using Agentstration.Schemas;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class JsonSchemaSelectionTests
{
    private const string Envelope = """
        {"type":"object","properties":{"total_count":{"type":"integer"},"results":{"type":"array","items":{"$ref":"#/$defs/row"}}},
         "required":["total_count","results"],"$defs":{"row":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}}}
        """;
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    [TestMethod]
    public async Task SelectedItemsResolveParentLocalReferencesAndValidateTheirOwnShape()
    {
        var parent = Json(Envelope);
        var selected = JsonSchemaSelection.Select(parent, "/properties/results/items");
        Assert.AreEqual("object", selected.GetProperty("type").GetString());
        Assert.IsTrue(parent.GetProperty("properties").GetProperty("results").GetProperty("items").TryGetProperty("$ref", out _));
        var handler = new BuiltinJsonSchemaFormatHandler();
        Assert.IsTrue((await handler.ValidateDefinitionAsync(parent, null, default)).IsValid);
        Assert.IsTrue((await handler.ValidateInstanceAsync(parent, Json("{\"total_count\":1,\"results\":[{\"name\":\"row\"}]}"), null, default)).IsValid);
        Assert.IsTrue((await handler.ValidateInstanceAsync(selected, Json("{\"name\":\"row\"}"), null, default)).IsValid);
        Assert.IsFalse((await handler.ValidateInstanceAsync(selected, Json("{}"), null, default)).IsValid);
        Assert.IsFalse((await handler.ValidateInstanceAsync(selected, Json("{\"name\":42}"), null, default)).IsValid);
        Assert.IsFalse((await handler.ValidateInstanceAsync(parent, Json("{\"name\":\"row\"}"), null, default)).IsValid);
    }

    [TestMethod]
    public void EscapedPropertyNamesAreSelectedWithoutInterpretingJsonPath()
    {
        var schema = Json("{\"type\":\"object\",\"properties\":{\"a/b~c\":{\"type\":\"string\"}}}");
        Assert.AreEqual("string", JsonSchemaSelection.Select(schema, "/properties/a~1b~0c").GetProperty("type").GetString());
    }

    [TestMethod]
    [DataRow("/properties/missing", "schema_pointer_not_found")]
    [DataRow("$.results[*]", "schema_pointer_invalid")]
    [DataRow("/properties", "schema_pointer_invalid")]
    [DataRow("/default", "schema_pointer_invalid")]
    public void InvalidSelectionsNeverFallBackToRoot(string path, string code)
    {
        var error = Assert.ThrowsExactly<SchemaSelectionException>(() => JsonSchemaSelection.Select(Json(Envelope), path));
        Assert.AreEqual(code, error.Code);
    }

    [TestMethod]
    [DataRow("{\"$ref\":\"https://example.test/schema\"}", "schema_reference_remote_unsupported")]
    [DataRow("{\"$ref\":\"#\"}", "schema_reference_cycle")]
    [DataRow("{\"$ref\":\"#/$defs/a\",\"$defs\":{\"a\":{\"type\":\"string\"}},\"minLength\":2}", "schema_reference_siblings_unsupported")]
    public void UnsafeReferencesFailExplicitly(string schema, string code)
    {
        var error = Assert.ThrowsExactly<SchemaSelectionException>(() => JsonSchemaSelection.Select(Json(schema)));
        Assert.AreEqual(code, error.Code);
    }

    [TestMethod]
    public void GenericJsonAcceptanceIsNotStructuralValidation()
    {
        var generic = DataContracts.Declared(Json("{\"type\":[\"object\",\"array\",\"string\",\"number\",\"boolean\",\"null\"]}"), "generic")!;
        Assert.IsFalse(generic.HasStructuralConstraints);
        Assert.IsFalse(DataContracts.Declared(Json("{\"type\":\"object\"}"), "generic")!.HasStructuralConstraints);
        Assert.IsTrue(DataContracts.Declared(JsonSchemaSelection.Select(Json(Envelope)), "declared")!.HasStructuralConstraints);
    }

    [TestMethod]
    [DataRow("2026-11-13T18:00:00Z", true)]
    [DataRow("2026-11-13T19:00:00+01:00", true)]
    [DataRow("2026-02-30T18:00:00Z", false)]
    [DataRow("2026-11-13", false)]
    [DataRow("private-response-body", false)]
    public async Task DeclaredDateTimeUsesTheSameAssertedFormatAsTheDocumentProvider(string value, bool valid)
    {
        var handler = new BuiltinJsonSchemaFormatHandler();
        var schema = Json("""{"type":"string","format":"date-time"}""");
        Assert.IsTrue((await handler.ValidateDefinitionAsync(schema, null, default)).IsValid);
        var result = await handler.ValidateInstanceAsync(schema, JsonSerializer.SerializeToElement(value), null, default);
        Assert.AreEqual(valid, result.IsValid);
        if (!valid)
        {
            Assert.AreEqual("json_schema_instance_format", result.Issues[0].Code);
            Assert.DoesNotContain(value, result.Issues[0].Message);
        }
    }
}
