using Agentstration.Web.Components;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class SecretPickerTests
{
    [TestMethod]
    public void PickerIdentityIncludesNamespaceWithoutSecretValue()
    {
        var item = new SecretPickerItem("openai-key", "default", "OpenAI API Key", "local", "Missing");

        Assert.AreEqual("default:openai-key", item.Id);
        Assert.IsFalse(typeof(SecretPickerItem).GetProperties().Any(value => value.Name.Contains("Value", StringComparison.OrdinalIgnoreCase)));
    }
}
