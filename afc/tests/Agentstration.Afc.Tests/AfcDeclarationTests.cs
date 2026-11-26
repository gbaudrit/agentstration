using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Afc.Abstractions;
using Agentstration.Afc.Aep;
using Agentstration.Afc.Client;
using Agentstration.Afc.Server;

namespace Agentstration.Afc.Tests;

[TestClass]
public sealed class AfcDeclarationTests
{
    [TestMethod]
    public void ExistingAepContributionsCarryTheExactContractWithoutAGenericRegistry()
    {
        var tools = AfcAepContributions.Create("forge", "forge");
        var manifest = new AepManifest(AepProtocol.Version, new("sample", "Sample", "1.0.0"), new Dictionary<string, AepCapabilityDescriptor>(),
            new([], Tools: tools), new([new("forge", "/mcp")]));
        Assert.IsEmpty(AfcAepContributions.Validate(manifest));
        Assert.IsEmpty(AepDescriptorValidator.Validate(manifest));
        foreach (var tool in tools) Assert.AreEqual(AfcContract.Id, AfcAepContributions.Read(tool).ContractId);
        Assert.IsNotEmpty(AfcAepContributions.Validate(manifest with { Mcp = new([]) }));
        Assert.IsNotEmpty(AfcAepContributions.Validate(manifest with { Contributions = new([], Tools: tools.Take(4).ToArray()) }));
        Assert.IsNotEmpty(AfcAepContributions.Validate(manifest with { Contributions = new([], Tools: [.. tools, tools[0]]) }));
    }

    [TestMethod]
    [DataRow("2.0")]
    [DataRow("1.-1")]
    [DataRow("1.01")]
    [DataRow("1.0.0")]
    [DataRow("")]
    public void MajorAndPreciseRevisionMustAgree(string version)
    {
        var declaration = AfcSchemas.Declaration() with { Version = version };
        Assert.AreEqual("incompatible_contract", Assert.Throws<AfcException>(() => AfcSchemas.Validate(declaration)).Code);
    }

    [TestMethod]
    public void WireIdentityDoesNotContainProductOrVendorAssemblies()
    {
        foreach (var assembly in new[] { typeof(AfcContract).Assembly, typeof(AfcClient).Assembly, typeof(AfcMcpServer).Assembly })
            Assert.IsFalse(assembly.GetReferencedAssemblies().Any(a => a.Name!.StartsWith("Agentstration.", StringComparison.Ordinal)
                && !a.Name.StartsWith("Agentstration.Afc.", StringComparison.Ordinal)));
        var dependencies = typeof(AfcContract).Assembly.GetReferencedAssemblies();
        Assert.IsFalse(dependencies.Any(a => a.Name!.Contains("Atc", StringComparison.Ordinal)));
        Assert.AreEqual("invalid_request", Assert.Throws<AfcException>(() => AfcValidation.Parse<AfcReadRequest>(
            JsonDocument.Parse("{\"requestId\":\"first\",\"requestId\":\"second\",\"repositoryId\":\"repo\",\"nativeId\":\"1\"}").RootElement)).Code);
    }

    [TestMethod]
    public void LimitsMeasureUtf8BytesIncludingExactBoundary()
    {
        using var document = JsonDocument.Parse("{\"text\":\"" + new string('é', 200) + "\"}");
        var raw = document.RootElement;
        var bytes = System.Text.Encoding.UTF8.GetByteCount(raw.GetRawText());
        Assert.IsTrue(raw.GetRawText().Length < bytes);
        AfcValidation.Bytes(raw, bytes);
        Assert.AreEqual("limit_exceeded", Assert.Throws<AfcException>(() => AfcValidation.Bytes(raw, bytes - 1)).Code);
    }

    [TestMethod]
    public void CheckedInSchemasMatchTheExecutableContract()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Afc.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        foreach (var operation in AfcContract.Tools)
        {
            using var input = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "docs", "schemas", operation + ".input.json")));
            using var output = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "docs", "schemas", operation + ".output.json")));
            Assert.IsTrue(JsonElement.DeepEquals(input.RootElement, AfcSchemas.Input(operation)), operation);
            Assert.IsTrue(JsonElement.DeepEquals(output.RootElement, AfcSchemas.Output(operation)), operation);
        }
    }
}
