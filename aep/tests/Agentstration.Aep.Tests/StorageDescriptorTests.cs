using System.Text.Json;
using Agentstration.Aep.Abstractions;

namespace Agentstration.Aep.Tests;

[TestClass]
public sealed class StorageDescriptorTests
{
    [TestMethod]
    public void ExactOpaqueStorageContributionRoundTripsWithoutChangingItsDigest()
    {
        var manifest = Manifest(Version());
        var copy = JsonSerializer.Deserialize<AepManifest>(JsonSerializer.Serialize(manifest, AepProtocol.JsonOptions), AepProtocol.JsonOptions)!;
        Assert.IsEmpty(AepStorageDescriptorValidator.Validate(copy));
        var original = manifest.Contributions.StorageTypes!.Single().Versions.Single();
        var version = copy.Contributions.StorageTypes!.Single().Versions.Single();
        Assert.AreEqual(AepStorageProtocol.Digest(original), AepStorageProtocol.Digest(version));
        Assert.IsTrue(version.ConfigurationSchema.GetProperty("properties").TryGetProperty("Backend_KEY", out _));
        Assert.AreNotEqual(AepStorageProtocol.Digest(original), AepStorageProtocol.Digest(original with { Asp = original.Asp with { ProviderVersion = "2.0" } }));
    }

    [TestMethod]
    public void WrongDigestsUnsupportedCapabilitiesAndUndeclaredMcpAreRejected()
    {
        var version = Version();
        Assert.IsNotEmpty(AepStorageDescriptorValidator.Validate(Manifest(version with { SchemaDigest = "sha256:wrong" })));
        Assert.IsNotEmpty(AepStorageDescriptorValidator.Validate(Manifest(version with { Capabilities = version.Capabilities with { StableVersions = true } })));
        Assert.IsNotEmpty(AepStorageDescriptorValidator.Validate(Manifest(version with { Asp = version.Asp with { McpServer = "foreign" } })));
        Assert.IsNotEmpty(AepStorageDescriptorValidator.Validate(Manifest(version with { Limits = version.Limits with { MaximumReadBytes = 65537 } })));
    }

    [TestMethod]
    public void SecuredRequirementsNeedExplicitBoundValueAndSecretCapabilities()
    {
        var manifest = Manifest(Version() with { RequiresConnectedService = true, Requirements = [new("credential", true, AepValueType.Text, AepValueProtection.Secured)] });
        Assert.HasCount(2, AepStorageDescriptorValidator.Validate(manifest));
        var capabilities = new Dictionary<string, AepCapabilityDescriptor>(manifest.Capabilities)
        {
            [AepCapabilityNames.BoundValues] = new(AepProtocol.BoundValuesCapabilityVersion),
            [AepCapabilityNames.SecretAccess] = new(AepProtocol.SecretAccessVersion)
        };
        Assert.IsEmpty(AepStorageDescriptorValidator.Validate(manifest with { Capabilities = capabilities }));
        Assert.IsNotEmpty(AepStorageDescriptorValidator.Validate(Manifest(Version() with { Requirements = [new("connection", true, AepValueType.Text, AepValueProtection.Standard)] })));
    }

    [TestMethod]
    public void InvalidOrOversizedUntrustedListsFailWithoutNullDereferences()
    {
        var manifest = Manifest(Version());
        foreach (var versions in new IReadOnlyList<AepStorageTypeVersion>[] { [null!], Enumerable.Repeat(Version(), 33).ToArray() })
            Assert.IsNotEmpty(AepStorageDescriptorValidator.Validate(manifest with { Contributions = new([], StorageTypes: [manifest.Contributions.StorageTypes!.Single() with { Versions = versions }]) }));
        Assert.IsNotEmpty(AepStorageDescriptorValidator.Validate(manifest with { Contributions = new([], StorageTypes: [null!]) }));
        Assert.IsNotEmpty(AepStorageDescriptorValidator.Validate(Manifest(Version() with { Requirements = [null!] })));
    }

    private static AepManifest Manifest(AepStorageTypeVersion version) => new(AepProtocol.Version,
        new("fixture", "Fixture", "1.0.0"), new Dictionary<string, AepCapabilityDescriptor> { [AepStorageProtocol.Capability] = new(AepStorageProtocol.Version) },
        new([], StorageTypes: [new("objects", "Objects", "1.0.0", [version])]), new([new("content", "/mcp")]));

    private static AepStorageTypeVersion Version()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { Backend_KEY = new { type = "string" } } });
        return new("1.0.0", AepSchemaDigest.Compute(schema), schema, AepSchemaDigest.Compute(schema), schema,
            false, [], new(true, true, true, true), new(100, 65536, 65536), new("asp.content/1", "content", "content", "fixture", "1.0"));
    }
}
