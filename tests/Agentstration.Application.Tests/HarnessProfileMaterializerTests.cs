using System.Text.Json;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class HarnessProfileMaterializerTests
{
    [TestMethod]
    public void PinnedCodexFilesRepeatExactlyWithoutPhysicalScopeOrRuntimeCredentials()
    {
        var profile = Profile(); var materializer = new CodexHarnessMaterializer();
        var first = materializer.Build(profile, Request(profile)); var second = materializer.Build(profile, Request(profile));
        Assert.IsTrue(first.IsSuccess, first.ErrorCode); Assert.IsTrue(second.IsSuccess, second.ErrorCode);
        Assert.AreEqual(first.Value!.Digest, second.Value!.Digest);
        CollectionAssert.AreEqual(first.Value.Files.ToArray(), second.Value.Files.ToArray());
        Assert.HasCount(3, first.Value.Files);
        Assert.AreEqual("Implement fixture-project.\n", first.Value.Files.Single(file => file.Path == "AGENTS.md").Content);
        var config = first.Value.Files.Single(file => file.Path == CodexHarnessMaterializer.ConfigurationPath).Content;
        StringAssert.Contains(config, "model = \"fixture-model\""); StringAssert.Contains(config, "network_access = false");
        var provenance = first.Value.Files.Single(file => file.Path == CodexHarnessMaterializer.ProvenancePath).Content;
        Assert.IsFalse(provenance.Contains(profile.ScopeRef!.Value.TargetId!.Value.ToString(), StringComparison.Ordinal));
        Assert.IsFalse(provenance.Contains(profile.NativeConfiguration.Uid.ToString(), StringComparison.Ordinal));
        Assert.AreEqual("1.0.0", first.Value.Provenance.ProfileVersion); Assert.AreEqual("codex", first.Value.Provenance.Materializer);
        Assert.AreEqual(CodexHarnessMaterializer.Version, first.Value.Provenance.MaterializerVersion);
        CollectionAssert.Contains(first.Value.UsageFields.ToArray(), "input_tokens"); CollectionAssert.Contains(first.Value.UnavailableUsageFields.ToArray(), "provider_cost");
    }

    [TestMethod]
    public void ExportRequiresEveryTemplateBindingAndBoundsExpandedInstructions()
    {
        var profile = Profile(); var materializer = new CodexHarnessMaterializer();
        Assert.AreEqual("harness_binding_missing", materializer.Build(profile, Request(profile) with { Bindings = new Dictionary<string, string>() }).ErrorCode);
        Assert.AreEqual("harness_bindings_invalid", materializer.Build(profile, Request(profile) with { Bindings = new Dictionary<string, string> { ["project-name"] = new string('a', 8_001) } }).ErrorCode);
        profile = profile with { Definition = profile.Definition with { Instructions = string.Concat(Enumerable.Repeat("{{project-name}}", 10)) } };
        Assert.AreEqual("harness_instructions_limit", materializer.Build(profile, Request(profile) with { Bindings = new Dictionary<string, string> { ["project-name"] = new string('a', 2_000) } }).ErrorCode);
    }

    [TestMethod]
    [DataRow("../AGENTS.md")]
    [DataRow("/tmp/AGENTS.md")]
    [DataRow("C:/AGENTS.md")]
    [DataRow(".git/config")]
    [DataRow("folder\\AGENTS.md")]
    [DataRow("folder/CON")]
    [DataRow("folder/AUX.txt")]
    public void UnsafePathsNeverBecomeNativeOutput(string path)
    {
        Assert.IsFalse(CodexHarnessMaterializer.SafePath(path));
        var profile = Profile(); profile = profile with { Definition = profile.Definition with { InstructionsPath = path } };
        Assert.IsFalse(new CodexHarnessMaterializer().Build(profile, Request(profile)).IsSuccess);
    }

    [TestMethod]
    public void UnownedFilesSymlinkParentsOverridesAndCaseCollisionsAreExplicitConflicts()
    {
        var profile = Profile(); var materializer = new CodexHarnessMaterializer(); var request = Request(profile);
        var digest = new string('a', 64);
        foreach (var existing in new HarnessExistingFile[][]
        {
            [new("AGENTS.md", digest, null)],
            [new("AGENTS.md", digest, "another-profile")],
            [new("AGENTS.override.md", digest, null)],
            [new("agents.md", digest, null)],
            [new(".codex", null, null, "symlink")],
            [new(".codex/config.toml", digest, null), new(".CODEX/config.toml", digest, null)],
            [new("AGENTS.md/nested", digest, null)]
        }) Assert.IsFalse(materializer.Build(profile, request with { ExistingFiles = existing }).IsSuccess);
        var preview = materializer.Build(profile, request).Value!;
        var owned = preview.Files.Select(file => new HarnessExistingFile(file.Path, file.Digest, file.OwnerProfile)).ToArray();
        Assert.AreEqual(preview.Digest, materializer.Build(profile, request with { ExistingFiles = owned }).Value!.Digest);
        Assert.IsFalse(materializer.Build(profile, request with { ExistingFiles = [owned[0] with { Digest = digest }] }).IsSuccess);
    }

    [TestMethod]
    public void NativeConfigurationRejectsUninstalledMaterializersAndUnsupportedAuthorityModes()
    {
        var native = Profile().NativeConfiguration.Definition;
        Assert.IsTrue(CodexHarnessMaterializer.ValidNative(native));
        Assert.IsFalse(CodexHarnessMaterializer.ValidNative(native with { Materializer = "unknown" }));
        Assert.IsFalse(CodexHarnessMaterializer.ValidNative(native with { MaterializerVersion = "2.0.0" }));
        Assert.IsFalse(CodexHarnessMaterializer.ValidNative(native with { Settings = native.Settings with { SandboxMode = "danger-full-access" } }));
        Assert.IsFalse(CodexHarnessMaterializer.ValidNative(native with { Settings = native.Settings with { Model = "model\napi_key=secret" } }));
    }

    private static HarnessExportRequest Request(HarnessProfileVersionResource profile) => new(default, profile.ProfileName, profile.Version, profile.ContentDigest,
        new Dictionary<string, string> { ["project-name"] = "fixture-project" }, []);
    private static HarnessProfileVersionResource Profile()
    {
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var definition = new HarnessProfileDefinition("Coder", "Implement {{project-name}}.\r\n",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new Dictionary<string, object> { ["project-name"] = new { type = "string", maxLength = 64 } }, required = new[] { "project-name" }, additionalProperties = false }),
            new("native", "1.0.0", default));
        return new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = HarnessResourceKinds.ProfileVersion, ScopeRef = scope, Metadata = new() { Name = "coder.v1.0.0" },
            ProfileName = "coder", ProfileUid = Guid.NewGuid(), ProfileGeneration = 1, Version = "1.0.0", Definition = definition,
            NativeConfiguration = new(scope, default, "native", "1.0.0", Guid.NewGuid(), 1, new string('b', 64), new("codex", "1.0.0", new("fixture-model"))),
            ContentDigest = new string('c', 64), PublishedAt = DateTimeOffset.UnixEpoch
        };
    }
}
