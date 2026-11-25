using System.Text.Json;
using Agentstration.ResourceManagement;

namespace Agentstration.ResourceManagement.Tests;

[TestClass]
public sealed class StrictYamlTests
{
    [TestMethod]
    public void StrictYamlPreservesLiteralStringsNumbersAndMultilineContent()
    {
        const string yaml = """
            name: "false"
            count: 9007199254740993
            amount: 1234567890.123456789012345678
            text: |
              First line: value
              Deuxième ligne
            """;
        var value = ResourceManifestSerializer.FromYamlStrict<Value>(yaml);
        Assert.AreEqual("false", value.Name);
        Assert.AreEqual(9007199254740993L, value.Count);
        Assert.AreEqual(1234567890.123456789012345678m, value.Amount);
        var roundTrip = ResourceManifestSerializer.FromYamlStrict<Value>(ResourceManifestSerializer.ToYaml(value));
        Assert.AreEqual(value, roundTrip);
    }

    [TestMethod]
    [DataRow("name: first\nname: second")]
    [DataRow("name: first\nunknown: value")]
    [DataRow("name: first\nName: second")]
    [DataRow("name: &a value")]
    [DataRow("name: *a")]
    [DataRow("name: !!str value")]
    [DataRow("name: first\n---\nname: second")]
    [DataRow("? [a, b]\n: value")]
    public void StrictYamlRejectsAmbiguousOrUnsupportedDocuments(string yaml)
    {
        Assert.Throws<Exception>(() => ResourceManifestSerializer.FromYamlStrict<Value>(yaml));
    }

    [TestMethod]
    public void StrictYamlBoundsDepthBeforeConstructingExpandedObjects()
    {
        var yaml = new string('[', 40) + "0" + new string(']', 40);
        Assert.Throws<JsonException>(() => ResourceManifestSerializer.FromYamlStrict<JsonElement>(yaml));
    }

    private sealed record Value(string Name, long Count, decimal Amount, string Text);
}
