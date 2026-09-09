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
