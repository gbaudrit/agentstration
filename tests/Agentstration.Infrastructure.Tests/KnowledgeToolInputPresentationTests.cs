using System.Text.Json;
using Agentstration.Knowledge;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed partial class KnowledgeToolInputPresentationTests
{
    private static readonly JsonElement Published = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"request":{"type":"object","properties":{"query":{},"pageSize":{"type":["integer","null"],"minimum":1,"maximum":100},"cursor":{"type":"string"}},"additionalProperties":false}},"required":[],"additionalProperties":false}
        """);
    private static readonly StorageQueryProfilePin Profile = new("odata/4.01", "1.0", "odata-options+json", "model");

    [TestMethod]
    public void ODataPresentationAcceptsQueriesAndContinuationsWithoutChangingThePublishedContract()
    {
        var original = Published.GetRawText();
        var schema = KnowledgeToolMetadataService.InputSchemaForModel(Published, Profile);
        foreach (var value in new[]
        {
            """{"request":{"pageSize":50,"query":{"$select":"name,publishedAt","$orderby":"publishedAt desc"}}}""",
            """{"request":{"query":{"$filter":"status eq 'open'"}}}""",
            """{"request":{"query":{}}}""",
            """{"request":{"cursor":"returned-cursor"}}"""
        }) ToolInputSchemaValidator.Validate(schema, JsonSerializer.Deserialize<JsonElement>(value));
        Assert.AreEqual(original, Published.GetRawText());
        Assert.IsTrue(JsonElement.DeepEquals(Published, KnowledgeToolMetadataService.InputSchemaForModel(Published, null)));
        Assert.IsTrue(JsonElement.DeepEquals(Published, KnowledgeToolMetadataService.InputSchemaForModel(Published, Profile with { Encoding = "custom" })));
    }

    [TestMethod]
    [DataRow("""{"request":{"query":{}},"pageSize":"50"}""", "tool_argument_unknown", "$.pageSize")]
    [DataRow("""{"request":{"query":{},"pageSize":"50"}}""", "tool_argument_type_invalid", "$.request.pageSize")]
    [DataRow("""{"request":{"query":"$select=name; $orderby=name desc"}}""", "tool_argument_type_invalid", "$.request.query")]
    [DataRow("""{"request":{"query":{"filter":"status eq 'open'"}}}""", "tool_argument_unknown", "$.request.query.filter")]
    [DataRow("""{"request":{"query":{"$select":50}}}""", "tool_argument_type_invalid", "$.request.query.$select")]
    [DataRow("{}", "tool_argument_required", "$.request")]
    public void ODataPresentationRejectsMalformedModelCalls(string arguments, string code, string path)
    {
        var schema = KnowledgeToolMetadataService.InputSchemaForModel(Published, Profile);
        var error = Assert.Throws<ToolInputValidationException>(() => ToolInputSchemaValidator.Validate(schema, JsonSerializer.Deserialize<JsonElement>(arguments)));
        Assert.AreEqual(code, error.Code);
        Assert.AreEqual(path, error.Path);
    }
}
