using System.Text;
using System.Text.Json;
using Agentstration.Functions;
using Agentstration.Infrastructure;
using Agentstration.Schemas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class XmlContentConfigurationTests
{
    private static readonly JsonElement Rules = JsonSerializer.Deserialize<JsonElement>("""
        {"root":{"path":"/value","type":"object","fields":{"text":{"path":".","type":"string"}}}}
        """);
    private static readonly JsonElement Xsd = JsonSerializer.SerializeToElement("""
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="value" type="xs:string" /></xs:schema>
        """);

    [TestMethod]
    public async Task NativeValidationAndMappingAcceptLargeUtf8ValuesAndEncodedJsonWithoutHidden64KLimits()
    {
        var text = new string('é', 50000);
        var xml = "<value>" + text + "</value>";
        Assert.IsGreaterThan(65536, Encoding.UTF8.GetByteCount(xml));
        var schema = new BuiltinXmlSchemaFormatHandler();
        Assert.IsTrue((await schema.ValidateInstanceAsync(Xsd, JsonSerializer.SerializeToElement(xml), null, default)).IsValid);
        var result = await new XmlToJsonFunction().ExecuteAsync(JsonSerializer.SerializeToElement(new { value = xml, rules = Rules }), default);
        Assert.AreEqual(text, result.GetProperty("text").GetString());
        Assert.IsGreaterThan(65536, JsonSerializer.SerializeToUtf8Bytes(result).Length);
        Assert.IsFalse(Agentstration.Functions.Contracts.FunctionInvocationSchemas.XmlContent.GetProperty("properties").GetProperty("value").TryGetProperty("maxLength", out _));
    }

    [TestMethod]
    public async Task XmlDocumentLimitCountsUtf8BytesAndTheXsdProviderKeepsItsNumericCause()
    {
        var xml = "<value>éé</value>";
        var bytes = Encoding.UTF8.GetByteCount(xml);
        var options = new XmlContentOptions { MaximumDocumentBytes = bytes };
        Assert.AreEqual("éé", BoundedXml.Parse(xml, default, options).Root!.Value);
        var error = Assert.ThrowsExactly<XmlContentLimitException>(() => BoundedXml.Parse(xml, default, options with { MaximumDocumentBytes = bytes - 1 }));
        Assert.AreEqual((long)bytes, error.Limit.Actual);
        Assert.AreEqual((long)bytes - 1, error.Limit.Maximum);
        Assert.AreEqual("utf8Bytes", error.Limit.Unit);
        Assert.AreEqual("Agentstration:XmlContent:MaximumDocumentBytes", error.Limit.Setting);
        var validation = await new BuiltinXmlSchemaFormatHandler(new() { MaximumDocumentBytes = 256 })
            .ValidateInstanceAsync(Xsd, JsonSerializer.SerializeToElement("<value>" + new string('x', 300) + "</value>"), null, default);
        Assert.AreEqual("utf8Bytes", validation.Issues.Single().Limit!.Unit);
    }

    [TestMethod]
    public async Task JsonOutputUsesTheConfiguredFunctionBudgetAtTheExactEncodedBoundary()
    {
        var input = JsonSerializer.SerializeToElement(new { value = "<value>é</value>", rules = Rules });
        var output = await new XmlToJsonFunction().ExecuteAsync(input, default);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(output).Length;
        _ = await new XmlToJsonFunction(functionOptions: new() { MaximumValueBytes = bytes }).ExecuteAsync(input, default);
        var error = await Assert.ThrowsExactlyAsync<DataContractException>(async () =>
            await new XmlToJsonFunction(functionOptions: new() { MaximumValueBytes = bytes - 1 }).ExecuteAsync(input, default));
        Assert.AreEqual("xml_output_limit", error.Code);
        Assert.AreEqual((long)bytes, error.Failure.Limit!.Actual);
        Assert.AreEqual("function-output", error.Failure.Boundary);
        Assert.AreEqual("Agentstration:FunctionContent:MaximumValueBytes", error.Failure.Limit.Setting);
    }

    [TestMethod]
    [DataRow("MaximumDepth", "<a><b><c /></b></a>", "xmlDepth")]
    [DataRow("MaximumNodes", "<a><b /><c /></a>", "xmlNodes")]
    [DataRow("MaximumAttributesPerElement", "<a b='1' c='2' />", "xmlAttributes")]
    public void StructuralCeilingsAreBoundedAndCanBeConfigured(string setting, string xml, string unit)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [setting] = "1" }).Build().Get<XmlContentOptions>()!;
        var error = Assert.ThrowsExactly<XmlContentLimitException>(() => BoundedXml.Parse(xml, default, configuration));
        Assert.AreEqual(unit, error.Limit.Unit);
        Assert.AreEqual(XmlContentOptions.SectionName + ":" + setting, error.Limit.Setting);
        _ = BoundedXml.Parse(xml, default, new());
    }

    [TestMethod]
    [DataRow("MaximumSelectedNodes", "selectedNodes")]
    [DataRow("MaximumArrayItems", "arrayItems")]
    [DataRow("MaximumMappingOperations", "mappingOperations")]
    public async Task MappingWorkLimitsExposeTheirSettingAndCannotBeBypassed(string setting, string unit)
    {
        var options = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [setting] = "1" }).Build().Get<XmlContentOptions>()!;
        var rules = JsonSerializer.Deserialize<JsonElement>("""
            {"root":{"path":"/catalog","type":"object","fields":{"entries":{"path":"entry","type":"array","fields":{"id":{"path":"@id","type":"string"}}}}}}
            """);
        var input = JsonSerializer.SerializeToElement(new { value = "<catalog><entry id='a' /><entry id='b' /></catalog>", rules });
        var error = await Assert.ThrowsExactlyAsync<DataContractException>(async () => await new XmlToJsonFunction(options).ExecuteAsync(input, default));
        Assert.AreEqual(unit, error.Failure.Limit!.Unit);
        Assert.AreEqual(XmlContentOptions.SectionName + ":" + setting, error.Failure.Limit.Setting);
        Assert.HasCount(2, (await new XmlToJsonFunction().ExecuteAsync(input, default)).GetProperty("entries").EnumerateArray().ToArray());
    }

    [TestMethod]
    [DataRow("MaximumDocumentBytes")]
    [DataRow("MaximumDepth")]
    [DataRow("MaximumNodes")]
    [DataRow("MaximumAttributesPerElement")]
    [DataRow("MaximumSelectedNodes")]
    [DataRow("MaximumArrayItems")]
    [DataRow("MaximumMappingOperations")]
    public void InvalidConfigurationFailsBeforeAnyStoreIsOpened(string setting)
    {
        var options = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [setting] = "0" }).Build().Get<XmlContentOptions>()!;
        Assert.ThrowsExactly<InvalidOperationException>(() => new ServiceCollection().AddAgentstration(new AgentstrationCompositionOptions
        { DataDirectory = "unused-invalid-xml-configuration", XmlContent = options }));
    }
    [TestMethod]
    public async Task OperatorsCanAdmitMoreThanTheLegacyArrayAndMappingCeilings()
    {
        var rules = JsonSerializer.Deserialize<JsonElement>("""
            {"root":{"path":"/catalog","type":"object","fields":{"entries":{"path":"entry","type":"array","fields":{"id":{"path":"@id","type":"string"}}}}}}
            """);
        var input = JsonSerializer.SerializeToElement(new { value = "<catalog>" + string.Concat(Enumerable.Repeat("<entry id='x' />", 101)) + "</catalog>", rules });
        var refused = await Assert.ThrowsExactlyAsync<DataContractException>(async () => await new XmlToJsonFunction().ExecuteAsync(input, default));
        Assert.AreEqual("arrayItems", refused.Failure.Limit!.Unit);
        var output = await new XmlToJsonFunction(new() { MaximumArrayItems = 200, MaximumMappingOperations = 100000 })
            .ExecuteAsync(input, default);
        Assert.HasCount(101, output.GetProperty("entries").EnumerateArray().ToArray());
        new XmlContentOptions
        {
            MaximumDocumentBytes = int.MaxValue,
            MaximumDepth = int.MaxValue,
            MaximumNodes = int.MaxValue,
            MaximumAttributesPerElement = int.MaxValue,
            MaximumSelectedNodes = int.MaxValue,
            MaximumArrayItems = int.MaxValue,
            MaximumMappingOperations = int.MaxValue
        }.Validate();
    }

}
