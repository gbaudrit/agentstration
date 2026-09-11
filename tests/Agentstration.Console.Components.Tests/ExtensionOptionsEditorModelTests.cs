using System.Text.Json;
using Agentstration.Management.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ExtensionOptionsEditorModelTests
{
    [TestMethod]
    public void NewConfigurationUsesPreferredContractVersionAndSchemaDigest()
    {
        var editor = ExtensionOptionsEditorModel.Create("openai", Contract(), null);
        editor.Enabled = true;
        editor.Fields.Single(field => field.Name == "effort").Value = "high";
        editor.Fields.Single(field => field.Name == "parallelCalls").Value = "true";

        using var result = JsonDocument.Parse(editor.ToProviderOptionsJson());
        var options = result.RootElement.GetProperty("openai");

        Assert.AreEqual("model-options", options.GetProperty("optionSet").GetString());
        Assert.AreEqual("2", options.GetProperty("version").GetString());
        Assert.AreEqual("sha256:v2", options.GetProperty("schemaDigest").GetString());
        Assert.AreEqual("high", options.GetProperty("values").GetProperty("effort").GetString());
        Assert.IsTrue(options.GetProperty("values").GetProperty("parallelCalls").GetBoolean());
    }

    [TestMethod]
    public void ExistingConfigurationRemainsPinnedAndPreservesOtherProviders()
    {
        const string json = """
            {
              "openai": {
                "optionSet": "model-options",
                "version": "1",
                "schemaDigest": "sha256:v1",
                "values": { "effort": "medium" }
              },
              "other": { "opaque": true }
            }
            """;

        var editor = ExtensionOptionsEditorModel.Create("openai", Contract(), json);
        editor.Fields.Single().Value = "low";
        using var result = JsonDocument.Parse(editor.ToProviderOptionsJson());

        Assert.AreEqual("1", result.RootElement.GetProperty("openai").GetProperty("version").GetString());
        Assert.AreEqual("sha256:v1", result.RootElement.GetProperty("openai").GetProperty("schemaDigest").GetString());
        Assert.IsTrue(result.RootElement.GetProperty("other").GetProperty("opaque").GetBoolean());
    }

    [TestMethod]
    public void UnknownContractAndMalformedDocumentRequireExplicitRawEditing()
    {
        var unknown = ExtensionOptionsEditorModel.Create(
            "openai",
            Contract(),
            """{ "openai": { "optionSet": "legacy", "version": "1", "values": {} } }""");
        var malformed = ExtensionOptionsEditorModel.Create("openai", Contract(), "[]");

        Assert.IsFalse(unknown.CanGuide);
        Assert.IsFalse(malformed.CanGuide);
        Assert.Throws<ArgumentException>(() => unknown.ToProviderOptionsJson());
    }

    [TestMethod]
    public void DigestMismatchAndUnrepresentedValuesRequireExplicitRawEditing()
    {
        var digestMismatch = ExtensionOptionsEditorModel.Create(
            "openai",
            Contract(),
            """{ "openai": { "optionSet": "model-options", "version": "1", "schemaDigest": "sha256:changed", "values": {} } }""");
        var unknownValue = ExtensionOptionsEditorModel.Create(
            "openai",
            Contract(),
            """{ "openai": { "optionSet": "model-options", "version": "1", "schemaDigest": "sha256:v1", "values": { "removed": true } } }""");

        Assert.IsFalse(digestMismatch.CanGuide);
        Assert.IsFalse(unknownValue.CanGuide);
    }

    [TestMethod]
    public void InvalidStructuredValueIsReportedAsAFormValidationError()
    {
        var editor = ExtensionOptionsEditorModel.Create("openai", Contract(), null);
        editor.Enabled = true;
        editor.Fields.Single(field => field.Name == "effort").Value = "medium";
        editor.Fields.Single(field => field.Name == "metadata").Value = "not-json";

        var exception = Assert.Throws<ArgumentException>(() => editor.ToProviderOptionsJson());

        StringAssert.Contains(exception.Message, "valid JSON");
    }

    private static ExtensionOptionSetResponse Contract() => new(
        "model-options",
        "model-provider",
        "openai",
        "model-profile",
        "2",
        [
            new ExtensionOptionSetVersionResponse("1", "sha256:v1", Schema("""
                {
                  "type": "object",
                  "properties": { "effort": { "type": "string" } }
                }
                """), false),
            new ExtensionOptionSetVersionResponse("2", "sha256:v2", Schema("""
                {
                  "type": "object",
                  "required": ["effort"],
                  "properties": {
                    "effort": { "type": "string", "enum": ["low", "medium", "high"] },
                    "parallelCalls": { "type": "boolean" },
                    "metadata": { "type": "object" }
                  }
                }
                """), false)
        ],
        [new ExtensionOptionMigrationDescriptorResponse("1", "2")]);

    private static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
