using System.Text;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Tools.SourceRegistry;

namespace Agentstration.Tools.SourceRegistry.Tests;

[TestClass]
public sealed class SourceRegistryCliTests
{
    [TestMethod]
    public async Task DigestAndValidationUseTheCanonicalReaderAsync()
    {
        using var files = new TemporaryDirectory("source registry tool tests");
        var path = files.Write("source manifest.yaml", Manifest());
        var expected = new SourceManifestReader().Read(Manifest()).Digest;

        var digest = await RunAsync(["source", "digest", path]);
        var validation = await RunAsync(["source", "validate", path]);

        Assert.AreEqual(SourceRegistryCli.SuccessExitCode, digest.ExitCode);
        Assert.AreEqual(expected, digest.Output.Trim());
        Assert.AreEqual(string.Empty, digest.Error);
        Assert.AreEqual(SourceRegistryCli.SuccessExitCode, validation.ExitCode);
        StringAssert.Contains(validation.Output, "valid SourceVersion agentstration/official-samples@1");
        StringAssert.Contains(validation.Output, expected);
    }

    [TestMethod]
    public async Task CanonicalDigestIgnoresPropertyOrderCommentsAndWhitespaceAsync()
    {
        using var files = new TemporaryDirectory("canonical source tests");
        var first = files.Write("first.yaml", Manifest());
        var second = files.Write("second.yaml", """
            # Publication comment
            definition:
              catalogs: []
              channels: []
              bindings: []
              publisher: { name: agentstration }
              description: null
              displayName: "Échantillon"
              version: "1"
            metadata: { name: official-samples }
            kind: SourceVersion
            apiVersion: agentstration.io/v1
            """);

        var firstResult = await RunAsync(["source", "digest", first]);
        var secondResult = await RunAsync(["source", "digest", second]);

        Assert.AreEqual(SourceRegistryCli.SuccessExitCode, firstResult.ExitCode);
        Assert.AreEqual(firstResult.Output, secondResult.Output);
    }

    [TestMethod]
    public async Task ValuesRetainCanonicalYamlScalarTypesAsync()
    {
        using var files = new TemporaryDirectory("typed values tests");
        var path = files.Write("typed.yaml", Manifest(includeChannel: true));

        var result = await RunAsync(["source", "validate", path]);

        Assert.AreEqual(SourceRegistryCli.SuccessExitCode, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
    }

    [TestMethod]
    public async Task InvalidAndMultipleDocumentsReturnPathQualifiedDiagnosticsAsync()
    {
        using var files = new TemporaryDirectory("invalid source tests");
        var invalid = files.Write("invalid.yaml", Manifest().Replace("name: agentstration", "name: Agentstration", StringComparison.Ordinal));
        var multiple = files.Write("multiple.yaml", Manifest() + Environment.NewLine + "---" + Environment.NewLine + "{}");

        var invalidResult = await RunAsync(["source", "validate", invalid]);
        var multipleResult = await RunAsync(["source", "digest", multiple]);

        Assert.AreEqual(SourceRegistryCli.ValidationExitCode, invalidResult.ExitCode);
        StringAssert.Contains(invalidResult.Error, "invalid.yaml: source_identity_invalid:");
        Assert.AreEqual(SourceRegistryCli.ValidationExitCode, multipleResult.ExitCode);
        StringAssert.Contains(multipleResult.Error, "multiple.yaml: source_manifest_document_count:");
        Assert.AreEqual(string.Empty, invalidResult.Output);
        Assert.AreEqual(string.Empty, multipleResult.Output);
    }

    [TestMethod]
    public async Task DuplicateBindingIsRejectedByTheSharedValidatorAsync()
    {
        using var files = new TemporaryDirectory("duplicate source tests");
        var duplicate = files.Write("duplicate.yaml", Manifest(includeChannel: true).Replace(
            "bindings:\n    - name: distribution\n      targetKind: sourceProvider",
            "bindings:\n    - name: distribution\n      targetKind: sourceProvider\n    - name: distribution\n      targetKind: sourceProvider",
            StringComparison.Ordinal));

        var result = await RunAsync(["source", "validate", duplicate]);

        Assert.AreEqual(SourceRegistryCli.ValidationExitCode, result.ExitCode);
        StringAssert.Contains(result.Error, "source_binding_duplicate");
    }

    [TestMethod]
    public async Task MissingInvalidUtf8AndCancelledInputsHaveStableExitCodesAsync()
    {
        using var files = new TemporaryDirectory("input source tests");
        var invalidUtf8 = files.WriteBytes("invalid-utf8.yaml", [0xff, 0xfe, 0xfd]);
        var missing = Path.Combine(files.Path, "missing.yaml");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var invalidResult = await RunAsync(["source", "validate", invalidUtf8]);
        var missingResult = await RunAsync(["source", "validate", missing]);
        var cancelledResult = await RunAsync(["source", "validate", missing], cancellation.Token);

        Assert.AreEqual(SourceRegistryCli.ValidationExitCode, invalidResult.ExitCode);
        StringAssert.Contains(invalidResult.Error, "source_manifest_encoding_invalid");
        Assert.AreEqual(SourceRegistryCli.InputExitCode, missingResult.ExitCode);
        StringAssert.Contains(missingResult.Error, "source_manifest_input_error");
        Assert.AreEqual(SourceRegistryCli.CancelledExitCode, cancelledResult.ExitCode);
        StringAssert.Contains(cancelledResult.Error, "operation_cancelled");
    }

    [TestMethod]
    public async Task OversizedInputIsRejectedBeforeParsingAsync()
    {
        using var files = new TemporaryDirectory("oversized source tests");
        var oversized = files.WriteBytes(
            "oversized.yaml",
            new byte[SourceManifestReader.MaximumManifestBytes + 1]);

        var result = await RunAsync(["source", "validate", oversized]);

        Assert.AreEqual(SourceRegistryCli.ValidationExitCode, result.ExitCode);
        StringAssert.Contains(result.Error, "source_manifest_size_limit");
        Assert.AreEqual(string.Empty, result.Output);
    }

    [TestMethod]
    public async Task RegistryValidateAndBuildVerifyAndPublishTheExactAllowlistedTreeAsync()
    {
        using var files = new TemporaryDirectory("registry publication tests");
        var input = files.CreateDirectory("input root");
        var sourcePath = files.WriteRelative(input, "sources/agentstration/official-samples/1/source.yaml", Manifest());
        var digest = new SourceManifestReader().Read(Manifest()).Digest;
        var registryPath = files.WriteRelative(input, "registry.yaml", Registry(digest));
        files.WriteRelative(input, "not-published.txt", "must not be copied");
        var output = Path.Combine(files.Path, "output root");

        var validation = await RunAsync(RegistryArguments("validate", registryPath, input));
        var build = await RunAsync(RegistryArguments("build", registryPath, input, output));

        Assert.AreEqual(SourceRegistryCli.SuccessExitCode, validation.ExitCode);
        StringAssert.Contains(validation.Output, "valid SourceRegistry agentstration-official sha256:");
        StringAssert.Contains(validation.Output, "publishers=1 sources=1 versions=1");
        Assert.AreEqual(SourceRegistryCli.SuccessExitCode, build.ExitCode);
        StringAssert.Contains(build.Output, "built SourceRegistry agentstration-official sha256:");
        Assert.IsTrue(File.Exists(Path.Combine(output, "registry.json")));
        Assert.IsTrue(File.Exists(Path.Combine(output, "registry.sha256")));
        CollectionAssert.AreEqual(File.ReadAllBytes(sourcePath), File.ReadAllBytes(Path.Combine(output, "sources", "agentstration", "official-samples", "1", "source.yaml")));
        Assert.IsFalse(File.Exists(Path.Combine(output, "not-published.txt")));
        var publishedFiles = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(output, path)).ToArray();
        Assert.HasCount(3, publishedFiles);
        var registryJson = File.ReadAllBytes(Path.Combine(output, "registry.json"));
        Assert.IsFalse(registryJson.AsSpan().EndsWith("\n"u8));
        var registryDigest = File.ReadAllText(Path.Combine(output, "registry.sha256"));
        StringAssert.Matches(registryDigest, new("^sha256:[a-f0-9]{64}\\n$"));
        using var document = JsonDocument.Parse(registryJson);
        Assert.AreEqual("SourceRegistry", document.RootElement.GetProperty("kind").GetString());
    }

    [TestMethod]
    public async Task RegistryValidationRejectsMissingDigestIdentityVersionAndLatestMismatchesAsync()
    {
        using var files = new TemporaryDirectory("registry mismatch tests");
        var input = files.CreateDirectory("input");
        var sourceRelative = "sources/agentstration/official-samples/1/source.yaml";
        files.WriteRelative(input, sourceRelative, Manifest());
        var digest = new SourceManifestReader().Read(Manifest()).Digest;
        var cases = new[]
        {
            ("missing", Registry(digest, "sources/agentstration/official-samples/2/source.yaml"), "source_registry_manifest_missing"),
            ("digest", Registry("sha256:" + new string('0', 64)), "source_registry_manifest_digest_mismatch"),
            ("identity", Registry(digest).Replace("name: official-samples", "name: another-source", StringComparison.Ordinal), "source_registry_manifest_identity_mismatch"),
            ("version", Registry(digest).Replace("latest: \"1\"", "latest: \"2\"", StringComparison.Ordinal).Replace("version: \"1\"", "version: \"2\"", StringComparison.Ordinal), "source_registry_manifest_version_mismatch"),
            ("latest", Registry(digest).Replace("latest: \"1\"", "latest: \"missing\"", StringComparison.Ordinal), "source_registry_latest_invalid")
        };

        foreach (var (name, registry, code) in cases)
        {
            var registryPath = files.WriteRelative(input, "registry.yaml", registry);
            var result = await RunAsync(RegistryArguments("validate", registryPath, input));
            Assert.AreEqual(SourceRegistryCli.ValidationExitCode, result.ExitCode, name);
            StringAssert.Contains(result.Error, code, name);
        }
    }

    [TestMethod]
    public async Task RegistryValidationRejectsUnsafeAndAmbiguousPublicationPathsAsync()
    {
        using var files = new TemporaryDirectory("registry path tests");
        var input = files.CreateDirectory("input");
        var digest = "sha256:" + new string('0', 64);
        var cases = new[]
        {
            "../source.yaml",
            "/source.yaml",
            "sources//source.yaml",
            "sources/%2e%2e/source.yaml",
            "sources\\source.yaml",
            "https://other.example/source.yaml",
            "https://example.test/sources/source.yaml?raw=1"
        };

        foreach (var manifestUrl in cases)
        {
            var registryPath = files.WriteRelative(input, "registry.yaml", Registry(digest, manifestUrl));
            var result = await RunAsync(RegistryArguments("validate", registryPath, input));
            Assert.AreEqual(SourceRegistryCli.ValidationExitCode, result.ExitCode, manifestUrl);
            StringAssert.Contains(result.Error, "source_registry_manifest_", manifestUrl);
        }
    }

    [TestMethod]
    public void RegistryReaderRejectsUnknownNullDuplicateAndUnsupportedYamlFeatures()
    {
        var reader = new SourceRegistryReader();
        var digest = "sha256:" + new string('0', 64);
        var yaml = Registry(digest);
        var json = """
            {"apiVersion":"agentstration.io/v1","kind":"SourceRegistry","metadata":{"name":"registry","name":"duplicate"},"definition":{"publishers":[],"sources":[]}}
            """;

        Assert.AreEqual("source_registry_property_duplicate", Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(json, "registry.json")).Code);
        Assert.AreEqual("source_registry_null_invalid", Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(yaml.Replace("displayName: Agentstration Registry", "displayName: null", StringComparison.Ordinal), "registry.yaml")).Code);
        Assert.AreEqual("source_registry_invalid", Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(yaml.Replace("kind: SourceRegistry", "kind: SourceRegistry\nunknown: value", StringComparison.Ordinal), "registry.yaml")).Code);
        Assert.AreEqual("source_registry_yaml_feature_invalid", Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(yaml.Replace("name: agentstration", "name: &publisher agentstration", StringComparison.Ordinal), "registry.yaml")).Code);
        Assert.AreEqual("source_registry_document_count", Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(yaml + "\n---\n{}", "registry.yaml")).Code);
    }

    [TestMethod]
    public void RegistryReaderRejectsDuplicateSourceAndVersionIdentities()
    {
        var digest = "sha256:" + new string('0', 64);
        var version = $"{{\"version\":\"1\",\"manifestUrl\":\"source.yaml\",\"manifestDigest\":\"{digest}\"}}";
        var source = $"{{\"publisher\":\"agentstration\",\"name\":\"sample\",\"versions\":[{version}]}}";
        var envelope = "{\"apiVersion\":\"agentstration.io/v1\",\"kind\":\"SourceRegistry\",\"metadata\":{\"name\":\"registry\"},\"definition\":{\"publishers\":[{\"name\":\"agentstration\",\"status\":\"Official\"}],\"sources\":SOURCES}}";
        var reader = new SourceRegistryReader();

        var duplicateSource = envelope.Replace("SOURCES", $"[{source},{source}]", StringComparison.Ordinal);
        var duplicateVersionSource = $"{{\"publisher\":\"agentstration\",\"name\":\"sample\",\"versions\":[{version},{version}]}}";
        var duplicateVersion = envelope.Replace("SOURCES", $"[{duplicateVersionSource}]", StringComparison.Ordinal);

        Assert.AreEqual("source_registry_source_duplicate", Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(duplicateSource, "registry.json")).Code);
        Assert.AreEqual("source_registry_version_duplicate", Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(duplicateVersion, "registry.json")).Code);
    }

    [TestMethod]
    public void RegistryCanonicalizationNormalizesArrayOrderAcrossYamlAndJson()
    {
        var reader = new SourceRegistryReader();
        var first = """
            apiVersion: agentstration.io/v1
            kind: SourceRegistry
            metadata: { name: registry }
            definition:
              publishers:
                - { name: zed, status: Declared }
                - { name: alpha, status: Official }
              sources: []
            """;
        var second = """
            {"kind":"SourceRegistry","definition":{"sources":[],"publishers":[{"status":"Official","name":"alpha"},{"status":"Declared","name":"zed"}]},"metadata":{"name":"registry"},"apiVersion":"agentstration.io/v1"}
            """;

        var yaml = reader.Read(first, "registry.yaml");
        var json = reader.Read(second, "registry.json");

        Assert.AreEqual(yaml.RegistryDigest, json.RegistryDigest);
        CollectionAssert.AreEqual(yaml.CanonicalJson, json.CanonicalJson);
        Assert.AreEqual("{\"apiVersion\":\"agentstration.io/v1\",\"definition\":{\"publishers\":[{\"name\":\"alpha\",\"status\":\"Official\"},{\"name\":\"zed\",\"status\":\"Declared\"}],\"sources\":[]},\"kind\":\"SourceRegistry\",\"metadata\":{\"name\":\"registry\"}}", Encoding.UTF8.GetString(yaml.CanonicalJson));
    }

    [TestMethod]
    public void RegistryCanonicalizationUsesRfc8785StringEscaping()
    {
        var document = """
            {"apiVersion":"agentstration.io/v1","kind":"SourceRegistry","metadata":{"name":"registry","displayName":"\u000f\n€\\\""},"definition":{"publishers":[{"name":"agentstration","status":"Official"}],"sources":[]}}
            """;

        var parsed = new SourceRegistryReader().Read(document, "registry.json");
        var canonical = Encoding.UTF8.GetString(parsed.CanonicalJson);

        StringAssert.Contains(canonical, "\"displayName\":\"\\u000f\\n€\\\\\\\"\"");
        Assert.DoesNotContain("\\u000F", canonical, StringComparison.Ordinal);
    }

    [TestMethod]
    public void RegistryReferenceResolutionIsSameOriginBaseBoundAndDeterministic()
    {
        var baseUri = new Uri("https://EXAMPLE.test:443/catalog/");

        Assert.AreEqual("sources/source.yaml", SourceRegistryReferenceResolver.ResolvePublicationPath(baseUri, "sources/source.yaml"));
        Assert.AreEqual("sources/source.yaml", SourceRegistryReferenceResolver.ResolvePublicationPath(baseUri, "https://example.test/catalog/sources/source.yaml"));
        Assert.AreEqual("source_registry_manifest_path_invalid", Assert.ThrowsExactly<SourceValidationException>(
            () => SourceRegistryReferenceResolver.ResolvePublicationPath(baseUri, "https://example.test/catalog/a/../source.yaml")).Code);
        Assert.AreEqual("source_registry_manifest_path_invalid", Assert.ThrowsExactly<SourceValidationException>(
            () => SourceRegistryReferenceResolver.ResolvePublicationPath(baseUri, "https://example.test/source.yaml")).Code);
        Assert.AreEqual("source_registry_base_uri_invalid", Assert.ThrowsExactly<SourceValidationException>(
            () => SourceRegistryReferenceResolver.ResolvePublicationPath(new Uri("https://example.test/catalog"), "source.yaml")).Code);
    }

    [TestMethod]
    public void RegistryReaderEnforcesPublisherAndNfcLimits()
    {
        var publishers = string.Join(',', Enumerable.Range(0, SourceRegistryLimits.MaximumPublishers + 1)
            .Select(index => $"{{\"name\":\"publisher-{index}\",\"status\":\"Declared\"}}"));
        var excessive = $"{{\"apiVersion\":\"agentstration.io/v1\",\"kind\":\"SourceRegistry\",\"metadata\":{{\"name\":\"registry\"}},\"definition\":{{\"publishers\":[{publishers}],\"sources\":[]}}}}";
        var nonCanonicalUnicode = """
            {"apiVersion":"agentstration.io/v1","kind":"SourceRegistry","metadata":{"name":"registry","displayName":"e\u0301"},"definition":{"publishers":[{"name":"agentstration","status":"Official"}],"sources":[]}}
            """;

        var reader = new SourceRegistryReader();
        Assert.AreEqual("source_registry_publisher_limit", Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(excessive, "registry.json")).Code);
        Assert.AreEqual("source_registry_string_invalid", Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(nonCanonicalUnicode, "registry.json")).Code);
    }

    [TestMethod]
    public async Task RegistryBuildRejectsOverlappingOrNonEmptyOutputWithoutMutationAsync()
    {
        using var files = new TemporaryDirectory("registry output tests");
        var input = files.CreateDirectory("input");
        var source = Manifest();
        files.WriteRelative(input, "sources/agentstration/official-samples/1/source.yaml", source);
        var registryPath = files.WriteRelative(input, "registry.yaml", Registry(new SourceManifestReader().Read(source).Digest));
        var nonEmpty = files.CreateDirectory("non-empty-output");
        files.WriteRelative(nonEmpty, "keep.txt", "keep");

        var overlap = await RunAsync(RegistryArguments("build", registryPath, input, Path.Combine(input, "output")));
        var existing = await RunAsync(RegistryArguments("build", registryPath, input, nonEmpty));

        Assert.AreEqual(SourceRegistryCli.ValidationExitCode, overlap.ExitCode);
        StringAssert.Contains(overlap.Error, "source_registry_output_overlap");
        Assert.AreEqual(SourceRegistryCli.ValidationExitCode, existing.ExitCode);
        StringAssert.Contains(existing.Error, "source_registry_output_not_empty");
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(nonEmpty, "keep.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(input, "output")));
    }

    [TestMethod]
    public async Task RegistryValidationRejectsCaseCollidingOutputPathsAndSupportsCancellationAsync()
    {
        using var files = new TemporaryDirectory("registry collision tests");
        var input = files.CreateDirectory("input");
        var source = Manifest();
        files.WriteRelative(input, "sources/agentstration/official-samples/1/source.yaml", source);
        var digest = new SourceManifestReader().Read(source).Digest;
        var collisionRegistry = Registry(digest).Replace(
            $"manifestDigest: {digest}",
            $"manifestDigest: {digest}\n        - version: \"2\"\n          manifestUrl: sources/agentstration/official-samples/1/SOURCE.yaml\n          manifestDigest: {digest}",
            StringComparison.Ordinal);
        var registryPath = files.WriteRelative(input, "registry.yaml", collisionRegistry);

        var collision = await RunAsync(RegistryArguments("validate", registryPath, input));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await RunAsync(RegistryArguments("validate", registryPath, input), cancellation.Token);

        Assert.AreEqual(SourceRegistryCli.ValidationExitCode, collision.ExitCode);
        StringAssert.Contains(collision.Error, "source_registry_output_path_duplicate");
        Assert.AreEqual(SourceRegistryCli.CancelledExitCode, cancelled.ExitCode);
        StringAssert.Contains(cancelled.Error, "operation_cancelled");
    }

    [TestMethod]
    public async Task RegistryCommandRequiresTheNormativeOptionsAsync()
    {
        var result = await RunAsync(["registry", "validate", "registry.yaml"]);

        Assert.AreEqual(SourceRegistryCli.UsageExitCode, result.ExitCode);
        StringAssert.Contains(result.Error, "Usage:");
    }

    [TestMethod]
    public void RegistryIndexJsonAndYamlHaveTheSameCanonicalDigestAndOrder()
    {
        var digest = "sha256:" + new string('1', 64);
        var yaml = Index(("zed", "1.0.0", null, "registry-zed.json", digest), ("alpha", "0.2.0", "0.3.0", "registry-alpha.json", digest));
        var json = """
            {"metadata":{"name":"official"},"definition":{"catalogs":[{"registryDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","registryUrl":"registry-zed.json","compatibility":{"agentstration":{"minVersion":"1.0.0"}},"name":"zed"},{"name":"alpha","compatibility":{"agentstration":{"maxVersionExclusive":"0.3.0","minVersion":"0.2.0"}},"registryUrl":"registry-alpha.json","registryDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111"}]},"kind":"SourceRegistryIndex","apiVersion":"agentstration.io/v1"}
            """;
        var reader = new SourceRegistryIndexReader();

        var parsedYaml = reader.Read(yaml, "index.yaml", new Uri("https://example.test/v1/"));
        var parsedJson = reader.Read(json, "index.json", new Uri("https://example.test/v1/"));

        Assert.AreEqual(parsedYaml.IndexDigest, parsedJson.IndexDigest);
        CollectionAssert.AreEqual(parsedYaml.CanonicalJson, parsedJson.CanonicalJson);
        Assert.AreEqual("alpha", parsedYaml.Manifest.Definition.Catalogs[0].Name);
    }

    [TestMethod]
    public void RegistryIndexRejectsUnknownFieldsLimitsAndInvalidIntervals()
    {
        var digest = "sha256:" + new string('2', 64);
        var reader = new SourceRegistryIndexReader();
        var uri = new Uri("https://example.test/v1/");
        var unknown = Index(("alpha", "0.2.0", "0.3.0", "registry.json", digest)).Replace("definition:", "unknown: true\ndefinition:", StringComparison.Ordinal);
        var reversed = Index(("alpha", "0.3.0", "0.3.0", "registry.json", digest));
        var catalogs = Enumerable.Range(0, SourceRegistryLimits.MaximumCatalogs + 1)
            .Select(index => ($"catalog-{index}", "0.2.0", (string?)"0.3.0", $"registry-{index}.json", digest)).ToArray();

        Assert.AreEqual("source_registry_index_invalid", Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(unknown, "index.yaml", uri)).Code);
        Assert.AreEqual("source_registry_index_interval_invalid", Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(reversed, "index.yaml", uri)).Code);
        Assert.AreEqual("source_registry_index_catalog_limit", Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(Index(catalogs), "index.yaml", uri)).Code);
        Assert.AreEqual("source_registry_index_size_limit", Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(new string(' ', SourceRegistryLimits.MaximumIndexDocumentBytes + 1), "index.yaml", uri)).Code);
    }

    [TestMethod]
    public async Task RegistryIndexBuildsOverlappingShardsAndDeduplicatesSharedManifestAsync()
    {
        using var files = new TemporaryDirectory("registry index publication tests");
        var input = files.CreateDirectory("input");
        var manifest = Manifest(includeChannel: true);
        var manifestPath = files.WriteRelative(input, "sources/agentstration/official-samples/1/source.yaml", manifest);
        var manifestDigest = new SourceManifestReader().Read(manifest).Digest;
        var shardJson = Encoding.UTF8.GetString(new SourceRegistryReader().Read(Registry(manifestDigest), "registry.yaml").CanonicalJson);
        var shardDigest = new SourceRegistryReader().Read(shardJson, "registry.json").RegistryDigest;
        files.WriteRelative(input, "registry-a.json", shardJson);
        files.WriteRelative(input, "registry-b.json", shardJson);
        var indexPath = files.WriteRelative(input, "index.yaml", Index(
            ("line-a", "0.2.0-alpha.1", "0.3.0", "registry-a.json", shardDigest),
            ("line-b", "0.2.0", "0.4.0", "registry-b.json", shardDigest)));
        var output = Path.Combine(files.Path, "output");
        var secondOutput = Path.Combine(files.Path, "second-output");

        var validation = await RunAsync(RegistryArguments("validate", indexPath, input));
        var build = await RunAsync(RegistryArguments("build", indexPath, input, output));
        var secondBuild = await RunAsync(RegistryArguments("build", indexPath, input, secondOutput));

        Assert.AreEqual(SourceRegistryCli.SuccessExitCode, validation.ExitCode, validation.Error);
        StringAssert.Contains(validation.Output, "valid SourceRegistryIndex official");
        StringAssert.Contains(validation.Output, "shards=2 sources=1 versions=1");
        Assert.AreEqual(SourceRegistryCli.SuccessExitCode, build.ExitCode, build.Error);
        Assert.AreEqual(SourceRegistryCli.SuccessExitCode, secondBuild.ExitCode, secondBuild.Error);
        Assert.IsTrue(File.Exists(Path.Combine(output, "index.json")));
        Assert.IsTrue(File.Exists(Path.Combine(output, "index.sha256")));
        Assert.IsTrue(File.Exists(Path.Combine(output, "registry-a.json")));
        Assert.IsTrue(File.Exists(Path.Combine(output, "registry-a.sha256")));
        Assert.IsTrue(File.Exists(Path.Combine(output, "registry-b.json")));
        Assert.IsTrue(File.Exists(Path.Combine(output, "registry-b.sha256")));
        CollectionAssert.AreEqual(File.ReadAllBytes(manifestPath), File.ReadAllBytes(Path.Combine(output, "sources", "agentstration", "official-samples", "1", "source.yaml")));
        Assert.HasCount(7, Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).ToArray());
        var relativeFiles = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(output, path)).Order().ToArray();
        CollectionAssert.AreEqual(relativeFiles, Directory.EnumerateFiles(secondOutput, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(secondOutput, path)).Order().ToArray());
        foreach (var relativeFile in relativeFiles)
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(output, relativeFile)), File.ReadAllBytes(Path.Combine(secondOutput, relativeFile)));
    }

    [TestMethod]
    public async Task RegistryIndexRejectsExactBoundaryWithoutChannelIntersectionAsync()
    {
        using var files = new TemporaryDirectory("registry index compatibility tests");
        var input = files.CreateDirectory("input");
        var manifest = Manifest(includeChannel: true).Replace("minVersion: 0.2.0-alpha.1", "minVersion: 0.3.0", StringComparison.Ordinal);
        files.WriteRelative(input, "sources/agentstration/official-samples/1/source.yaml", manifest);
        var manifestDigest = new SourceManifestReader().Read(manifest).Digest;
        var shard = new SourceRegistryReader().Read(Registry(manifestDigest), "registry.yaml");
        files.WriteRelative(input, "registry-line.json", Encoding.UTF8.GetString(shard.CanonicalJson));
        var indexPath = files.WriteRelative(input, "index.yaml", Index(("line", "0.2.0", "0.3.0", "registry-line.json", shard.RegistryDigest)));

        var result = await RunAsync(RegistryArguments("validate", indexPath, input));

        Assert.AreEqual(SourceRegistryCli.ValidationExitCode, result.ExitCode);
        StringAssert.Contains(result.Error, "source_registry_shard_compatibility_missing");
    }

    [TestMethod]
    public async Task RegistryIndexRejectsMissingShardAndCanonicalDigestMismatchAsync()
    {
        using var files = new TemporaryDirectory("registry index shard integrity tests");
        var input = files.CreateDirectory("input");
        var wrongDigest = "sha256:" + new string('0', 64);
        var indexPath = files.WriteRelative(input, "index.yaml", Index(("line", "0.2.0", "0.3.0", "registry-line.json", wrongDigest)));

        var missing = await RunAsync(RegistryArguments("validate", indexPath, input));
        files.WriteRelative(input, "registry-line.json", Encoding.UTF8.GetString(new SourceRegistryReader().Read(Registry("sha256:" + new string('1', 64)), "registry.yaml").CanonicalJson));
        var mismatch = await RunAsync(RegistryArguments("validate", indexPath, input));

        Assert.AreEqual(SourceRegistryCli.ValidationExitCode, missing.ExitCode);
        StringAssert.Contains(missing.Error, "source_registry_index_registry_missing");
        Assert.AreEqual(SourceRegistryCli.ValidationExitCode, mismatch.ExitCode);
        StringAssert.Contains(mismatch.Error, "source_registry_index_digest_mismatch");
    }

    [TestMethod]
    public async Task RegistryIndexRejectsCrossShardDigestConflictsAsync()
    {
        using var files = new TemporaryDirectory("registry index conflict tests");
        var input = files.CreateDirectory("input");
        var firstManifest = Manifest(includeChannel: true);
        var secondManifest = firstManifest.Replace("Échantillon", "Autre échantillon", StringComparison.Ordinal);
        var firstDigest = new SourceManifestReader().Read(firstManifest).Digest;
        var secondDigest = new SourceManifestReader().Read(secondManifest).Digest;
        files.WriteRelative(input, "sources/a.yaml", firstManifest);
        files.WriteRelative(input, "sources/b.yaml", secondManifest);
        var firstShard = new SourceRegistryReader().Read(Registry(firstDigest, "sources/a.yaml"), "registry.yaml");
        var secondShard = new SourceRegistryReader().Read(Registry(secondDigest, "sources/b.yaml"), "registry.yaml");
        files.WriteRelative(input, "registry-a.json", Encoding.UTF8.GetString(firstShard.CanonicalJson));
        files.WriteRelative(input, "registry-b.json", Encoding.UTF8.GetString(secondShard.CanonicalJson));
        var indexPath = files.WriteRelative(input, "index.yaml", Index(
            ("a", "0.2.0", "0.3.0", "registry-a.json", firstShard.RegistryDigest),
            ("b", "0.2.0", "0.3.0", "registry-b.json", secondShard.RegistryDigest)));

        var result = await RunAsync(RegistryArguments("validate", indexPath, input));

        Assert.AreEqual(SourceRegistryCli.ValidationExitCode, result.ExitCode);
        StringAssert.Contains(result.Error, "source_registry_cross_shard_digest_conflict");
    }

    [TestMethod]
    public void RegistryUrlResolutionUsesSpecificSameOriginAndPathErrors()
    {
        var baseUri = new Uri("https://example.test/v1/");

        Assert.AreEqual("registry-a.json", SourceRegistryReferenceResolver.ResolveRegistryPublicationPath(baseUri, "registry-a.json"));
        Assert.AreEqual("registry-a.json", SourceRegistryReferenceResolver.ResolveRegistryPublicationPath(baseUri, "https://EXAMPLE.test:443/v1/registry-a.json"));
        Assert.AreEqual("source_registry_index_registry_origin_invalid", Assert.ThrowsExactly<SourceValidationException>(
            () => SourceRegistryReferenceResolver.ResolveRegistryPublicationPath(baseUri, "https://other.test/v1/registry-a.json")).Code);
        Assert.AreEqual("source_registry_index_registry_path_invalid", Assert.ThrowsExactly<SourceValidationException>(
            () => SourceRegistryReferenceResolver.ResolveRegistryPublicationPath(baseUri, "../registry-a.json")).Code);
    }

    [TestMethod]
    public void ToolAssemblyHasNoServerNetworkOrStorageDependency()
    {
        var references = typeof(SourceRegistryCli).Assembly.GetReferencedAssemblies().Select(value => value.Name).ToArray();
        Assert.IsFalse(references.Any(name => name is not null && (
            name.Contains("Agentstration.Management.Core", StringComparison.Ordinal)
            || name.Contains("Agentstration.Infrastructure", StringComparison.Ordinal)
            || name.Contains("Agentstration.Web", StringComparison.Ordinal)
            || name.Contains("Storage", StringComparison.Ordinal)
            || name.Contains("System.Net.Http", StringComparison.Ordinal))));
    }

    private static async Task<CommandResult> RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await SourceRegistryCli.RunAsync(arguments, output, error, cancellationToken);
        return new(exitCode, output.ToString(), error.ToString());
    }

    private static string[] RegistryArguments(string command, string registry, string root, string? output = null)
    {
        var arguments = new List<string>
        {
            "registry", command, registry,
            "--publication-root", root,
            "--base-uri", "https://example.test/"
        };
        if (output is not null)
        {
            arguments.Add("--output");
            arguments.Add(output);
        }
        return [.. arguments];
    }

    private static string Registry(string digest, string manifestUrl = "sources/agentstration/official-samples/1/source.yaml") => $$"""
        apiVersion: agentstration.io/v1
        kind: SourceRegistry
        metadata:
          name: agentstration-official
          displayName: Agentstration Registry
        definition:
          publishers:
            - name: agentstration
              displayName: Agentstration
              url: https://www.agentstration.io
              status: Official
          sources:
            - publisher: agentstration
              name: official-samples
              displayName: Agentstration Samples
              description: Reusable samples
              latest: "1"
              versions:
                - version: "1"
                  manifestUrl: {{manifestUrl}}
                  manifestDigest: {{digest}}
        """;

    private static string Index(params (string Name, string Min, string? Max, string Url, string Digest)[] catalogs)
    {
        var result = new StringBuilder("apiVersion: agentstration.io/v1\nkind: SourceRegistryIndex\nmetadata:\n  name: official\ndefinition:\n  catalogs:\n");
        foreach (var catalog in catalogs)
        {
            result.Append("    - name: ").Append(catalog.Name).Append("\n      compatibility:\n        agentstration:\n          minVersion: ").Append(catalog.Min).Append('\n');
            if (catalog.Max is not null) result.Append("          maxVersionExclusive: ").Append(catalog.Max).Append('\n');
            result.Append("      registryUrl: ").Append(catalog.Url).Append("\n      registryDigest: ").Append(catalog.Digest).Append('\n');
        }
        return result.ToString();
    }

    private static string Manifest(bool includeChannel = false) => $$"""
        apiVersion: agentstration.io/v1
        kind: SourceVersion
        metadata:
          name: official-samples
        definition:
          version: "1"
          displayName: "Échantillon"
          description: null
          publisher:
            name: agentstration
          bindings:{{(includeChannel ? "\n    - name: distribution\n      targetKind: sourceProvider" : " []")}}
          channels:{{(includeChannel ? "\n    - name: stable\n      compatibility:\n        agentstration:\n          minVersion: 0.2.0-alpha.1\n      provider:\n        binding: distribution\n      configuration:\n        optionSet: git/source-channel\n        version: \"1.0\"\n        schemaDigest: sha256:example\n        values:\n          enabled: true\n          retries: 3\n          ratio: 0.5\n          label: sample\n          empty: null\n          items: [one, two]" : " []")}}
          catalogs: []
        """;

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string name)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(string relativePath)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string Write(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public string WriteBytes(string name, byte[] content)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public string WriteRelative(string root, string relativePath, string content)
        {
            var path = System.IO.Path.Combine(root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
