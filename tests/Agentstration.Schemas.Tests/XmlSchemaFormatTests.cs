using System.Text.Json;
using Agentstration.Schemas;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class XmlSchemaFormatTests
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement("""
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="rss" type="xs:int" /></xs:schema>
        """);

    [TestMethod]
    [DataRow("<rss>42</rss>", true)]
    [DataRow("<rss>bad</rss>", false)]
    [DataRow("<rss />", false)]
    [DataRow("<other>42</other>", false)]
    [DataRow("<rss>", false)]
    [DataRow("<!DOCTYPE rss [<!ENTITY e SYSTEM 'https://example.test/secret'>]><rss>&e;</rss>", false)]
    public async Task XsdActuallyValidatesNativeXmlContent(string xml, bool valid)
    {
        var handler = new BuiltinXmlSchemaFormatHandler();
        Assert.IsTrue((await handler.ValidateDefinitionAsync(Schema, null, default)).IsValid);
        Assert.AreEqual(valid, (await handler.ValidateInstanceAsync(Schema, JsonSerializer.SerializeToElement(xml), null, default)).IsValid);
    }

    [TestMethod]
    public async Task StandaloneXsdRejectsExternalCompositionAndBoundViolations()
    {
        var handler = new BuiltinXmlSchemaFormatHandler();
        foreach (var content in new[]
        {
            "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'><xs:import namespace='remote' schemaLocation='https://example.test/schema' /></xs:schema>",
            "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'><xs:include schemaLocation='file:///etc/passwd' /></xs:schema>",
            "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'><xs:element name='x' type='xs:missing' /></xs:schema>",
            "<not-a-schema />"
        }) Assert.IsFalse((await handler.ValidateDefinitionAsync(JsonSerializer.SerializeToElement(content), null, default)).IsValid);
        foreach (var xml in new[] { new string('x', 65_537), string.Concat(Enumerable.Repeat("<x>", 34)) + string.Concat(Enumerable.Repeat("</x>", 34)), "<rss " + string.Join(' ', Enumerable.Range(0, 65).Select(i => $"a{i}='x'")) + ">42</rss>" })
            Assert.IsFalse((await handler.ValidateInstanceAsync(Schema, JsonSerializer.SerializeToElement(xml), null, default)).IsValid);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await handler.ValidateDefinitionAsync(Schema, null, cancelled.Token));
    }
}
