using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Tools.Tests;

[TestClass]
public sealed class ToolRelayTests
{
    private static readonly JsonElement Schema = Json("""
        {"type":"object","properties":{"store":{"type":"object","properties":{"id":{"type":"string"},"path":{"type":"string"}},"required":["id","path"],"additionalProperties":false},"optional":{"type":"string"}},"required":["store"],"additionalProperties":false}
        """);

    [TestMethod]
    public void NestedBindingProjectsTheCallerSchemaAndReconstructsTheOriginalContract()
    {
        ToolArgumentBinding[] bindings = [Literal("/store/id", "\"hr\"")];
        var projected = ToolRelayArguments.PublicSchema(Schema, bindings);
        Assert.IsFalse(projected.GetProperty("properties").GetProperty("store").GetProperty("properties").TryGetProperty("id", out _));
        var input = Json("""{"store":{"path":"policy.pdf"}}""");
        ToolInputSchemaValidator.Validate(projected, input);
        var output = ToolRelayArguments.Reconstruct(input, bindings,
            new Dictionary<string, JsonElement> { [bindings[0].Path] = Json("\"hr\"") }, Schema);
        Assert.AreEqual("hr", output.GetProperty("store").GetProperty("id").GetString());
        Assert.AreEqual("policy.pdf", output.GetProperty("store").GetProperty("path").GetString());
    }

    [TestMethod]
    [DataRow("""{"store":{"id":"other","path":"policy.pdf"}}""")]
    [DataRow("""{"store":{"id":null,"path":"policy.pdf"}}""")]
    [DataRow("""{"store":null}""")]
    [DataRow("""{"store":"other"}""")]
    [DataRow("""{"store":{"path":"a","path":"b"}}""")]
    public void CallerCannotReplaceFixedValuesOrTheirParent(string input)
    {
        Assert.Throws<ToolDefinitionValidationException>(() => ToolRelayArguments.ValidateCaller(Json(input), [Literal("/store/id", "\"hr\"")]));
    }

    [TestMethod]
    public void UndeclaredAliasesCannotReplaceHiddenInputsInPermissiveSourceObjects()
    {
        var schema = Json("""{"type":"object","properties":{"store":{"type":"string"},"query":{"type":"string"}},"additionalProperties":true}""");
        ToolArgumentBinding[] bindings = [Literal("/store", "\"hr\"")];
        Assert.Throws<ToolDefinitionValidationException>(() => ToolRelayArguments.Reconstruct(Json("""{"Store":"other"}"""), bindings,
            new Dictionary<string, JsonElement> { ["/store"] = Json("\"hr\"") }, schema));
    }

    [TestMethod]
    public void TypedBindingRejectsMixedValueSources()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ToolArgumentBinding>(
            """{"kind":"literal","Path":"/value","Value":true,"Parameter":{}}"""));
    }

    [TestMethod]
    public void EscapedPropertyNamesAndNullLiteralRemainStructuralJson()
    {
        var schema = Json("""{"type":"object","properties":{"a/b~c":{"type":["null","string"]}},"required":["a/b~c"],"additionalProperties":false}""");
        ToolArgumentBinding[] bindings = [Literal("/a~1b~0c", "null")];
        _ = ToolRelayArguments.PublicSchema(schema, bindings);
        var reconstructed = ToolRelayArguments.Reconstruct(Json("{}"), bindings,
            new Dictionary<string, JsonElement> { [bindings[0].Path] = Json("null") }, schema);
        Assert.AreEqual(JsonValueKind.Null, reconstructed.GetProperty("a/b~c").ValueKind);
    }

    [TestMethod]
    [DataRow("/store", "/store/id")]
    [DataRow("/store/id", "/store/id")]
    public void DuplicateAndParentChildBindingsAreRejected(string first, string second)
    {
        Assert.Throws<ToolDefinitionValidationException>(() => ToolRelayArguments.PublicSchema(Schema, [Literal(first, "{}"), Literal(second, "\"hr\"")]));
    }

    [TestMethod]
    public void AnOptionalObjectWithFixedInputStillRequiresItsRemainingRequiredProperties()
    {
        var schema = Json("""{"type":"object","properties":{"options":{"type":"object","properties":{"fixed":{"type":"boolean"},"required":{"type":"string"}},"required":["required"]}}}""");
        var projected = ToolRelayArguments.PublicSchema(schema, [Literal("/options/fixed", "true")]);
        Assert.Throws<ToolInputValidationException>(() => ToolInputSchemaValidator.Validate(projected, Json("{}")));
    }

    [TestMethod]
    public void UnknownAndInvalidPathsAndUnsupportedCompositionsFailExplicitly()
    {
        foreach (var path in new[] { "", "store/id", "/store/~2id", "/missing" })
            Assert.Throws<ToolDefinitionValidationException>(() => ToolRelayArguments.PublicSchema(Schema, [Literal(path, "true")]));
        var composed = Json("""{"type":"object","properties":{"x":{"type":"string"}},"oneOf":[{}]}""");
        Assert.Throws<ToolDefinitionValidationException>(() => ToolRelayArguments.PublicSchema(composed, [Literal("/x", "\"v\"")]));
    }

    [TestMethod]
    public void ReconstructedInputIsValidatedAgainstTheOriginalSchemaAndBounded()
    {
        ToolArgumentBinding[] bindings = [Literal("/store/id", "\"hr\"")];
        Assert.Throws<ToolDefinitionValidationException>(() => ToolRelayArguments.Reconstruct(Json("{}"), bindings,
            new Dictionary<string, JsonElement> { [bindings[0].Path] = Json("\"hr\"") }, Schema));
        Assert.Throws<ToolDefinitionValidationException>(() => ToolRelayArguments.Reconstruct(Json("""{"store":{"path":"a"}}"""), bindings,
            new Dictionary<string, JsonElement> { [bindings[0].Path] = JsonSerializer.SerializeToElement(new string('a', 65_536)) }, Schema));
    }

    [TestMethod]
    public void ToolDefinitionYamlUsesTheExistingDiscriminatedResourcePattern()
    {
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var resource = new ToolDefinitionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolDefinition,
            Metadata = new() { Name = "relay" },
            ScopeRef = scope,
            Definition = new RelayToolDefinitionProperties
            {
                DisplayName = "Relay",
                Target = new() { Name = "source", ScopeRef = scope, Uid = Guid.NewGuid(), Generation = 7 },
                Bindings = [Literal("/store/id", "\"hr\"")]
            }
        };
        var yaml = ResourceManifestSerializer.ToYaml(resource);
        StringAssert.Contains(yaml, "toolKind: relay");
        var parsed = ResourceManifestSerializer.FromYaml<ToolDefinitionResource>(yaml);
        Assert.IsInstanceOfType<RelayToolDefinitionProperties>(parsed.Definition);
        Assert.IsInstanceOfType<LiteralToolArgumentBinding>(parsed.Definition.Bindings[0]);
        Assert.IsFalse(yaml.Contains("implementation:", StringComparison.Ordinal));
    }

    private static LiteralToolArgumentBinding Literal(string path, string value) => new() { Path = path, Value = Json(value) };
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
