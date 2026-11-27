using System.Text.Json;
using Agentstration.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class AdditionalSettingsConfigurationTests
{
    [TestMethod]
    [DataRow("{\"Agentstration\":{\"Bootstrap\":null}}")]
    [DataRow("{\"agentstration\":{\"BOOTSTRAP\":{\"InitialProfiles\":[\"bad\"]}}}")]
    [DataRow("{\"Agentstration:Bootstrap:EnableAdditionalSettingsBootstrap\":true}")]
    [DataRow("{\"Agentstration\":{\"Bootstrap:Path\":\"bad\"}}")]
    [DataRow("{\"Agentstration:Bootstrap\":[]}")]
    [DataRow("{\"Agentstration:Bootstrap\":\"replacement\"}")]
    [DataRow("{\"Agentstration\":{\"AdditionalSettings\":[\"bad\"]}}")]
    [DataRow("{\"AGENTSTRATION:additionalsettings:0\":\"bad\"}")]
    [DataRow("{\"Agentstration:AdditionalSettings\":null}")]
    [DataRow("{\"Agentstration\":{\"AdditionalSettings:0\":\"bad\"}}")]
    public void ReservedKeysAreRejectedBeforeIntegration(string json)
    {
        using var fixture = new Fixture();
        var file = fixture.Write(json);
        using var configuration = fixture.Configuration(file);
        Assert.ThrowsExactly<InvalidOperationException>(() => configuration.LoadAgentstrationAdditionalSettings(fixture.Root));
        Assert.AreEqual("original", configuration["Ordinary"]);
        Assert.IsTrue(configuration.GetValue<bool>("Agentstration:Bootstrap:InitialBootstrapEnabled"));
    }

    [TestMethod]
    public void NativeJsonSemanticsAndOrderedOverlaysRetainEnvironmentAndArgumentPriority()
    {
        using var fixture = new Fixture();
        var first = fixture.Write("{ // native comments and trailing comma\n\"Ordinary\":\"first\",\"Array\":[\"a\",\"b\"],\"Override\":\"file\",}");
        var second = fixture.Write("{\"ordinary\":\"second\",\"Array:0\":\"c\",\"Override\":\"last\"}");
        using var configuration = fixture.Configuration(first, second);
        configuration.AddCommandLine(["--Override=argument"]);
        var snapshot = configuration.LoadAgentstrationAdditionalSettings(fixture.Root);
        Assert.AreEqual("second", configuration["Ordinary"]);
        Assert.AreEqual("c", configuration["Array:0"]);
        Assert.AreEqual("b", configuration["Array:1"]);
        Assert.AreEqual("argument", configuration["Override"]);
        CollectionAssert.AreEqual(new[] { first, second }, snapshot.Files.Select(value => value.Path).ToArray());
        Assert.HasCount(2, configuration.GetSection("Agentstration:AdditionalSettings").Get<string[]>()!);
    }

    [TestMethod]
    public void EnvironmentVariablesOverrideAdditionalJsonWithoutChangingTheFrozenList()
    {
        using var fixture = new Fixture();
        var file = fixture.Write("{\"Ordinary\":\"file\"}");
        var prefix = "AGENTSTRATION_TEST_" + Guid.NewGuid().ToString("N") + "_";
        Environment.SetEnvironmentVariable(prefix + "Ordinary", "environment");
        try
        {
            using var configuration = fixture.Configuration(file);
            configuration.AddEnvironmentVariables(prefix);
            var snapshot = configuration.LoadAgentstrationAdditionalSettings(fixture.Root);
            Assert.AreEqual("environment", configuration["Ordinary"]);
            Assert.AreEqual(file, snapshot.Files.Single().Path);
        }
        finally { Environment.SetEnvironmentVariable(prefix + "Ordinary", null); }
    }

    [TestMethod]
    public void DuplicateIdentityAndInvalidJsonAreExplicitFailures()
    {
        using var fixture = new Fixture();
        var file = fixture.Write("{}");
        using var duplicate = fixture.Configuration(file, file);
        Assert.ThrowsExactly<InvalidOperationException>(() => duplicate.LoadAgentstrationAdditionalSettings(fixture.Root));
        File.WriteAllText(file, "{ invalid }");
        using var invalid = fixture.Configuration(file);
        Assert.ThrowsExactly<InvalidOperationException>(() => invalid.LoadAgentstrationAdditionalSettings(fixture.Root));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "additional-settings-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public string Write(string json)
        {
            var path = Path.Combine(Root, Guid.NewGuid().ToString("D") + ".appsettings.json");
            File.WriteAllText(path, json);
            return path;
        }
        public ConfigurationManager Configuration(params string[] files)
        {
            var configuration = new ConfigurationManager();
            var path = Path.Combine(Root, "base.json");
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Ordinary = "original",
                Agentstration = new { AdditionalSettings = files, Bootstrap = new { InitialBootstrapEnabled = true } }
            }));
            configuration.AddJsonFile(path);
            return configuration;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
