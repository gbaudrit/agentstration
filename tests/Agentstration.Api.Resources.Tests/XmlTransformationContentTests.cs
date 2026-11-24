using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Agentstration.Functions;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class XmlTransformationContentTests : ResourceManagementApiTestBase
{
    [TestMethod]
    [DataRow(false, "Agentstration:XmlContent:MaximumDocumentBytes", "utf8Bytes")]
    [DataRow(true, "Agentstration:FunctionContent:MaximumValueBytes", "jsonBytes")]
    public async Task ExposedFunctionUsesHostSettingsAndPreservesTheLimitCause(bool serializedBudget, string setting, string unit)
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.UseSetting(setting, "512"));
        var context = await GetBootstrapContextAsync(factory);
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        Assert.AreEqual(512, serializedBudget ? factory.Services.GetRequiredService<FunctionContentOptions>().MaximumValueBytes
            : factory.Services.GetRequiredService<XmlContentOptions>().MaximumDocumentBytes);
        await RssTransformationTestData.CreateAsync(factory.Services, owner);
        var functions = factory.Services.GetRequiredService<TransformationFunctionService>();
        var function = await functions.ResolveAsync(new("rss-2-0-xml-to-json", "1.0.0", owner), owner, default);
        var document = XDocument.Parse(RssTransformationTestData.Feed(1));
        document.Root!.Element("channel")!.Element("item")!.Element("title")!.Value = new string('é', 1000);
        var xml = document.ToString();
        var input = JsonSerializer.SerializeToElement(new { value = xml });
        var error = await Assert.ThrowsExactlyAsync<DataContractException>(() => functions.InvokeAsync(function, owner, input, default));
        Assert.AreEqual(unit, error.Failure.Limit!.Unit);
        Assert.AreEqual(setting, error.Failure.Limit.Setting);
        Assert.AreEqual(512L, error.Failure.Limit.Maximum);
        Assert.AreEqual(serializedBudget ? JsonSerializer.SerializeToUtf8Bytes(input).LongLength : Encoding.UTF8.GetByteCount(xml), error.Failure.Limit.Actual);
        Assert.AreEqual("function-input", error.Failure.Boundary);
        Assert.AreEqual(serializedBudget ? "$" : "$.value", error.Failure.Path);
        Assert.AreEqual("1.0.0", function.Reference.Version);
        Assert.DoesNotContain(new string('é', 20), error.Message + JsonSerializer.Serialize(error.Failure));
    }
    [TestMethod]
    public async Task InvalidXmlCarriesTheExactAcceptedSchemaAndAnActionableCause()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        await RssTransformationTestData.CreateAsync(factory.Services, owner);
        var functions = factory.Services.GetRequiredService<TransformationFunctionService>();
        var function = await functions.ResolveAsync(new("rss-2-0-xml-to-json", "1.0.0", owner), owner, default);
        var xml = "<rss version='2.0'>\n<channel><unexpected>private-value</unexpected></channel></rss>";
        var error = await Assert.ThrowsExactlyAsync<DataContractException>(() => functions.InvokeAsync(function, owner,
            JsonSerializer.SerializeToElement(new { value = xml }), default));
        Assert.AreEqual("transformation_input_invalid", error.Code);
        var diagnostic = error.Failure.Xml!;
        Assert.AreEqual("contract", diagnostic.Kind);
        Assert.Contains("unexpected", diagnostic.NodePath!);
        Assert.AreEqual("rss-2-0-xml", diagnostic.Schema!.Name);
        Assert.AreEqual("1.0.0", diagnostic.Schema.Version);
        Assert.AreEqual(function.Transformation!.Input.DefinitionHash, diagnostic.Schema.DefinitionHash);
        var details = JsonSerializer.Serialize(new { contractFailure = error.Failure });
        Assert.IsTrue(details.Length < 8192);
        Assert.AreEqual(diagnostic, DataContracts.ParseFailure(details)!.Xml);
        Assert.DoesNotContain("private-value", details);
    }

}
