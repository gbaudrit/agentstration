using System.Text.Json;
using Agentstration.Knowledge;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Tests;

public sealed partial class KnowledgeToolInputPresentationTests
{
    [TestMethod]
    public void FlatODataPresentationKeepsOptionalArgumentsAtToolLevel()
    {
        var published = Published.GetProperty("properties").GetProperty("request");
        var schema = KnowledgeToolMetadataService.InputSchemaForModel(published, Profile);
        foreach (var json in new[] { "{}", """{"query":{}}""", """{"pageSize":50,"query":{"$filter":"status eq 'open'"}}""", """{"cursor":"next"}""" })
            ToolInputSchemaValidator.Validate(schema, JsonSerializer.Deserialize<JsonElement>(json));
        Assert.IsFalse(schema.GetProperty("properties").TryGetProperty("request", out _));
        Assert.IsFalse(schema.TryGetProperty("required", out _));
        var help = KnowledgeToolMetadataService.Format("records", null, "Query records.", Profile, flatArguments: true);
        StringAssert.Contains(help, "do not add a request wrapper");
        StringAssert.Contains(help, "Use pageSize for pages");
        Assert.DoesNotContain("request.query", help);
        Assert.DoesNotContain("request.pageSize", help);
        Assert.DoesNotContain("{\"request\":", help);
    }

    [TestMethod]
    [DataRow("""{"request":{"query":{}}}""", "tool_argument_unknown", "$.request")]
    [DataRow("""{"pageSize":"50","query":{}}""", "tool_argument_type_invalid", "$.pageSize")]
    [DataRow("""{"query":{"filter":"status eq 'open'"}}""", "tool_argument_unknown", "$.query.filter")]
    [DataRow("""{"profile":{},"query":{}}""", "tool_argument_unknown", "$.profile")]
    public void FlatODataPresentationRejectsWrappersAndInvalidFields(string json, string code, string path)
    {
        var schema = KnowledgeToolMetadataService.InputSchemaForModel(Published.GetProperty("properties").GetProperty("request"), Profile);
        var failure = Assert.Throws<ToolInputValidationException>(() => ToolInputSchemaValidator.Validate(schema, JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.AreEqual(code, failure.Code);
        Assert.AreEqual(path, failure.Path);
    }
}
