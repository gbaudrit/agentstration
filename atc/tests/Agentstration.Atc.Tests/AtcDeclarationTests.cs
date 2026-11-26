using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Atc.Abstractions;
using Agentstration.Atc.Aep;
using Agentstration.Atc.Client;
using Agentstration.Atc.Server;

namespace Agentstration.Atc.Tests;

[TestClass]
public sealed class AtcDeclarationTests
{
    [TestMethod]
    public void ExistingAepContributionsCarryTheExactContractWithoutAGenericRegistry()
    {
        var tools = AtcAepContributions.Create("tracker", "tracking");
        var manifest = new AepManifest(AepProtocol.Version, new("sample", "Sample", "1.0.0"), new Dictionary<string, AepCapabilityDescriptor>(),
            new([], Tools: tools), new([new("tracking", "/mcp")]));
        Assert.IsEmpty(AtcAepContributions.Validate(manifest));
        Assert.IsEmpty(AepDescriptorValidator.Validate(manifest));
        foreach (var tool in tools) Assert.AreEqual(AtcContract.Id, AtcAepContributions.Read(tool).ContractId);
        Assert.IsNotEmpty(AtcAepContributions.Validate(manifest with { Mcp = new([]) }));
        Assert.IsNotEmpty(AtcAepContributions.Validate(manifest with { Contributions = new([], Tools: tools.Take(3).ToArray()) }));
        Assert.IsNotEmpty(AtcAepContributions.Validate(manifest with { Contributions = new([], Tools: [.. tools, tools[0]]) }));
    }

    [TestMethod]
    [DataRow("2.0")]
    [DataRow("1.-1")]
    [DataRow("1.01")]
    [DataRow("1.0.0")]
    [DataRow("")]
    public void MajorAndPreciseRevisionMustAgree(string version)
    {
        var declaration = AtcSchemas.Declaration() with { Version = version };
        Assert.AreEqual("incompatible_contract", Assert.Throws<AtcException>(() => AtcSchemas.Validate(declaration)).Code);
    }

    [TestMethod]
    public void WireIdentityDoesNotContainProductOrVendorAssemblies()
    {
        foreach (var assembly in new[] { typeof(AtcContract).Assembly, typeof(AtcClient).Assembly, typeof(AtcMcpServer).Assembly })
            Assert.IsFalse(assembly.GetReferencedAssemblies().Any(a => a.Name!.StartsWith("Agentstration.", StringComparison.Ordinal)
                && !a.Name.StartsWith("Agentstration.Atc.", StringComparison.Ordinal)));
        var json = JsonSerializer.Serialize(new AtcTargetRef("authority", "team", "binding", "revision"), AtcContract.Json);
        Assert.IsFalse(json.Contains("repository", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("invalid_request", Assert.Throws<AtcException>(() => AtcValidation.Parse<AtcReadRequest>(
            JsonDocument.Parse("{\"requestId\":\"first\",\"requestId\":\"second\",\"itemId\":\"1\"}").RootElement)).Code);
    }

    [TestMethod]
    public void LimitsMeasureUtf8BytesIncludingExactBoundary()
    {
        using var document = JsonDocument.Parse("{\"text\":\"" + new string('é', 200) + "\"}");
        var raw = document.RootElement;
        var bytes = System.Text.Encoding.UTF8.GetByteCount(raw.GetRawText());
        Assert.IsTrue(raw.GetRawText().Length < bytes);
        AtcValidation.Bytes(raw, bytes);
        Assert.AreEqual("limit_exceeded", Assert.Throws<AtcException>(() => AtcValidation.Bytes(raw, bytes - 1)).Code);
    }

    [TestMethod]
    public void CheckedInSchemasMatchTheExecutableContract()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Atc.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        foreach (var operation in AtcContract.Tools)
        {
            using var input = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "docs", "schemas", operation + ".input.json")));
            using var output = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "docs", "schemas", operation + ".output.json")));
            Assert.IsTrue(JsonElement.DeepEquals(input.RootElement, AtcSchemas.Input(operation)), operation);
            Assert.IsTrue(JsonElement.DeepEquals(output.RootElement, AtcSchemas.Output(operation)), operation);
        }
    }
}
