using System.Text;
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
    public async Task RegistryCommandsRemainUnavailableUntilTheContractIsFinalizedAsync()
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

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
