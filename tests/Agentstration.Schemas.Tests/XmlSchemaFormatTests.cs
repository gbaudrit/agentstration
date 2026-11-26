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
    [TestMethod]
    [DataRow("<rss>private-value</rss>", "contract", true)]
    [DataRow("<rss>\n<unclosed></rss>", "syntax", true)]
    [DataRow("<!DOCTYPE rss [<!ENTITY e SYSTEM 'https://private.example/secret'>]><rss>&e;</rss>", "syntax", false)]
    public async Task InvalidInstancesKeepSafeLocationsAndExpectedTypesWithoutEchoingValues(string xml, string kind, bool hasLocation)
    {
        var result = await new BuiltinXmlSchemaFormatHandler().ValidateInstanceAsync(Schema, JsonSerializer.SerializeToElement(xml), null, default);
        Assert.IsFalse(result.IsValid);
        var diagnostic = result.Issues[0].Xml!;
        Assert.AreEqual(kind, diagnostic.Kind);
        if (hasLocation)
        { Assert.IsTrue(diagnostic.Line > 0); Assert.IsTrue(diagnostic.Column > 0); }
        else Assert.IsNull(diagnostic.Line);
        if (kind == "contract")
        {
            Assert.AreEqual("/rss[1]", diagnostic.NodePath);
            Assert.AreEqual("xs:int", diagnostic.ExpectedType);
        }
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("private-value", serialized);
        Assert.DoesNotContain("private.example", serialized);
        Assert.IsTrue(diagnostic.IsValid);
    }

    [TestMethod]
    public async Task UnexpectedChildrenIdentifyTheNodeAndDeclaredSequence()
    {
        var xsd = JsonSerializer.SerializeToElement("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="order"><xs:complexType><xs:sequence>
            <xs:element name="id" type="xs:int"/><xs:element name="name" type="xs:string"/>
            </xs:sequence></xs:complexType></xs:element></xs:schema>
            """);
        var result = await new BuiltinXmlSchemaFormatHandler().ValidateInstanceAsync(xsd,
            JsonSerializer.SerializeToElement("<order>\n<unexpected>private-value</unexpected>\n</order>"), null, default);
        var diagnostic = result.Issues[0].Xml!;
        Assert.AreEqual("contract", diagnostic.Kind);
        Assert.Contains("unexpected", diagnostic.NodePath!);
        Assert.Contains("id", diagnostic.ExpectedStructure!);
        Assert.Contains("name", diagnostic.ExpectedStructure!);
        Assert.AreEqual(2, diagnostic.Line);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(result));
    }

}
