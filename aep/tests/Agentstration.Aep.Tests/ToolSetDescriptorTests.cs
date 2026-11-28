using System.Text.Json;
using Agentstration.Aep.Abstractions;

namespace Agentstration.Aep.Tests;

[TestClass]
public sealed class ToolSetDescriptorTests
{
    [TestMethod]
    public void ToolSetManifestRoundTripsAndReferencesOnlyItsOwnAdvertisedTools()
    {
        var manifest = Manifest(new AepToolSetContribution("tools", "Git tools", "1.0.0", [new("git.repository", "git.repository")]));
        var copy = JsonSerializer.Deserialize<AepManifest>(JsonSerializer.Serialize(manifest, AepProtocol.JsonOptions), AepProtocol.JsonOptions)!;
        Assert.IsEmpty(AepDescriptorValidator.Validate(copy));
        Assert.AreEqual("git.repository", copy.Contributions.ToolSets!.Single().Members.Single().ToolId);
        Assert.IsNotEmpty(AepDescriptorValidator.Validate(manifest with { Capabilities = new Dictionary<string, AepCapabilityDescriptor>() }));
        Assert.IsNotEmpty(AepDescriptorValidator.Validate(manifest with { Capabilities = new Dictionary<string, AepCapabilityDescriptor> { [AepToolSetDescriptorValidator.Capability] = new("2.0") } }));
    }

    [TestMethod]
    public void UntrustedGroupsRejectUnknownToolsDuplicateRoutesInvalidNamesAndOversizedLists()
    {
        var good = new AepToolSetContribution("tools", "Tools", "1.0.0", [new("git.repository", "git.repository")]);
        foreach (var invalid in new[]
        {
            good with { Id = "../other" }, good with { Version = "../1" }, good with { DisplayName = "" },
            good with { Members = [] }, good with { Members = [null!] },
            good with { Members = [new("other.registration.tool", "read")] },
            good with { Members = [new("git.repository", "read"), new("git.repository", "read")] },
            good with { Members = Enumerable.Repeat(good.Members.Single(), 129).ToArray() }
        }) Assert.IsNotEmpty(AepDescriptorValidator.Validate(Manifest(invalid)));
        Assert.IsNotEmpty(AepDescriptorValidator.Validate(Manifest(good, good)));
        Assert.IsNotEmpty(AepDescriptorValidator.Validate(Manifest(Enumerable.Repeat(good, 33).ToArray())));
        Assert.IsNotEmpty(AepDescriptorValidator.Validate(Manifest(new AepToolSetContribution[] { null! })));
    }

    private static AepManifest Manifest(params AepToolSetContribution[] groups) => new(AepProtocol.Version,
        new("example.git", "Git", "1.0.0"),
        new Dictionary<string, AepCapabilityDescriptor> { [AepToolSetDescriptorValidator.Capability] = new("1.0") },
        new([], Tools: [new("git.repository", "Git repository", new("git", "git_repository"))], ToolSets: groups),
        new([new("git", "/mcp")]));
}
