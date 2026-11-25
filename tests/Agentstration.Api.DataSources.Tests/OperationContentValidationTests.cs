using System.Text.Json;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class OperationContentValidationTests
{
    [TestMethod]
    [DataRow("<root>valid</root>", true)]
    [DataRow("<wrong/>", false)]
    [DataRow("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><root>&x;</root>", false)]
    public async Task XmlAdoptionValidatesNativeContentRatherThanTheJsonEnvelope(string xml, bool valid)
    {
        var schema = JsonSerializer.SerializeToElement("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="root" type="xs:string"/></xs:schema>""");
        var snapshot = new SchemaBindingSnapshot("operation.content", BuiltinXmlSchemaFormatHandler.Format, BuiltinXmlSchemaFormatHandler.Dialect,
            schema, JsonSchemaSelection.DefinitionHash(BuiltinXmlSchemaFormatHandler.Format, BuiltinXmlSchemaFormatHandler.Dialect, schema));
        var response = JsonSerializer.SerializeToElement(new { mediaType = "application/xml; charset=utf-8", content = xml });
        var result = await OperationContentValidation.ValidateAsync(new("application/xml", "/content", snapshot), response,
            new SchemaFormatRegistry([new BuiltinXmlSchemaFormatHandler()]), default);
        Assert.AreEqual(valid, result.IsValid);
    }
}
