using System.Text.Json;
using Agentstration.Web.Components.JsonSchema;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class JsonSchemaInputSchemaTests
{
    [TestMethod]
    public void ObjectPropertyReturnsAnIndependentNestedSchema()
    {
        using var document = JsonDocument.Parse("""
            { "type": "object", "properties": { "parameters": { "type": "object", "properties": { "query": { "type": "string" } } } } }
            """);

        var schema = JsonSchemaInputSchema.ObjectProperty(document.RootElement, "parameters");

        Assert.IsNotNull(schema);
        Assert.AreEqual("object", schema.Value.GetProperty("type").GetString());
        Assert.IsTrue(schema.Value.GetProperty("properties").TryGetProperty("query", out _));
    }

    [TestMethod]
    public void ObjectPropertyReturnsNullForMissingOrNonObjectContracts()
    {
        using var document = JsonDocument.Parse("""{ "type": "array" }""");

        Assert.IsNull(JsonSchemaInputSchema.ObjectProperty(null, "parameters"));
        Assert.IsNull(JsonSchemaInputSchema.ObjectProperty(document.RootElement, "parameters"));
    }
}
