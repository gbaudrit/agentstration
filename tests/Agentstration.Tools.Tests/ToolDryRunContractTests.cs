using System.Text.Json;
using Agentstration.Tools;

namespace Agentstration.Tools.Tests;

[TestClass]
public sealed class ToolDryRunContractTests
{
    [TestMethod]
    public void SupportRequiresBooleanDryRunProperty()
    {
        Assert.IsTrue(ToolDryRunContract.IsSupported(Schema("boolean")));
        Assert.IsFalse(ToolDryRunContract.IsSupported(Schema("string")));
        Assert.IsFalse(ToolDryRunContract.IsSupported(JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })));
    }

    [TestMethod]
    public void EnableAddsOrOverridesDryRun()
    {
        var enabled = ToolDryRunContract.Enable(JsonSerializer.SerializeToElement(new { value = "test", dryRun = false }));

        Assert.AreEqual("test", enabled.GetProperty("value").GetString());
        Assert.IsTrue(enabled.GetProperty("dryRun").GetBoolean());
        Assert.IsTrue(ToolDryRunContract.IsEnabled(enabled));
    }

    private static JsonElement Schema(string type) => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new Dictionary<string, object> { ["dryRun"] = new { type } }
    });
}
