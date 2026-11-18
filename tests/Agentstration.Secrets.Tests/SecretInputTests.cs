using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Secrets.Tests;

[TestClass]
public sealed class SecretInputTests
{
    [TestMethod]
    public void DurableReferencesRoundTripWithoutAValueAndPreserveOpaqueSiblingValues()
    {
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var reference = new SecretInputReference("binding", new(ResourceAddress.Create(default, "Secret", "token"), scope),
            Guid.NewGuid(), new(scope, ResourceAddress.Create(default, "ToolDefinition", "relay.v1.0.0")));
        var symbolic = JsonSerializer.SerializeToElement(new { token = SecretInputs.Marker(reference.Id), literal = "unchanged" });
        var reloaded = JsonSerializer.Deserialize<SecretInputReference[]>(JsonSerializer.Serialize(new[] { reference }))!;
        Assert.IsTrue(SecretInputs.IsReference(symbolic.GetProperty("token"), reloaded));
        Assert.IsFalse(SecretInputs.IsReference(symbolic.GetProperty("token"), []));
        Assert.AreEqual("unchanged", SecretInputs.ForTypeValidation(symbolic, reloaded).GetProperty("literal").GetString());
    }

    [TestMethod]
    public void ProviderEchoesAreReplacedByReferencesAndEmbeddedValuesAreRedacted()
    {
        using var resolved = new ResolvedSecretInputs(JsonSerializer.SerializeToElement(new { token = "synthetic-value" }),
            new Dictionary<string, string> { ["binding"] = "synthetic-value" });
        var output = resolved.Protect(JsonSerializer.SerializeToElement(new
        {
            exact = "synthetic-value",
            embedded = "prefix synthetic-value suffix",
            unaffected = "other"
        }))!.Value;
        Assert.IsFalse(output.GetRawText().Contains("synthetic-value", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(resolved).Contains("synthetic-value", StringComparison.Ordinal));
        Assert.AreEqual("[REDACTED]", resolved.ToString());
        Assert.IsTrue(SecretInputs.TryGetId(output.GetProperty("exact"), out var id));
        Assert.AreEqual("binding", id);
        Assert.AreEqual("prefix [REDACTED] suffix", output.GetProperty("embedded").GetString());
        Assert.AreEqual("other", output.GetProperty("unaffected").GetString());
    }
}
