using System.Text.Json;
using Agentstration.Schemas;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class DataContractsTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    [TestMethod]
    public void AbsenceNeverBecomesDynamicAndMissingDynamicInputFails()
    {
        var parent = DataContracts.Declared(Json("{\"type\":\"string\"}"), "parent")!;
        Assert.IsNull(DataContracts.Resolve(null, parent, parent, "absent"));
        Assert.IsNull(DataContracts.Resolve(new() { Mode = DataContractMode.Absent }, parent, parent, "absent"));
        var failure = Assert.ThrowsExactly<DataContractException>(() => DataContracts.Resolve(new() { Mode = DataContractMode.Dynamic }, null, parent, "dynamic"));
        Assert.AreEqual("data_contract_unresolved", failure.Code);
        var resolved = DataContracts.Resolve(new() { Mode = DataContractMode.Dynamic }, parent, null, "dynamic")!;
        Assert.AreEqual(parent.Digest, resolved.Digest);
        Assert.AreEqual(DataContractMode.Dynamic, resolved.DeclaredMode);
        Assert.IsFalse(resolved.Validated);
    }

    [TestMethod]
    public void FixedContractDoesNotDependOnParentAndBroadJsonIsExplicit()
    {
        var fixedContract = DataContracts.Resolve(new() { Schema = Json("{}") }, null,
            DataContracts.Declared(Json("{\"type\":\"string\"}"), "parent"), "producer")!;
        DataContracts.Validate(fixedContract, Json("{\"answer\":42}"), "tool-output");
        Assert.AreEqual(DataContractMode.Fixed, fixedContract.DeclaredMode);
        Assert.AreEqual(64, fixedContract.Digest.Length);
    }

    [TestMethod]
    public void NestedFailureKeepsPathAndCauseWithoutCopyingSensitiveValues()
    {
        var contract = DataContracts.Declared(Json("{\"type\":\"object\",\"properties\":{\"rows\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"age\":{\"type\":\"integer\"}}}}}}"), "agent:fixture/output")!;
        var failure = Assert.ThrowsExactly<DataContractException>(() => DataContracts.Validate(contract,
            Json("{\"rows\":[{\"age\":\"private-value@example.test\"}]}"), "agent-output", "child-run", "anonymize"));
        Assert.AreEqual("$.rows[0].age", failure.Failure.Path);
        Assert.AreEqual("integer", failure.Failure.Expected);
        Assert.AreEqual("string", failure.Failure.Actual);
        Assert.AreEqual("child-run", failure.Failure.FlowRunId);
        Assert.DoesNotContain("private-value", failure.Message + JsonSerializer.Serialize(failure.Failure));
    }

    [TestMethod]
    public void TextAndJsonAreDistinctAndInvalidJsonHasSafeActionableDetails()
    {
        var json = DataContracts.Declared(Json("{\"type\":\"object\"}"), "producer", "application/json")!;
        var failure = Assert.ThrowsExactly<DataContractException>(() => DataContracts.ReadText(json, "```json\nprivate payload\n```", "artifact-output", "run", "transform"));
        Assert.AreEqual("data_contract_json_invalid", failure.Code);
        Assert.AreEqual("json_syntax", failure.Failure.Rule);
        Assert.DoesNotContain("private payload", failure.Message);
        var text = DataContracts.Declared(Json("{\"type\":\"string\"}"), "producer", "text/plain")!;
        Assert.AreEqual("hello", DataContracts.ReadText(text, "hello", "artifact-output").GetString());
        StringAssert.Contains(DataContracts.Instruction(text, true), "final chat message is separate");
    }

    [TestMethod]
    [DataRow("{\"type\":[\"string\",\"null\"]}", "null", true)]
    [DataRow("{\"type\":[\"string\",\"null\"]}", "42", false)]
    [DataRow("{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"integer\"}]}", "42", true)]
    [DataRow("{\"oneOf\":[{\"type\":\"number\"},{\"type\":\"integer\"}]}", "42", false)]
    [DataRow("{\"allOf\":[{\"type\":\"integer\"},{\"minimum\":5}]}", "2", false)]
    public void FixedContractsValidateNullableAndComposedToolSchemas(string schema, string value, bool accepted)
    {
        var contract = DataContracts.Declared(Json(schema), "tool/output")!;
        if (accepted) DataContracts.Validate(contract, Json(value), "tool-output");
        else Assert.ThrowsExactly<DataContractException>(() => DataContracts.Validate(contract, Json(value), "tool-output"));
    }

    [TestMethod]
    [DataRow("application/json")]
    [DataRow("text/json; charset=utf-8")]
    [DataRow("application/problem+json")]
    public void JsonMediaTypesParseStructuredContentAndInvalidEncodingHasASafeCause(string mediaType)
    {
        var contract = DataContracts.Declared(Json("{\"type\":\"object\"}"), "artifact", mediaType)!;
        Assert.AreEqual(JsonValueKind.Object, DataContracts.ReadUtf8(contract, "{}"u8, "artifact-publication").ValueKind);
        var error = Assert.ThrowsExactly<DataContractException>(() => DataContracts.ReadUtf8(contract, new byte[] { 0xff }, "artifact-publication", "run", "publish"));
        Assert.AreEqual("data_contract_encoding_invalid", error.Code);
        Assert.AreEqual("publish", error.Failure.StepName);
        Assert.AreEqual("valid UTF-8 content", error.Failure.Expected);
    }

    [TestMethod]
    public void OutputExpectationIsBoundOnlyToAnExplicitParameterAndConflictsFail()
    {
        var contract = DataContracts.Declared(Json("{\"type\":\"string\"}"), "tool/output")!;
        var ordinary = Json("{\"text\":\"fixture\"}");
        Assert.IsTrue(JsonElement.DeepEquals(ordinary, DataContracts.BindOutputParameter(ordinary, Json("{\"type\":\"object\"}"), contract, "tool-input")));
        var inputSchema = Json("{\"type\":\"object\",\"properties\":{\"outputContract\":{\"type\":\"object\"}}}");
        var bound = DataContracts.BindOutputParameter(ordinary, inputSchema, contract, "tool-input");
        Assert.AreEqual("string", bound.GetProperty("outputContract").GetProperty("schema").GetProperty("type").GetString());
        Assert.ThrowsExactly<DataContractException>(() => DataContracts.BindOutputParameter(Json("{\"outputContract\":{}}"), inputSchema, contract, "tool-input"));
        Assert.IsNull(DataContracts.ParseFailure("{\"ordinary\":\"diagnostic\"}"));
    }

    [TestMethod]
    public void ProtectedStringReferencesDeferValueConstraintsWithoutWeakeningOtherFields()
    {
        var schema = Json("{\"type\":\"object\",\"required\":[\"password\",\"label\"],\"properties\":{\"password\":{\"type\":\"string\",\"minLength\":20},\"label\":{\"type\":\"string\",\"minLength\":2}},\"additionalProperties\":false}");
        var contract = DataContracts.Declared(schema, "flow/input");
        var value = Json("{\"password\":{\"opaqueFixtureRef\":true},\"label\":\"ok\"}");
        static bool Opaque(JsonElement item) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("opaqueFixtureRef", out _);
        DataContracts.Validate(contract, value, "flow-input", isProtectedString: Opaque);
        Assert.ThrowsExactly<DataContractException>(() => DataContracts.Validate(contract,
            Json("{\"password\":{\"opaqueFixtureRef\":true},\"label\":\"x\"}"), "flow-input", isProtectedString: Opaque));
        Assert.ThrowsExactly<DataContractException>(() => DataContracts.Validate(contract, value, "flow-input"));
    }

    [TestMethod]
    public void InvalidModesAndUnsupportedDefinitionsAreRejectedBeforeExecution()
    {
        Assert.ThrowsExactly<DataContractException>(() => DataContracts.ValidateDefinition(new() { Mode = DataContractMode.Dynamic, Source = "unknown" }, "fixture"));
        Assert.ThrowsExactly<DataContractException>(() => DataContracts.ValidateDefinition(new() { Mode = DataContractMode.Absent, Schema = Json("{}") }, "fixture"));
        Assert.ThrowsExactly<DataContractException>(() => DataContracts.Declared(Json("{\"$ref\":\"https://unavailable.test/schema\"}"), "fixture"));
    }
}
