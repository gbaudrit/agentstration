using System.Text.Json;
using Agentstration.Tools;

namespace Agentstration.Tools.Tests;

public sealed partial class ToolRelayTests
{
    private static readonly JsonElement Enveloped = Json("""
        {"type":"object","properties":{"source":{"type":"string"},"request":{"type":"object","properties":{
          "profile":{"type":"string"},"snapshotId":{"type":["string","null"]},"pageSize":{"type":"integer"},
          "query":{"type":"object"},"cursor":{"type":"string"}},"required":["profile"],"additionalProperties":false}},
        "required":["source","request"],"additionalProperties":false}
        """);
    private static readonly ToolArgumentBinding[] EnvelopeBindings = [Literal("/source", "\"fixed-source\""), Literal("/request/profile", "\"fixed-profile\"")];

    [TestMethod]
    [DataRow("{}")]
    [DataRow("""{"snapshotId":"snapshot-returned","pageSize":50,"query":{"$filter":"status eq 'open'"}}""")]
    [DataRow("""{"cursor":"returned-cursor"}""")]
    public void FlatRelayRestoresTheNativeEnvelopeAndFixedBindings(string json)
    {
        var before = Enveloped.GetRawText();
        var schema = ToolRelayArguments.PublicSchema(Enveloped, EnvelopeBindings, "request");
        Assert.IsFalse(schema.GetProperty("properties").TryGetProperty("request", out _));
        Assert.IsFalse(schema.GetProperty("properties").TryGetProperty("profile", out _));
        Assert.IsEmpty(schema.GetProperty("required").EnumerateArray());
        var input = Json(json);
        ToolInputSchemaValidator.Validate(schema, input);
        var values = EnvelopeBindings.Cast<LiteralToolArgumentBinding>().ToDictionary(binding => binding.Path, binding => binding.Value);
        var output = ToolRelayArguments.Reconstruct(input, EnvelopeBindings, values, Enveloped, inputEnvelope: "request");
        Assert.AreEqual("fixed-source", output.GetProperty("source").GetString());
        Assert.AreEqual("fixed-profile", output.GetProperty("request").GetProperty("profile").GetString());
        foreach (var property in input.EnumerateObject())
            Assert.IsTrue(JsonElement.DeepEquals(property.Value, output.GetProperty("request").GetProperty(property.Name)));
        Assert.AreEqual(before, Enveloped.GetRawText());
        Assert.AreEqual(json, input.GetRawText());
    }

    [TestMethod]
    [DataRow("""{"request":{"query":{}}}""")]
    [DataRow("""{"profile":"other"}""")]
    [DataRow("""{"source":"other"}""")]
    [DataRow("""{"pageSize":"50"}""")]
    [DataRow("""{"pageSize":1,"pageSize":2}""")]
    public void FlatRelayRejectsOldWrappersOverridesDuplicatesAndInvalidTypes(string json)
    {
        var values = EnvelopeBindings.Cast<LiteralToolArgumentBinding>().ToDictionary(binding => binding.Path, binding => binding.Value);
        Assert.Throws<ToolDefinitionValidationException>(() => ToolRelayArguments.Reconstruct(Json(json), EnvelopeBindings, values, Enveloped, inputEnvelope: "request"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("missing")]
    [DataRow("source")]
    public void FlatteningRequiresOneRemainingObjectProperty(string envelope)
    {
        Assert.Throws<ToolDefinitionValidationException>(() => ToolRelayArguments.PublicSchema(Enveloped, EnvelopeBindings, envelope));
        Assert.Throws<ToolDefinitionValidationException>(() => ToolRelayArguments.PublicSchema(Enveloped, [], "request"));
    }
}
